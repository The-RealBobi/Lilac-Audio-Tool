using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Level5.AudioTool.Core.Cri;

internal static partial class CriAcbCommands
{
    internal static object Run(CriArguments args) => args.Command switch
    {
        "patch-acb-waveform" => PatchWaveform(args),
        "patch-acb-stream-awb" => PatchStreamAwb(args),
        "rename-acb-cue" => EditCueName(args, delete: false),
        "delete-acb-cue" => EditCueName(args, delete: true),
        "add-acb-awb-hca" => AddOrRedirectCue(args, redirect: false),
        "redirect-acb-cue-awb-hca" => AddOrRedirectCue(args, redirect: true),
        _ => throw new InvalidOperationException($"Unsupported ACB command: {args.Command}")
    };

    private static object PatchWaveform(CriArguments args)
    {
        var source = args.Position(0);
        var target = args.Position(1);
        uint id = AwbId(args, "id");
        var wav = args.Get("wav") is { } wavPath ? CriAudioWav.Read(wavPath) : null;
        int samples = args.Int("samples") ?? wav?.FrameCount ?? throw new InvalidDataException("Use --samples or --wav.");
        int? loopStart = args.Int("loop-start"), loopEnd = args.Int("loop-end");
        string loopSource = loopStart.HasValue ? "explicit" : "none";
        if (wav is not null && args.Has("no-loop"))
        {
            loopStart = loopEnd = null;
            loopSource = "none";
        }
        if (wav is not null && loopStart is null && loopEnd is null && !args.Has("no-loop") &&
            args.Get("loop-mode") != "none" && wav.Loop is { } loop)
        {
            loopStart = loop.Start;
            loopEnd = loop.End;
            loopSource = "wav-smpl";
        }
        if (samples <= 0)
            throw new InvalidDataException("Sample count must be positive.");
        if (loopStart.HasValue != loopEnd.HasValue)
            throw new InvalidDataException("Both --loop-start and --loop-end are required.");
        if (loopStart is < 0 || loopEnd <= loopStart || loopEnd > samples || (wav is not null && loopEnd > wav.FrameCount))
            throw new InvalidDataException($"Invalid loop range {loopStart}..{loopEnd} for {samples} samples.");

        byte[] data = CriFiles.Read(source);
        var tables = UtfTable.Read(data).NestedTables();
        var waveform = RequireTable(tables, "WaveformTable");
        var extensions = tables.GetValueOrDefault("WaveformExtensionDataTable");
        bool memory = args.Has("memory");
        bool patchCueLengths = args.Has("patch-cue-lengths");
        string idName = memory ? "MemoryAwbId" : "StreamAwbId";
        int encodeType = args.Int("encode-type", 2);
        int? channels = args.Int("channels"), samplingRate = args.Int("sampling-rate"), chConfig = args.Int("ch-config");
        var changedWaveforms = new List<int>();
        var oldDurations = new Dictionary<int, long>();
        var newDurations = new Dictionary<int, long>();
        for (int i = 0; i < waveform.Rows.Count; i++)
        {
            var row = waveform.Rows[i];
            if (Number(row, idName) != id || (!memory && Number(row, "Streaming") != 1))
                continue;
            if (!UtfField.PatchInteger(data, row, "NumSamples", samples))
                throw new InvalidDataException($"Waveform row {i} has no patchable NumSamples field.");
            if (WaveformDuration(row) is long duration)
                oldDurations[i] = duration;
            UtfField.PatchInteger(data, row, "EncodeType", encodeType);
            if (channels is int ch) UtfField.PatchInteger(data, row, "NumChannels", ch);
            if (samplingRate is int sr) UtfField.PatchInteger(data, row, "SamplingRate", sr);
            if (chConfig is int cc) UtfField.PatchInteger(data, row, "ChConfig", cc);
            UtfField.PatchInteger(data, row, "LoopFlag", loopStart.HasValue ? 2 : 1);
            if (loopStart is int start && loopEnd is int end &&
                Index(row, "ExtensionData") is int extension && extension != ushort.MaxValue &&
                RowAt(extensions, extension) is { } ext)
            {
                UtfField.PatchInteger(data, ext, "LoopStart", start);
                UtfField.PatchInteger(data, ext, "LoopEnd", end);
            }
            long rate = samplingRate.GetValueOrDefault() != 0 ? samplingRate!.Value : Number(row, "SamplingRate") ?? 48000;
            if (rate == 0) rate = 48000;
            newDurations.Add(i, Duration(samples, Math.Max(1, rate)));
            changedWaveforms.Add(i);
        }
        if (changedWaveforms.Count == 0)
            throw new InvalidDataException($"No waveform row found for {idName} {id}.");

        var changedCues = new List<int>();
        if (patchCueLengths && tables.TryGetValue("CueTable", out var cues))
            for (int i = 0; i < cues.Rows.Count; i++)
            {
                var row = cues.Rows[i];
                var indices = ResolveCueWaveforms(row, tables);
                if (!indices.Any(newDurations.ContainsKey) || Number(row, "Length") is not long oldLength ||
                    oldLength == uint.MaxValue)
                    continue;
                long[] before = indices.Where(oldDurations.ContainsKey).Select(index => oldDurations[index]).ToArray();
                long[] after = indices.Select(index => newDurations.TryGetValue(index, out long value)
                        ? value : WaveformDuration(waveform.Rows[index]))
                    .OfType<long>().ToArray();
                if (before.Length != 0 && after.Length != 0 &&
                    (indices.Length == 1 || Math.Abs(oldLength - before.Max()) <= 2) &&
                    UtfField.PatchInteger(data, row, "Length", after.Max()))
                    changedCues.Add(i);
            }

        CriFiles.Write(target, data);
        var report = new
        {
            source, target, awb_id = id, archive = memory ? "memory" : "stream", wav_or_input_sample_count = samples,
            effective_sample_count = samples, loop_source = loopSource, loop_start = loopStart, loop_end = loopEnd,
            encode_type = encodeType, channels, sampling_rate = samplingRate, ch_config = chConfig,
            changed_waveform_rows = changedWaveforms, changed_cue_rows = changedCues, patch_cue_lengths = patchCueLengths
        };
        CriFiles.WriteJson(target + ".patch-report.json", report);
        return report;
    }

    private static object PatchStreamAwb(CriArguments args)
    {
        var source = args.Position(0);
        var target = args.Position(1);
        var awb = args.Require("awb");
        var awbName = args.Get("name") ?? Path.GetFileNameWithoutExtension(awb);
        byte[] originalAwb = CriFiles.Read(awb);
        _ = Afs2Bank.Read(originalAwb);
        byte[] awbData = originalAwb;
        byte[] acbData = CriFiles.Read(source);
        byte[]? marker = null;
        if (args.Get("provenance") is { Length: > 0 } provenance)
        {
            if (!provenance.StartsWith("LAT_v", StringComparison.Ordinal) ||
                provenance.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-')))
                throw new InvalidDataException("Invalid export provenance.");
            marker = new byte[checked((provenance.Length + 15) & ~15)];
            Encoding.ASCII.GetBytes(provenance, marker);
            if (BinaryPrimitives.ReadUInt32BigEndian(acbData.AsSpan(4)) != acbData.Length - 8)
                throw new InvalidDataException("ACB UTF size does not match the exported bank.");
            if (!awbData.AsSpan().EndsWith(marker))
                awbData = [.. awbData, .. marker];
        }
        byte[] hash = MD5.HashData(awbData);
        var (hashRows, headerRows) = PatchStreamReferences(acbData, awbData, hash, awbName);
        if (marker is not null && !acbData.AsSpan().EndsWith(marker))
        {
            acbData = [.. acbData, .. marker];
            BinaryPrimitives.WriteUInt32BigEndian(acbData.AsSpan(4), (uint)(acbData.Length - 8));
        }

        byte[]? awbBackup = ReferenceEquals(awbData, originalAwb) ? null : File.ReadAllBytes(awb);
        if (awbBackup is not null) CriFiles.Write(awb, awbData);
        try
        {
            CriFiles.Write(target, acbData);
        }
        catch (IOException) when (awbBackup is not null)
        {
            CriFiles.Write(awb, awbBackup);
            throw;
        }
        catch (UnauthorizedAccessException) when (awbBackup is not null)
        {
            CriFiles.Write(awb, awbBackup);
            throw;
        }
        var report = new
        {
            source, target, awb, awb_name = awbName, awb_md5 = Convert.ToHexString(hash).ToLowerInvariant(),
            changed_hash_rows = hashRows, changed_header_rows = headerRows
        };
        CriFiles.WriteJson(target + ".stream-awb-report.json", report);
        return report;
    }

    private static (List<int> HashRows, List<int> HeaderRows) PatchStreamReferences(
        byte[] acb, byte[] awb, byte[] hash, string? awbName)
    {
        var tables = UtfTable.Read(acb).NestedTables();
        var hashRows = new List<int>();
        var headerRows = new List<int>();
        if (tables.TryGetValue("StreamAwbHash", out var hashes))
        {
            for (int i = 0; i < hashes.Rows.Count; i++)
            {
                var row = hashes.Rows[i];
                if (awbName is not null && Field(row, "Name")?.Value is string { Length: > 0 } name && name != awbName)
                    continue;
                if (Field(row, "Hash") is not { Value: byte[] existing } field)
                    continue;
                if (existing.Length != hash.Length)
                {
                    if (awbName is not null)
                        throw new InvalidDataException($"StreamAwbHash.Hash has unexpected size {existing.Length}.");
                    continue;
                }
                hash.CopyTo(acb.AsSpan(field.Offset, hash.Length));
                hashRows.Add(i);
            }
            if (awbName is not null && hashRows.Count == 0)
                throw new InvalidDataException($"No StreamAwbHash row matched AWB name '{awbName}'.");
        }
        if (tables.TryGetValue("StreamAwbAfs2Header", out var headers))
        {
            for (int i = 0; i < headers.Rows.Count; i++)
            {
                if (Field(headers.Rows[i], "Header") is not { Value: byte[] previous } field)
                    continue;
                if (awb.Length < previous.Length)
                {
                    if (awbName is not null)
                        throw new InvalidDataException("AWB is shorter than stored StreamAwbAfs2Header.Header.");
                    continue;
                }
                awb.AsSpan(0, previous.Length).CopyTo(acb.AsSpan(field.Offset, previous.Length));
                headerRows.Add(i);
            }
            if (awbName is not null && headerRows.Count == 0)
                throw new InvalidDataException("No StreamAwbAfs2Header.Header field was patched.");
        }
        return (hashRows, headerRows);
    }

    private static object EditCueName(CriArguments args, bool delete)
    {
        string source = args.Require("acb");
        string target = args.Require("output");
        string name = args.Require("cue-name");
        var root = UtfTable.Read(CriFiles.Read(source));
        var tables = root.NestedTables();
        var nameTable = RequireTable(tables, "CueNameTable");
        var rows = nameTable.Rows.Where(row => Equals(Field(row, "CueName")?.Value, name)).ToArray();
        if (rows.Length != 1)
            throw new InvalidDataException($"Expected one cue named '{name}', found {rows.Length}.");
        var selected = rows[0];

        object report;
        if (delete)
        {
            var cueTable = RequireTable(tables, "CueTable");
            if (Index(selected, "CueIndex") is not int index || RowAt(cueTable, index) is null)
                throw new InvalidDataException("CueIndex is invalid.");
            nameTable.Rows.Remove(selected);
            cueTable.Rows.RemoveAt(index);
            foreach (var row in nameTable.Rows)
                if (Number(row, "CueIndex") is long other && other > index)
                    UtfField.Set(row, "CueIndex", other - 1);
            report = new { acb = target, deleted_name = name, cue_index = index, awb_payload_preserved = true };
        }
        else
        {
            string newName = args.Require("new-name");
            if (nameTable.Rows.Any(row => Equals(Field(row, "CueName")?.Value, newName)))
                throw new InvalidDataException($"Cue name already exists: {newName}");
            UtfField.Set(selected, "CueName", newName);
            report = new { acb = target, old_name = name, new_name = newName };
        }
        CriFiles.Write(target, SerializeNested(root, tables));
        return report;
    }

    private static byte[] SerializeNested(UtfTable root, Dictionary<string, UtfTable> tables) =>
        root.ReplaceNested(tables.ToDictionary(pair => pair.Key, pair => pair.Value.Write()));

    private static UtfTable RequireTable(Dictionary<string, UtfTable> tables, string name) =>
        tables.GetValueOrDefault(name) ?? throw new InvalidDataException($"ACB has no {name}.");

    private static uint AwbId(CriArguments args, string key)
    {
        string value = args.Require(key);
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static long? WaveformDuration(List<UtfField> row) =>
        Number(row, "NumSamples") is long samples && Number(row, "SamplingRate") is > 0 and long rate
            ? Duration(samples, rate) : null;

    private static long Duration(long samples, long rate) => (long)Math.Round(samples * 1000.0 / rate);
}
