using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Level5.AudioTool.Core.Cri;

public static class CriCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Run(IReadOnlyList<string> arguments, string audioRoot, string dataRoot)
    {
        var args = new CriArguments(arguments, audioRoot, dataRoot);
        object? result = args.Command switch
        {
            "inspect" => Inspect(args),
            "unpack-awb" => Unpack(args),
            "replace-awb" => Replace(args),
            "patch-acb-waveform" or "add-acb-awb-hca" or "rename-acb-cue" or "delete-acb-cue"
                or "redirect-acb-cue-awb-hca" or "patch-acb-stream-awb" => CriAcbCommands.Run(args),
            "wav-info" or "clip-audio" or "loop-audio" or "ensure-plugins"
                or "preview-awb-entry" or "replace-awb-wav" => CriAudioCommands.Run(args),
            _ => throw new ArgumentException($"Unknown audio command: {args.Command}.", nameof(arguments))
        };
        return result switch { null => "", string text => text, _ => JsonSerializer.Serialize(result, JsonOptions) };
    }

    private static object Inspect(CriArguments args)
    {
        string path = args.Position(0);
        var data = CriFiles.Read(path);
        var output = args.Get("output");
        Dictionary<string, object?> metadata;
        if (data.AsSpan().StartsWith("AFS2"u8))
        {
            var names = args.Get("acb") is { } acb ? CriAcbCommands.CueNames(CriFiles.Read(acb)) : null;
            metadata = Metadata(Afs2Bank.Read(data), names);
        }
        else
        {
            var table = UtfTable.Read(data);
            var nested = table.NestedTables();
            var includeRows = !args.Has("summary");
            metadata = TableMetadata(table, includeRows);
            metadata["nested_tables"] = nested.ToDictionary(pair => pair.Key, pair => TableMetadata(pair.Value, false));
            if (output is not null)
                foreach (var (name, child) in nested)
                    CriFiles.WriteJson(Path.Combine(output, "tables", CriFiles.SafeName(name) + ".json"),
                        TableMetadata(child, includeRows));
        }
        if (output is not null)
            CriFiles.WriteJson(Path.Combine(output, Path.GetFileName(path) + ".metadata.json"), metadata);
        return metadata;
    }

    private static object Unpack(CriArguments args)
    {
        var source = args.Position(0);
        var output = args.Require("output");
        var bank = Afs2Bank.Read(CriFiles.Read(source));
        var names = args.Get("acb") is { } acb ? CriAcbCommands.CueNames(CriFiles.Read(acb)) : new Dictionary<uint, List<string>>();
        var extracted = new List<object>();
        for (int index = 0; index < bank.Entries.Count; index++)
        {
            var entry = bank.Entries[index];
            var cueNames = names.GetValueOrDefault(entry.Id) ?? [];
            string name = cueNames.FirstOrDefault() ?? "";
            string suffix = name.Length > 0 ? "_" + CriFiles.SafeName(name) : "";
            string filename = FormattableString.Invariant($"{index:D4}_{entry.Id:D5}{suffix}{entry.Extension}");
            CriFiles.Write(Path.Combine(output, filename), entry.Data);
            extracted.Add(new
            {
                index, id = entry.Id, name, cue_names = cueNames, cue_count = cueNames.Count,
                file = filename, size = entry.Data.Length, extension = entry.Extension, sha1 = Sha1(entry.Data)
            });
        }
        var metadata = Metadata(bank, null);
        metadata["source"] = source;
        metadata["extracted"] = extracted;
        CriFiles.WriteJson(Path.Combine(output, "manifest.json"), metadata);
        return $"Extracted {extracted.Count} entries to {output}";
    }

    private static object Replace(CriArguments args)
    {
        var source = args.Position(0);
        var target = args.Position(1);
        var bank = Afs2Bank.Read(CriFiles.Read(source));
        var byId = Replacements(args.All("replace-id"));
        var byIndex = Replacements(args.All("replace-index"));
        if (byId.Keys.Except(bank.Entries.Select(entry => entry.Id)).Any()
            || byIndex.Keys.Any(index => index >= bank.Entries.Count))
            throw new InvalidDataException("A requested replacement is missing from the AWB.");
        var replaced = new List<object>();
        for (int index = 0; index < bank.Entries.Count; index++)
        {
            var entry = bank.Entries[index];
            var path = byId.GetValueOrDefault(entry.Id) ?? byIndex.GetValueOrDefault((uint)index);
            if (path is null) continue;
            var replacement = entry with { Data = File.ReadAllBytes(path) };
            bank.Entries[index] = replacement;
            replaced.Add(new { index, id = entry.Id, old_size = entry.Data.Length, new_size = replacement.Data.Length,
                source = path, extension = replacement.Extension });
        }
        var data = bank.Write();
        CriFiles.Write(target, data);
        CriFiles.WriteJson(target + ".replace-report.json", new
        {
            source, target, replaced, result = Metadata(Afs2Bank.Read(data), null)
        });
        return $"Wrote {target} with {replaced.Count} replacement(s)";
    }

    private static Dictionary<uint, string> Replacements(IReadOnlyList<string> values)
    {
        var result = new Dictionary<uint, string>();
        foreach (var value in values)
        {
            int equals = value.IndexOf('=');
            if (equals <= 0) throw new ArgumentException($"Replacement must use KEY=PATH: {value}");
            var key = value[..equals];
            uint number = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.Parse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : uint.Parse(key, CultureInfo.InvariantCulture);
            result[number] = value[(equals + 1)..];
        }
        return result;
    }

    internal static Dictionary<string, object?> Metadata(Afs2Bank bank, Dictionary<uint, List<string>>? names)
    {
        var entries = bank.Entries.Select((entry, index) =>
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index, ["id"] = entry.Id, ["offset"] = entry.Offset,
                ["size"] = entry.Data.Length, ["extension"] = entry.Extension, ["sha1"] = Sha1(entry.Data)
            };
            if (names is not null)
            {
                var cues = names.GetValueOrDefault(entry.Id) ?? [];
                item["name"] = cues.FirstOrDefault() ?? "";
                item["cue_names"] = cues;
                item["cue_count"] = cues.Count;
            }
            return item;
        }).ToArray();
        return new()
        {
            ["type"] = "awb", ["file_count"] = entries.Length, ["version"] = bank.Version,
            ["reserved"] = bank.Reserved, ["offset_size"] = bank.OffsetWidth, ["id_size"] = bank.IdWidth,
            ["alignment"] = bank.Alignment, ["subkey"] = bank.Subkey, ["entries"] = entries
        };
    }

    private static Dictionary<string, object?> TableMetadata(UtfTable table, bool includeRows)
    {
        var result = new Dictionary<string, object?>
        {
            ["table_name"] = table.Name, ["row_count"] = table.Rows.Count,
            ["columns"] = table.Rows.FirstOrDefault()?.Select(field => field.Name).ToArray() ?? []
        };
        if (includeRows)
            result["rows"] = table.Rows.Select(row => new { fields = row.Select(field => new
            {
                name = field.Name, type = field.Kind, offset = field.Offset, size = field.Size,
                value = field.Value is byte[] bytes ? (object)$"<{bytes.Length} bytes>" : field.Value
            }) });
        return result;
    }

    private static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
}
