using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Level5.AudioTool.Core.Hca;

namespace Level5.AudioTool.Core.Cri;

internal static class CriAudioCommands
{
    internal static object Run(CriArguments args)
    {
        var tools = new CriAudioTools(args);
        return args.Command switch
        {
            "wav-info" => AudioInfo(args.Position(0), tools),
            "clip-audio" => ClipAudio(args, tools),
            "loop-audio" => LoopAudio(args, tools),
            "ensure-plugins" => EnsurePlugins(args, tools),
            "preview-awb-entry" => Preview(args, tools),
            "replace-awb-wav" => ReplaceWav(args, tools),
            _ => throw new ArgumentException($"Unknown audio command: {args.Command}"),
        };
    }

    private static Dictionary<string, object?> AudioInfo(string path, CriAudioTools tools)
    {
        try { return CriAudioWav.Read(path).Info(path); }
        catch (InvalidDataException)
        {
            var ffmpeg = tools.Ffmpeg(download: true);
            using var work = new CriAudioTemp(Path.GetTempPath());
            var prepared = Path.Combine(work.Path, "analysis.wav");
            Transcode(ffmpeg.Path, path, prepared, null, null);
            var info = CriAudioWav.Read(prepared).Info(path);
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            info["source_format"] = extension.Length == 0 ? "unknown" : extension;
            info["normalized_for_analysis"] = true;
            info["ffmpeg"] = ToolInfo(ffmpeg);
            info["loop"] = null;
            return info;
        }
    }

    private static object ClipAudio(CriArguments args, CriAudioTools tools)
    {
        string source = args.Position(0), output = args.Require("output");
        int rate = Number(args, "sample-rate"), start = Number(args, "start-sample"), end = Number(args, "end-sample");
        if (rate <= 0 || start < 0 || end <= start)
            throw new ArgumentOutOfRangeException(nameof(args), "Invalid clip range or sample rate.");
        var ffmpeg = tools.Ffmpeg(download: true);
        PrepareParent(output);
        CriAudioTools.Execute(ffmpeg.Path, ["-y", "-hide_banner", "-loglevel", "error",
            "-ss", ((double)start / rate).ToString("F9", CultureInfo.InvariantCulture), "-i", source,
            "-t", ((double)(end - start) / rate).ToString("F9", CultureInfo.InvariantCulture),
            "-vn", "-acodec", "pcm_s16le", output], -1);
        return new { source, output, start_sample = start, end_sample = end, sample_rate = rate, ffmpeg = ToolInfo(ffmpeg) };
    }

    private static object LoopAudio(CriArguments args, CriAudioTools tools)
    {
        string source = args.Position(0), output = args.Require("output");
        int rate = Number(args, "sample-rate"), play = Number(args, "play-start"),
            start = Number(args, "loop-start"), end = Number(args, "loop-end");
        if (rate <= 0 || play < 0 || start < 0 || end <= start || play >= end)
            throw new ArgumentOutOfRangeException(nameof(args), "Invalid loop range or sample rate.");
        var ffmpeg = tools.Ffmpeg(download: true);
        using var work = new CriAudioTemp(Path.GetTempPath());
        var path = Path.Combine(work.Path, "source.wav");
        Transcode(ffmpeg.Path, source, path, rate, null);
        var wav = CriAudioWav.Read(path);
        int clipPlay = Math.Min(play, Math.Max(0, wav.FrameCount - 1));
        int clipStart = Math.Min(start, Math.Max(0, wav.FrameCount - 1));
        int clipEnd = Math.Min(end, wav.FrameCount);
        if (clipEnd <= clipStart || clipPlay >= clipEnd)
            throw new InvalidDataException("Loop range is outside the decoded audio.");
        var duration = double.Parse(args.Get("duration-seconds") ?? "300", CultureInfo.InvariantCulture);
        var samples = Math.Max(checked(2 * clipEnd - clipPlay - clipStart),
            checked((int)(Math.Max(1d, duration) * rate)));
        int written = wav.WriteLoop(output, clipPlay, clipStart, clipEnd, samples);
        return new { source, output, play_start = play, loop_start = start, loop_end = end,
            sample_rate = rate, sample_count = written, ffmpeg = ToolInfo(ffmpeg) };
    }

    private static object EnsurePlugins(CriArguments args, CriAudioTools tools)
    {
        var checks = new List<object>();
        try
        {
            var vgm = tools.Vgmstream(download: !args.Has("no-vgmstream-download"));
            checks.Add(new { name = "vgmstream-cli", available = vgm.Path is not null, path = vgm.Path, source = vgm.Source });
        }
        catch (Exception e) when (e is IOException or HttpRequestException or PlatformNotSupportedException or UnauthorizedAccessException or JsonException)
        {
            checks.Add(new { name = "vgmstream-cli", available = false, path = (string?)null, source = $"download-failed: {e.Message}" });
        }
        try
        {
            var ffmpeg = tools.Ffmpeg(download: !args.Has("no-ffmpeg-download"));
            checks.Add(new { name = "ffmpeg", available = true, path = ffmpeg.Path, source = ffmpeg.Source });
        }
        catch (Exception e) when (e is IOException or HttpRequestException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            checks.Add(new { name = "ffmpeg", available = false, path = (string?)null, source = "missing", error = e.Message });
        }
        return new { checks };
    }

    private static object Preview(CriArguments args, CriAudioTools tools)
    {
        string source = args.Position(0);
        var bank = Afs2Bank.Read(CriFiles.Read(source));
        var (index, entry) = Select(bank, args.Int("id"), args.Int("index"));
        var acbEntry = args.Get("acb") is { } acb ? CriAcbCommands.EntryInfo(CriFiles.Read(acb), entry.Id) : null;
        var sha1 = Convert.ToHexString(SHA1.HashData(entry.Data)).ToLowerInvariant();
        Directory.CreateDirectory(args.Require("output"));
        var cache = Directory.CreateDirectory(Path.Combine(args.DataRoot, ".cache", "previews", sha1)).FullName;
        var entryPath = Path.Combine(cache, "entry" + entry.Extension);
        var wavPath = Path.Combine(cache, "preview.wav");
        var infoPath = Path.Combine(cache, "preview.info.json");
        bool cached = File.Exists(wavPath) && new FileInfo(wavPath).Length > 0;
        string decoder = cached ? "cache" : "copy";
        if (!cached)
        {
            CriFiles.Write(entryPath, entry.Data);
            using var work = new CriAudioTemp(cache);
            var decoded = Path.Combine(work.Path, "preview.wav");
            if (entry.Extension == ".wav")
                File.Copy(entryPath, decoded);
            else
            {
                if (entry.Extension is not (".hca" or ".adx"))
                    throw new InvalidDataException($"Preview is not supported for AWB entry type {entry.Extension}.");
                var vgm = tools.Vgmstream(download: false);
                if (vgm.Path is not null)
                {
                    try
                    {
                        CriAudioTools.Execute(vgm.Path, ["-o", decoded, entryPath], -1);
                        if (File.Exists(decoded) && new FileInfo(decoded).Length > 0)
                            decoder = "vgmstream";
                    }
                    catch (Exception e) when (e is IOException or Win32Exception)
                    {
                        File.Delete(decoded);
                    }
                }
                if (decoder != "vgmstream")
                {
                    File.Delete(decoded);
                    bool hca = entry.Extension == ".hca";
                    tools.Hca(hca ? "decode" : "decode-adx", entryPath, decoded);
                    decoder = hca ? "cri-hca-tool" : "cri-hca-tool-adx";
                }
            }
            var decodedInfo = CriAudioWav.Read(decoded).Info(wavPath);
            File.Move(decoded, wavPath, overwrite: true);
            CriFiles.WriteJson(infoPath, decodedInfo);
        }
        object metadata = File.Exists(infoPath) ?
            JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(infoPath)) : CriAudioWav.Read(wavPath).Info(wavPath);
        return new { source, entry = new { index, id = entry.Id, extension = entry.Extension, size = entry.Data.Length, sha1 },
            extracted = entryPath, wav = wavPath, decoder, acb_entry = acbEntry, cached, wav_info = metadata,
            loop = acbEntry?.GetValueOrDefault("loop"), sample_count = acbEntry?.GetValueOrDefault("sample_count"),
            sample_rate = acbEntry?.GetValueOrDefault("sample_rate") };
    }

    private static string ReplaceWav(CriArguments args, CriAudioTools tools)
    {
        string source = args.Position(0), target = args.Position(1), input = args.Position(2);
        int? id = args.Int("id"), requestedIndex = args.Int("index");
        if (id.HasValue == requestedIndex.HasValue)
            throw new ArgumentException("Use exactly one of --id or --index.");
        if (!File.Exists(input))
            throw new FileNotFoundException("Replacement audio not found.", input);
        string codec = args.Get("codec") ?? "HCA";
        if (codec is not ("HCA" or "ADX"))
            throw new ArgumentException($"Unsupported replacement codec {codec}.");
        bool hca = codec == "HCA";
        var archive = Afs2Bank.Read(CriFiles.Read(source));
        var (index, original) = Select(archive, id, requestedIndex);

        var targetDir = PrepareParent(target);
        using var work = new CriAudioTemp(targetDir);
        var originalPath = Path.Combine(work.Path, $"original_{index:D4}_{original.Id:D5}{original.Extension}");
        File.WriteAllBytes(originalPath, original.Data);
        JsonObject originalInfo = original.Extension == ".hca" ? InspectHca(tools, originalPath) : new();
        var originalBody = originalInfo["Hca"] as JsonObject ?? new();
        var ffmpeg = tools.Ffmpeg(download: !args.Has("no-ffmpeg-download"));
        string prepared = Path.Combine(work.Path, Path.GetFileNameWithoutExtension(input) + ".prepared.wav");
        int targetRate = Value(originalBody, "SampleRate") ?? 48000;
        int targetChannels = Value(originalBody, "ChannelCount") ?? 1;
        Transcode(ffmpeg.Path, input, prepared, targetRate, targetChannels);

        var preparedWav = CriAudioWav.Read(prepared);
        var preparedInfo = preparedWav.Info(prepared);
        preparedInfo["target_sample_rate"] = targetRate;
        preparedInfo["target_channels"] = targetChannels;
        Dictionary<string, object?> inputInfo;
        try { inputInfo = AudioInfo(input, tools); }
        catch (IOException) when (!args.Has("loop-start") && !args.Has("loop-end"))
        {
            inputInfo = new() { ["path"] = input, ["loop"] = null };
        }
        var (loopStart, loopEnd, loopSource) = ResolveLoop(args, input, inputInfo, preparedWav);
        var encoded = Path.Combine(work.Path, Path.GetFileNameWithoutExtension(input) + (hca ? ".hca" : ".adx"));
        int? estimate = EstimateBitrate(original.Data.Length, originalBody);
        int? bitrate = hca ? args.Int("bitrate") is int selected && selected != 0 ? selected : estimate : null;
        var command = new List<string> { hca ? "encode" : "encode-adx", prepared, encoded };
        if (hca)
            command.AddRange(["--quality", args.Get("quality") ?? "High"]);
        if (bitrate.HasValue)
            command.AddRange(["--bitrate", bitrate.Value.ToString(CultureInfo.InvariantCulture)]);
        if (args.Get("key") is { Length: > 0 } key)
            command.AddRange(["--key", key]);
        if (hca && loopStart.HasValue && loopEnd.HasValue)
        {
            loopStart = loopEnd = null;
            loopSource = "disabled-hca-loop-preserve-duration";
        }
        if (loopStart.HasValue && loopEnd.HasValue)
            command.AddRange(["--loop-start", loopStart.Value.ToString(CultureInfo.InvariantCulture),
                "--loop-end", loopEnd.Value.ToString(CultureInfo.InvariantCulture)]);
        else
            command.Add("--no-loop");

        var rawEncode = tools.Hca(command.ToArray());
        JsonObject encodeReport;
        try { encodeReport = ReadJson(rawEncode); }
        catch (JsonException) { encodeReport = new() { ["raw_stdout"] = rawEncode }; }

        bool normalized = false, matched = false;
        if (hca)
        {
            var newInfo = InspectHca(tools, encoded);
            if (args.Has("match-original-hca-profile"))
            {
                matched = MatchHcaProfile(encoded, originalInfo, originalBody);
                if (matched)
                {
                    newInfo = InspectHca(tools, encoded);
                    encodeReport["matchedOriginalHcaProfile"] = true;
                }
            }
            var newBody = newInfo["Hca"] as JsonObject ?? new();
            if (Value(originalBody, "EncryptionType").GetValueOrDefault() == 0 && Value(newBody, "EncryptionType") == 1)
            {
                normalized = HcaType1Uncipher.TryConvertToPlain(File.ReadAllBytes(encoded), out var plain);
                if (normalized)
                {
                    File.WriteAllBytes(encoded, plain);
                    newInfo = InspectHca(tools, encoded);
                    newBody = newInfo["Hca"] as JsonObject ?? new();
                    encodeReport["normalizedHcaCiph"] = "type1-to-plain";
                }
            }
            encodeReport["hcaVersion"] = newInfo["Version"]?.DeepClone();
            encodeReport["hcaEncryptionType"] = newBody["EncryptionType"]?.DeepClone();
            encodeReport["minResolution"] = newBody["MinResolution"]?.DeepClone();
        }

        byte[] replacement = File.ReadAllBytes(encoded);
        if (args.Has("keep-hca"))
            CriFiles.Write(target + ".replacement.hca", replacement);
        var newEntry = original with { Data = replacement };
        var replaced = new List<object>();
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            var current = archive.Entries[i];
            if (id.HasValue ? current.Id != original.Id : i != index)
                continue;
            archive.Entries[i] = current with { Data = replacement };
            replaced.Add(new { index = i, id = current.Id, old_size = current.Data.Length,
                new_size = replacement.Length, source = encoded, extension = newEntry.Extension });
        }
        byte[] output = archive.Write();
        CriFiles.Write(target, output);
        CriFiles.WriteJson(target + ".replace-report.json", new
        {
            source, target, replaced, result = CriCommands.Metadata(Afs2Bank.Read(output), null)
        });
        int effective = Value(encodeReport, "hcaSampleCount") ?? preparedWav.FrameCount;
        CriFiles.WriteJson(target + ".wav-replace-report.json", new
        {
            source_awb = source, target_awb = target, input_audio = input, codec, encoder = "CriHcaTool",
            prepared_by_ffmpeg = prepared, ffmpeg = ToolInfo(ffmpeg), selector = new { id, index = requestedIndex },
            original_entry = new { index, id = original.Id, size = original.Data.Length, extension = original.Extension,
                hca = originalBody, estimated_bitrate = estimate },
            input_audio_info = inputInfo, prepared_wav_info = preparedInfo, loop_source = loopSource,
            loop_start = loopStart, loop_end = loopEnd, normalized_hca_ciph = normalized, matched_hca_profile = matched,
            effective_sample_count = effective, bitrate, checks = Checks(original.Data.Length, originalInfo, originalBody, inputInfo,
                preparedWav, encodeReport, effective, loopStart, loopEnd, loopSource), hca = encodeReport
        });
        return $"Wrote {target} with {replaced.Count} replacement(s)";
    }

    private static List<object> Checks(int originalSize, JsonObject originalInfo, JsonObject original, Dictionary<string, object?> input,
        CriAudioWav prepared, JsonObject report, int effective, int? loopStart, int? loopEnd, string loopSource)
    {
        int? inputRate = input.GetValueOrDefault("sample_rate") as int?;
        int? inputChannels = input.GetValueOrDefault("channels") as int?;
        var checks = new List<object>
        {
            new { name = "sample_rate", status = inputRate == prepared.SampleRate ? "ok" : "fixed",
                original_hca = original["SampleRate"], input = inputRate, prepared = prepared.SampleRate },
            new { name = "channels", status = inputChannels == prepared.Channels ? "ok" : "fixed",
                original_hca = original["ChannelCount"], input = inputChannels, prepared = prepared.Channels },
            new { name = "loop_range", status = loopStart is null || (loopStart >= 0 && loopEnd > loopStart && loopEnd <= prepared.FrameCount)
                ? "ok" : "warning", loop_start = loopStart, loop_end = loopEnd, prepared_samples = prepared.FrameCount },
        };
        if (loopSource == "disabled-hca-loop-preserve-duration")
            checks.Add(new { name = "hca_loop", status = "warning",
                reason = "HCA loop metadata was not written because the current encoder trims the file to LoopEnd." });
        int originalSamples = Value(original, "SampleCount").GetValueOrDefault();
        double? ratio = originalSamples > 0 ? (double)effective / originalSamples : null;
        checks.Add(new { name = "duration", status = ratio is < .75 or > 1.25 ? "warning" : "ok",
            original_samples = originalSamples, new_samples = effective,
            ratio = ratio.HasValue ? Math.Round(ratio.Value, 4) : (double?)null });
        bool originalLoop = original["Looping"]?.GetValue<bool>() ?? false, newLoop = loopStart.HasValue && loopEnd.HasValue;
        checks.Add(new { name = "loop_semantics", status = originalLoop == newLoop ? "ok" : "warning",
            original_looping = originalLoop, new_looping = newLoop, original_loop_start = original["LoopStartSample"],
            original_loop_end = original["LoopEndSample"], new_loop_start = loopStart, new_loop_end = loopEnd });
        long? newSize = report["outputSize"]?.GetValue<long>();
        double? sizeRatio = originalSize > 0 && newSize.HasValue ? (double)newSize.Value / originalSize : null;
        checks.Add(new { name = "awb_entry_size", status = sizeRatio is < .5 or > 1.5 ? "warning" : "changed",
            original_bytes = originalSize, new_hca_bytes = newSize });
        if (report["hcaVersion"] is { } version)
            checks.Add(new { name = "hca_version", status = Value(originalInfo, "Version").GetValueOrDefault() == version.GetValue<int>() ?
                "ok" : "warning", original_version = originalInfo["Version"], new_version = version });
        checks.Add(new { name = "hca_encryption", status = Value(original, "EncryptionType").GetValueOrDefault() == 0 ? "ok" : "warning",
            original_encryption_type = original["EncryptionType"], new_encrypted = report["encrypted"] });
        return checks;
    }

    private static (int? Start, int? End, string Source) ResolveLoop(CriArguments args, string path,
        Dictionary<string, object?> input, CriAudioWav prepared)
    {
        if (args.Has("no-loop"))
            return (null, null, "none");
        int? start = args.Int("loop-start"), end = args.Int("loop-end");
        int inputCount = input.GetValueOrDefault("sample_count") as int? ?? 0;
        string source;
        if (start.HasValue || end.HasValue)
        {
            if (!start.HasValue || !end.HasValue)
                throw new ArgumentException("Both --loop-start and --loop-end are required.");
            source = "explicit";
        }
        else
        {
            if (args.Get("loop-mode") is "none")
                return (null, null, "none");
            try
            {
                if (CriAudioWav.Read(path).Loop is not { } loop)
                    return (null, null, "none");
                (start, end) = loop;
            }
            catch (InvalidDataException) { return (null, null, "none"); }
            source = "wav-smpl";
        }
        if (start < 0 || end <= start || end > inputCount)
        {
            if (source == "wav-smpl") return (null, null, "none");
            throw new ArgumentOutOfRangeException(nameof(args), $"Invalid loop range {start}..{end} for {inputCount} samples.");
        }
        int inputRate = input.GetValueOrDefault("sample_rate") as int? ?? prepared.SampleRate;
        if (inputRate > 0 && prepared.SampleRate != inputRate)
        {
            start = checked((int)Math.Round((double)start!.Value * prepared.SampleRate / inputRate));
            end = checked((int)Math.Round((double)end!.Value * prepared.SampleRate / inputRate));
            source += "-scaled";
        }
        start = Math.Max(0, Math.Min(start!.Value, prepared.FrameCount - 1));
        end = Math.Max(start.Value + 1, Math.Min(end!.Value, prepared.FrameCount));
        return (start, end, source);
    }

    private static int? EstimateBitrate(int size, JsonObject metadata)
    {
        int rate = Value(metadata, "SampleRate").GetValueOrDefault(), count = Value(metadata, "SampleCount").GetValueOrDefault();
        return rate > 0 && count > 0 ? Math.Max(24000, checked((int)Math.Round(size * 8d * rate / count))) : null;
    }

    private static bool MatchHcaProfile(string path, JsonObject info, JsonObject body)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8 || !bytes.AsSpan().StartsWith("HCA\0"u8))
            return false;
        bool changed = false;
        if (Value(info, "Version") is int version && version > 0 && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4)) != version)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), checked((ushort)version));
            changed = true;
        }
        if (Value(body, "MinResolution") is int resolution && resolution is >= 0 and <= byte.MaxValue)
        {
            int header = Math.Min(bytes.Length, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6)));
            var chunk = bytes.AsSpan(8, Math.Max(0, header - 8)).IndexOf("comp"u8);
            if (chunk >= 0 && chunk + 14 < header && bytes[chunk + 14] != resolution)
            {
                bytes[chunk + 14] = (byte)resolution;
                changed = true;
            }
        }
        if (changed)
            File.WriteAllBytes(path, bytes);
        return changed;
    }

    private static JsonObject InspectHca(CriAudioTools tools, string path) => ReadJson(tools.Hca("inspect", path));

    private static JsonObject ReadJson(string output)
    {
        int start = output.IndexOf('{'), end = output.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new JsonException("The HCA helper returned no metadata.");
        return JsonNode.Parse(output[start..(end + 1)])?.AsObject() ?? new();
    }

    private static int? Value(JsonObject obj, string name) => obj[name]?.GetValue<int>();

    private static void Transcode(string executable, string source, string output, int? rate, int? channels)
    {
        var command = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", source, "-vn", "-acodec", "pcm_s16le" };
        if (rate.HasValue) command.AddRange(["-ar", rate.Value.ToString(CultureInfo.InvariantCulture)]);
        if (channels.HasValue) command.AddRange(["-ac", channels.Value.ToString(CultureInfo.InvariantCulture)]);
        command.Add(output);
        CriAudioTools.Execute(executable, command, -1);
    }

    private static string PrepareParent(string path) => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!).FullName;
    private static int Number(CriArguments args, string name) => CriArguments.ParseInteger(args.Require(name));
    private static object ToolInfo((string Path, string Source) value) => new { path = value.Path, source = value.Source };

    private static (int Index, Afs2Entry Entry) Select(Afs2Bank bank, int? id, int? index)
    {
        for (int i = 0; i < bank.Entries.Count; i++)
            if (id.HasValue && bank.Entries[i].Id == unchecked((uint)id.Value) || index == i)
                return (i, bank.Entries[i]);
        throw new InvalidDataException($"No AWB entry found for id={id} index={index}.");
    }
}
