using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Level5.AudioTool.Core.Cri;

internal static partial class CriAcbCommands
{
    private static object AddOrRedirectCue(CriArguments args, bool redirect)
    {
        string sourceAcb = args.Require("acb"), sourceAwb = args.Require("awb");
        string targetAcb = args.Require("output-acb"), targetAwb = args.Require("output-awb");
        string templateName = args.Require(redirect ? "cue-name" : "template-name");
        string outputName = redirect ? templateName : args.Require("name");
        uint id = AwbId(args, "awb-id");
        int samples = RequiredInt(args, "samples"), length = RequiredInt(args, "length-ms");
        byte[] hca = File.ReadAllBytes(args.Require("hca"));
        if (!hca.AsSpan().StartsWith("HCA\0"u8))
            throw new InvalidDataException("The new entry must be a plain HCA file.");

        var archive = Afs2Bank.Read(CriFiles.Read(sourceAwb));
        if (archive.Entries.Any(entry => entry.Id == id))
            throw new InvalidDataException($"AWB ID {id} already exists.");
        var root = UtfTable.Read(CriFiles.Read(sourceAcb));
        var tables = root.NestedTables();
        var names = RequireTable(tables, "CueNameTable");
        var cues = RequireTable(tables, "CueTable");
        var waves = RequireTable(tables, "WaveformTable");
        var sequences = RequireTable(tables, "SequenceTable");
        var synths = RequireTable(tables, "SynthTable");
        var tracks = RequireTable(tables, "TrackTable");
        var events = RequireTable(tables, "TrackEventTable");

        if (!redirect && names.Rows.Any(row => Equals(Field(row, "CueName")?.Value, outputName)))
            throw new InvalidDataException($"Cue name already exists: {outputName}");
        var name = names.Rows.FirstOrDefault(row => Equals(Field(row, "CueName")?.Value, templateName))
            ?? throw new InvalidDataException($"Template cue not found: {templateName}");
        int cueIndex = Index(name, "CueIndex") ?? throw new InvalidDataException("CueIndex is invalid.");
        var cue = RequiredRow(cues, cueIndex, "CueIndex");
        if (Number(cue, "ReferenceType") != 3 || Index(cue, "ReferenceIndex") is not int sequenceIndex)
            throw new InvalidDataException("Cue is not a sequence cue.");
        var sequence = RequiredRow(sequences, sequenceIndex, "Sequence index");

        int[] trackIndices = ReadIndices(Blob(sequence, "TrackIndex"), Index(sequence, "NumTracks") ?? 0).ToArray();
        if (trackIndices.Length != 1)
            throw new InvalidDataException("Only one-track cues are supported.");
        var track = RequiredRow(tracks, trackIndices[0], "Track index");
        int eventIndex = Index(track, "EventIndex") ?? throw new InvalidDataException("Cue track has no event.");
        var trackEvent = RequiredRow(events, eventIndex, "Track event index");
        var synthIndices = CommandSynthIndices(Blob(trackEvent, "Command"));
        if (synthIndices.Count != 1)
            throw new InvalidDataException("Only one-synth cues are supported.");
        int synthIndex = synthIndices[0];
        var synth = RequiredRow(synths, synthIndex, "Synth index");
        int[] waveformIndices = ResolveCueWaveforms(cue, tables);
        if (waveformIndices.Length == 0 || (redirect && waveformIndices.Length != 1))
            throw new InvalidDataException("Only one-waveform cues are supported.");
        int waveformIndex = waveformIndices[0];

        int newWaveIndex = waves.Rows.Count, newSynthIndex = synths.Rows.Count;
        int newEventIndex = events.Rows.Count, newTrackIndex = tracks.Rows.Count;
        int newSequenceIndex = sequences.Rows.Count, newCueIndex = cues.Rows.Count;
        var newWave = UtfField.CloneRow(RequiredRow(waves, waveformIndex, "Waveform index"));
        UtfField.Set(newWave, "StreamAwbId", (long)id);
        if (redirect || Field(newWave, "NumSamples") is not null)
            UtfField.Set(newWave, "NumSamples", (long)samples);
        if (args.Int("channels") is int channels)
            UtfField.Set(newWave, "NumChannels", (long)channels);
        if (args.Int("sample-rate") is int rate)
            UtfField.Set(newWave, "SamplingRate", (long)rate);
        UtfField.Set(newWave, "LoopFlag", 1L);

        var newSynth = UtfField.CloneRow(synth);
        UtfField.Set(newSynth, "ReferenceItems", ReplaceBigEndianIndices(Blob(newSynth, "ReferenceItems"), waveformIndex, newWaveIndex));
        if (!redirect)
        {
            UtfField.Set(newSynth, "ControlWorkArea1", (long)newSynthIndex);
            UtfField.Set(newSynth, "ControlWorkArea2", (long)newSynthIndex);
        }
        var newEvent = UtfField.CloneRow(trackEvent);
        UtfField.Set(newEvent, "Command", ReplaceSynthIndex(Blob(newEvent, "Command"), synthIndex, newSynthIndex));
        var newTrack = UtfField.CloneRow(track);
        UtfField.Set(newTrack, "EventIndex", (long)newEventIndex);
        var newSequence = UtfField.CloneRow(sequence);
        var trackReference = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(trackReference, checked((ushort)newTrackIndex));
        UtfField.Set(newSequence, "TrackIndex", trackReference);

        var finalCue = redirect ? cue : UtfField.CloneRow(cue);
        UtfField.Set(finalCue, "ReferenceIndex", (long)newSequenceIndex);
        UtfField.Set(finalCue, "Length", (long)length);
        if (!redirect)
        {
            UtfField.Set(finalCue, "CueId", cues.Rows.Select(row => Number(row, "CueId") ?? 0).DefaultIfEmpty().Max() + 1);
            var newName = UtfField.CloneRow(name);
            UtfField.Set(newName, "CueName", outputName);
            UtfField.Set(newName, "CueIndex", (long)newCueIndex);
            cues.Rows.Add(finalCue);
            names.Rows.Add(newName);
        }

        waves.Rows.Add(newWave);
        synths.Rows.Add(newSynth);
        events.Rows.Add(newEvent);
        tracks.Rows.Add(newTrack);
        sequences.Rows.Add(newSequence);
        archive.Entries.Add(new Afs2Entry(id, hca));
        byte[] awbData = archive.Write();

        // The embedded stream header must grow with the new ID and offset entries.
        int count = archive.Entries.Count;
        int headerSize = checked(16 + count * awbData[6] + (count + 1) * awbData[5]);
        if (RowAt(tables.GetValueOrDefault("StreamAwbAfs2Header"), 0) is { } headerRow &&
            Field(headerRow, "Header") is not null)
            UtfField.Set(headerRow, "Header", awbData[..headerSize]);
        byte[] acbData = SerializeNested(root, tables);
        PatchStreamReferences(acbData, awbData, MD5.HashData(awbData), awbName: null);

        byte[]? existingAwb = File.Exists(targetAwb) ? File.ReadAllBytes(targetAwb) : null;
        CriFiles.Write(targetAwb, awbData);
        try
        {
            CriFiles.Write(targetAcb, acbData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (existingAwb is not null)
                CriFiles.Write(targetAwb, existingAwb);
            else
                File.Delete(targetAwb);
            throw;
        }
        if (redirect)
            return new { acb = targetAcb, awb = targetAwb, cue_name = outputName, awb_id = id, new_waveform_index = newWaveIndex };
        return new { acb = targetAcb, awb = targetAwb, name = outputName, awb_id = id, new_cue_index = newCueIndex, new_waveform_index = newWaveIndex };
    }

    private static int RequiredInt(CriArguments args, string name) =>
        args.Int(name) ?? throw new InvalidDataException($"Missing --{name}.");

    private static byte[] ReplaceBigEndianIndices(byte[] data, int from, int to)
    {
        var result = (byte[])data.Clone();
        for (int offset = 0; offset <= result.Length - 2; offset += 2)
            if (BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(offset)) == from)
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), checked((ushort)to));
        return result;
    }

    private static byte[] ReplaceSynthIndex(byte[] data, int from, int to)
    {
        var result = (byte[])data.Clone();
        foreach (byte[] pattern in SynthPatterns)
        {
            int cursor = 0;
            while (cursor < result.Length)
            {
                int relative = result.AsSpan(cursor).IndexOf(pattern);
                if (relative < 0) break;
                int offset = cursor + relative;
                int value = offset + pattern.Length;
                if (value <= result.Length - 2 && BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(value)) == from)
                    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(value), checked((ushort)to));
                cursor = offset + 1;
            }
        }
        return result;
    }
}
