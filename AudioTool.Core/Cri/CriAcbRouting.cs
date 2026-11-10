using System.Buffers.Binary;

namespace Level5.AudioTool.Core.Cri;

internal static partial class CriAcbCommands
{
    internal static Dictionary<uint, List<string>> CueNames(byte[] acb)
    {
        var tables = UtfTable.Read(acb).NestedTables();
        var result = new Dictionary<uint, List<string>>();
        if (!tables.TryGetValue("WaveformTable", out var waveform) || !tables.TryGetValue("CueTable", out var cue))
            return result;

        var byIndex = new Dictionary<int, string>();
        if (tables.TryGetValue("CueNameTable", out var names))
            foreach (var row in names.Rows)
                if (Index(row, "CueIndex") is int index && Field(row, "CueName")?.Value is string name)
                    byIndex[index] = name;

        for (int cueIndex = 0; cueIndex < cue.Rows.Count; cueIndex++)
        {
            var row = cue.Rows[cueIndex];
            var name = byIndex.GetValueOrDefault(cueIndex);
            if (string.IsNullOrEmpty(name))
                name = new[] { "Name", "CueName", "CueNameTable", "UserData" }
                    .Select(key => Field(row, key)?.Value as string)
                    .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? $"Cue {cueIndex}";
            foreach (int waveformIndex in ResolveCueWaveforms(row, tables))
            {
                var wave = waveform.Rows[waveformIndex];
                long? id = Number(wave, "StreamAwbId");
                if (id is null or < 0)
                    id = Number(wave, "MemoryAwbId");
                if (id is null or < 0 or > uint.MaxValue)
                    continue;
                if (!result.TryGetValue((uint)id, out var existing))
                    result.Add((uint)id, existing = []);
                if (!existing.Contains(name))
                    existing.Add(name);
            }
        }
        return result;
    }

    internal static Dictionary<string, object?>? EntryInfo(byte[] acb, uint id)
    {
        var tables = UtfTable.Read(acb).NestedTables();
        if (!tables.TryGetValue("WaveformTable", out var waveform))
            return null;
        for (int i = 0; i < waveform.Rows.Count; i++)
        {
            var row = waveform.Rows[i];
            var stream = Number(row, "StreamAwbId");
            var memory = Number(row, "MemoryAwbId");
            if (stream != id && memory != id)
                continue;
            var flag = Number(row, "LoopFlag");
            Dictionary<string, object?>? loop = null;
            if (flag is null or 2 && Index(row, "ExtensionData") is int extension && extension != ushort.MaxValue &&
                RowAt(tables.GetValueOrDefault("WaveformExtensionDataTable"), extension) is { } extensionRow &&
                Number(extensionRow, "LoopStart") is long start && Number(extensionRow, "LoopEnd") is long end && end > start)
                loop = new() { ["start"] = start, ["end"] = end, ["source"] = "acb" };
            return new()
            {
                ["waveform_index"] = i, ["stream_awb_id"] = stream, ["memory_awb_id"] = memory,
                ["loop_flag"] = flag, ["sample_count"] = Number(row, "NumSamples"),
                ["sample_rate"] = Number(row, "SamplingRate"), ["channels"] = Number(row, "NumChannels"),
                ["encode_type"] = Number(row, "EncodeType"), ["loop"] = loop
            };
        }
        return null;
    }

    private static int[] ResolveCueWaveforms(List<UtfField> cue, Dictionary<string, UtfTable> tables)
    {
        int waveCount = tables.GetValueOrDefault("WaveformTable")?.Rows.Count ?? 0;
        if (Index(cue, "ReferenceIndex") is not int index)
            return [];
        long? type = Number(cue, "ReferenceType");
        if (type == 1)
            return index < waveCount ? [index] : [];
        if (type != 3 || RowAt(tables.GetValueOrDefault("SequenceTable"), index) is not { } sequence)
            return [];

        var synths = tables.GetValueOrDefault("SynthTable");
        var tracks = tables.GetValueOrDefault("TrackTable");
        var events = tables.GetValueOrDefault("TrackEventTable");
        var sequenceCommands = tables.GetValueOrDefault("SeqCommandTable");
        int trackCount = Index(sequence, "NumTracks") ?? 0;
        var resolved = new HashSet<int>();
        foreach (int track in ReadIndices(Blob(sequence, "TrackIndex"), trackCount))
        {
            if (Index(RowAt(tracks, track), "EventIndex") is int eventIndex && RowAt(events, eventIndex) is { } row)
                AddCommandWaveforms(row);
            else
                AddSynthWaveforms(track, []);
        }
        if (trackCount == 0 && Index(sequence, "CommandIndex") is int command &&
            RowAt(sequenceCommands, command) is { } commandRow)
            AddCommandWaveforms(commandRow);
        return resolved.Where(wave => wave < waveCount).OrderBy(wave => wave).ToArray();

        void AddCommandWaveforms(List<UtfField> row)
        {
            foreach (int synth in CommandSynthIndices(Blob(row, "Command")))
                AddSynthWaveforms(synth, []);
        }

        void AddSynthWaveforms(int initial, HashSet<int> seen)
        {
            var pending = new Stack<int>();
            pending.Push(initial);
            while (pending.TryPop(out int synth))
            {
                if (!seen.Add(synth) || RowAt(synths, synth) is not { } row)
                    continue;
                byte[] data = Blob(row, "ReferenceItems");
                for (int i = 0; i <= data.Length - 4; i += 4)
                {
                    int reference = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 2));
                    switch (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i)))
                    {
                        case 1: resolved.Add(reference); break;
                        case 5 or 7: pending.Push(reference); break;
                    }
                }
            }
        }
    }

    private static IEnumerable<int> ReadIndices(byte[] data, int count)
    {
        for (int i = 0; i < Math.Min(count, data.Length / 2); i++)
            yield return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i * 2));
    }

    private static readonly byte[][] SynthPatterns =
    [
        [0x07, 0xd0, 0x04, 0x00, 0x02, 0x00], [0x00, 0x4f, 0x05, 0x00, 0x01, 0x00]
    ];

    private static List<int> CommandSynthIndices(byte[] data)
    {
        var indices = new List<int>();
        foreach (byte[] pattern in SynthPatterns)
        {
            int cursor = 0;
            while (cursor < data.Length)
            {
                int relative = data.AsSpan(cursor).IndexOf(pattern);
                if (relative < 0) break;
                int offset = cursor + relative;
                int start = offset + pattern.Length;
                if (start <= data.Length - 2)
                {
                    int value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start));
                    if (!indices.Contains(value)) indices.Add(value);
                }
                cursor = offset + 1;
            }
        }
        return indices;
    }

    private static UtfField? Field(List<UtfField>? row, string name) =>
        row is null ? null : UtfField.Find(row, name);

    private static long? Number(List<UtfField>? row, string name) =>
        row is null ? null : UtfField.Integer(row, name);

    private static int? Index(List<UtfField>? row, string name) =>
        Number(row, name) is >= 0 and <= int.MaxValue and long index ? (int)index : null;

    private static byte[] Blob(List<UtfField> row, string name) => Field(row, name)?.Value as byte[] ?? [];

    private static List<UtfField>? RowAt(UtfTable? table, int index) =>
        table is not null && (uint)index < table.Rows.Count ? table.Rows[index] : null;

    private static List<UtfField> RequiredRow(UtfTable table, int index, string label) =>
        RowAt(table, index) ?? throw new InvalidDataException($"{label} is invalid.");
}
