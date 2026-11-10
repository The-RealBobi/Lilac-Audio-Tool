using System.Buffers.Binary;

namespace Level5.AudioTool.Core.Cri;

public sealed record Afs2Entry(uint Id, byte[] Data, int Offset = 0)
{
    public string Extension => Data.AsSpan().StartsWith("HCA\0"u8)
        || Data.AsSpan().StartsWith(new byte[] { 0xC8, 0xC3, 0xC1, 0 }) ? ".hca"
        : Data.AsSpan().StartsWith("RIFF"u8) ? ".wav"
        : Data.Length > 0 && Data[0] == 0x80 ? ".adx" : ".bin";
}

public sealed record Afs2Bank(byte Version, byte Reserved, int OffsetWidth, int IdWidth, ushort Alignment, ushort Subkey, List<Afs2Entry> Entries)
{
    public static Afs2Bank Read(byte[] source)
    {
        var bytes = source.AsSpan();
        if (bytes.Length < 16 || !bytes[..4].SequenceEqual("AFS2"u8))
            throw new InvalidDataException("Invalid AFS2 signature.");
        int offsetWidth = bytes[5], idWidth = bytes[6];
        if (offsetWidth is not (2 or 4 or 8) || idWidth is not (2 or 4))
            throw new InvalidDataException("Unsupported AFS2 table width.");
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
        ushort alignment = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        ushort subkey = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        int offsetTable = checked(16 + count * idWidth);
        int headerSize = checked(offsetTable + (count + 1) * offsetWidth);
        if (headerSize > bytes.Length)
            throw new InvalidDataException("Truncated AFS2 tables.");
        var entries = new List<Afs2Entry>(count);
        for (int i = 0; i < count; i++)
        {
            uint id = checked((uint)ReadWord(bytes[(16 + i * idWidth)..], idWidth));
            int start = Align(checked((int)ReadWord(bytes[(offsetTable + i * offsetWidth)..], offsetWidth)), alignment);
            int end = checked((int)ReadWord(bytes[(offsetTable + (i + 1) * offsetWidth)..], offsetWidth));
            if (start < headerSize || start > end || end > bytes.Length)
                throw new InvalidDataException("AFS2 entry is outside the data region.");
            entries.Add(new Afs2Entry(id, bytes[start..end].ToArray(), start));
        }
        return new Afs2Bank(bytes[4], bytes[7], offsetWidth, idWidth, alignment, subkey, entries);
    }

    public byte[] Write()
    {
        int idWidth = Entries.Any(entry => entry.Id > ushort.MaxValue) ? 4 : IdWidth;
        int offsetWidth = OffsetWidth;
        int length;
        var offsets = new int[Entries.Count + 1];
        while (true)
        {
            offsets[0] = checked(16 + Entries.Count * idWidth + offsets.Length * offsetWidth);
            length = offsets[0];
            for (int i = 0; i < Entries.Count; i++)
            {
                length = checked(Align(length, Alignment) + Entries[i].Data.Length);
                offsets[i + 1] = length;
            }
            int needed = OffsetWidth == 8 ? 8 : OffsetWidth == 2 && length <= ushort.MaxValue ? 2 : 4;
            if (needed == offsetWidth) break;
            offsetWidth = needed;
        }
        var output = new byte[Math.Max(length, Align(offsets[0], Alignment))];
        "AFS2"u8.CopyTo(output);
        output[4] = Version;
        output[5] = (byte)offsetWidth;
        output[6] = (byte)idWidth;
        output[7] = Reserved;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), Entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12), Alignment);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(14), Subkey);
        int cursor = 16;
        foreach (var entry in Entries)
        {
            WriteWord(output.AsSpan(cursor), idWidth, entry.Id);
            cursor += idWidth;
        }
        foreach (int offset in offsets)
        {
            WriteWord(output.AsSpan(cursor), offsetWidth, (uint)offset);
            cursor += offsetWidth;
        }
        for (int i = 0; i < Entries.Count; i++)
            Entries[i].Data.CopyTo(output, Align(offsets[i], Alignment));
        return output;
    }

    private static int Align(int value, int alignment) =>
        alignment <= 1 ? value : checked(value + alignment - 1) & ~(alignment - 1);

    private static ulong ReadWord(ReadOnlySpan<byte> data, int width) => width switch
    {
        2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
        8 => BinaryPrimitives.ReadUInt64LittleEndian(data),
        _ => throw new InvalidDataException("Unsupported AFS2 integer width.")
    };

    private static void WriteWord(Span<byte> data, int width, ulong value)
    {
        switch (width)
        {
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(data, checked((ushort)value)); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(data, checked((uint)value)); break;
            case 8: BinaryPrimitives.WriteUInt64LittleEndian(data, value); break;
            default: throw new InvalidDataException("Unsupported AFS2 integer width.");
        }
    }
}
