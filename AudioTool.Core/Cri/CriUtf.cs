using System.Buffers.Binary;
using System.Text;

namespace Level5.AudioTool.Core.Cri;

public sealed class UtfField(string name, byte rawType, object? value, int offset, int size)
{
    public string Name { get; } = name;
    public byte RawType { get; } = rawType;
    public object? Value { get; set; } = value;
    public int Offset { get; } = offset;
    public int Size { get; } = size;

    public string Kind => (RawType & 0x0f) switch
    {
        0x0b => "data",
        0x0a => "string",
        0x08 => "float",
        _ => "number"
    };

    public static UtfField? Find(IEnumerable<UtfField> row, string name) =>
        row.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static long? Integer(IEnumerable<UtfField> row, string name) => Find(row, name)?.Value switch
    {
        byte number => number,
        sbyte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number when number <= long.MaxValue => (long)number,
        _ => null
    };

    public static void Set(IEnumerable<UtfField> row, string name, object? value)
    {
        var field = Find(row, name) ?? throw new InvalidDataException($"UTF row has no field '{name}'.");
        field.Value = value;
    }

    public static List<UtfField> CloneRow(IEnumerable<UtfField> row) =>
        row.Select(field => new UtfField(field.Name, field.RawType,
            field.Value is byte[] data ? data.ToArray() : field.Value, -1, 0)).ToList();

    public static bool PatchInteger(byte[] data, IEnumerable<UtfField> row, string name, long value)
    {
        if (Find(row, name) is not { Offset: >= 0, Size: > 0 } field)
            return false;
        var target = data.AsSpan(field.Offset, field.Size);
        switch (field.Size)
        {
            case 1: target[0] = unchecked((byte)value); break;
            case 2: BinaryPrimitives.WriteUInt16BigEndian(target, checked((ushort)value)); break;
            case 4: BinaryPrimitives.WriteUInt32BigEndian(target, checked((uint)value)); break;
            case 8: BinaryPrimitives.WriteUInt64BigEndian(target, checked((ulong)value)); break;
            default: throw new InvalidDataException($"Unsupported integer patch size: {field.Size}.");
        }
        return true;
    }
}

public sealed class UtfTable(string name, List<List<UtfField>> rows)
{
    private const int HeaderSize = 0x20;
    private const int OffsetBase = 8;
    private static ReadOnlySpan<byte> Magic => "@UTF"u8;

    public string Name { get; } = name;
    public List<List<UtfField>> Rows { get; } = rows;

    public static UtfTable Read(byte[] data, int baseOffset = 0)
    {
        if (data.Length < HeaderSize || !data.AsSpan().StartsWith(Magic))
            throw new InvalidDataException("Not a valid CRI @UTF table.");

        int rowOffset = ReadUInt16(data, 0x0a) + OffsetBase;
        int stringsOffset = AddOffset(OffsetBase, ReadUInt32(data, 0x0c), data.Length);
        int dataOffset = AddOffset(OffsetBase, ReadUInt32(data, 0x10), data.Length);
        uint nameOffset = ReadUInt32(data, 0x14);
        int fieldCount = ReadUInt16(data, 0x18);
        int rowSize = ReadUInt16(data, 0x1a);
        uint rowCount = ReadUInt32(data, 0x1c);
        if (rowOffset < HeaderSize || stringsOffset < rowOffset || dataOffset < stringsOffset ||
            (long)rowOffset + rowCount * (long)rowSize > stringsOffset ||
            fieldCount > (rowOffset - HeaderSize) / 5 || rowCount > int.MaxValue)
            throw new InvalidDataException("Invalid CRI @UTF table section sizes.");

        var columns = new UtfField[fieldCount];
        int schemaCursor = HeaderSize;
        int rowFieldsSize = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            var descriptor = ReadSection(data, schemaCursor, 5, rowOffset);
            byte type = descriptor[0];
            uint columnNameOffset = BinaryPrimitives.ReadUInt32BigEndian(descriptor[1..]);
            string columnName = ReadString(data, stringsOffset, dataOffset, columnNameOffset);
            schemaCursor += 5;
            int size = ScalarSize(type);
            if ((type & 0xf0) is 0x30 or 0x70)
            {
                columns[i] = ReadField(data, columnName, type, schemaCursor, rowOffset,
                    stringsOffset, dataOffset, baseOffset);
                schemaCursor += size;
            }
            else
            {
                if ((type & 0xf0) == 0x50)
                    rowFieldsSize += size;
                columns[i] = new UtfField(columnName, type, (type & 0xf0) == 0x10 ? 0L : null, -1, 0);
            }
        }
        if (rowFieldsSize > rowSize)
            throw new InvalidDataException("CRI @UTF fields exceed the row size.");

        var table = new UtfTable(ReadString(data, stringsOffset, dataOffset, nameOffset), []);
        for (int i = 0; i < rowCount; i++)
        {
            int cursor = rowOffset + rowSize * i;
            var row = new List<UtfField>(fieldCount);
            foreach (var column in columns)
            {
                if ((column.RawType & 0xf0) == 0x50)
                {
                    var field = ReadField(data, column.Name, column.RawType, cursor, stringsOffset,
                        stringsOffset, dataOffset, baseOffset);
                    row.Add(field);
                    cursor += field.Size;
                }
                else
                {
                    row.Add(new UtfField(column.Name, column.RawType, column.Value, column.Offset, column.Size));
                }
            }
            table.Rows.Add(row);
        }
        return table;
    }

    public Dictionary<string, UtfTable> NestedTables()
    {
        var tables = new Dictionary<string, UtfTable>(StringComparer.Ordinal);
        foreach (var row in Rows)
        foreach (var field in row)
        {
            if (field.Kind != "data" || field.Value is not byte[] data || !data.AsSpan().StartsWith(Magic))
                continue;
            try
            {
                tables[field.Name] = Read(data, field.Offset);
            }
            catch (InvalidDataException)
            {
                // A data blob with these first four bytes need not be a usable nested table.
            }
        }
        return tables;
    }

    public byte[] ReplaceNested(IReadOnlyDictionary<string, byte[]> replacements)
    {
        foreach (var row in Rows)
        foreach (var field in row)
            if (field.Kind == "data" && replacements.TryGetValue(field.Name, out var value))
                field.Value = value;
        return Write();
    }

    public byte[] Write()
    {
        if (Rows.Count == 0)
            throw new InvalidDataException($"Cannot serialize empty UTF table '{Name}'.");

        var columns = Rows[0];
        int rowSize = 0;
        foreach (var column in columns)
        {
            int size = ScalarSize(column.RawType);
            if (size == 0)
                throw new InvalidDataException($"Unsupported UTF column type 0x{column.RawType & 0x0f:x2} in '{Name}.{column.Name}'.");
            rowSize += size;
        }

        int rowOffset = HeaderSize + columns.Count * 5;
        long stringsStart = rowOffset + (long)rowSize * Rows.Count;
        if (stringsStart - OffsetBase > ushort.MaxValue)
            throw TooLarge();

        using var strings = new MemoryStream();
        using var payloads = new MemoryStream();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint AddString(string value)
        {
            if (offsets.TryGetValue(value, out uint existing))
                return existing;
            uint offset = (uint)strings.Length;
            offsets.Add(value, offset);
            strings.Write(Encoding.UTF8.GetBytes(value));
            strings.WriteByte(0);
            return offset;
        }

        uint nameOffset = AddString(Name);
        var table = new byte[(int)stringsStart];
        int cursor = rowOffset;
        foreach (var row in Rows)
        {
            if (row.Count != columns.Count)
                throw new InvalidDataException($"UTF table '{Name}' contains inconsistent rows.");
            for (int i = 0; i < columns.Count; i++)
            {
                byte type = columns[i].RawType;
                int size = ScalarSize(type);
                var target = table.AsSpan(cursor, size);
                object? value = row[i].Value;
                switch (type & 0x0f)
                {
                    case 0x0a:
                        BinaryPrimitives.WriteUInt32BigEndian(target, AddString(value as string ?? ""));
                        break;
                    case 0x0b:
                        var payload = value as byte[] ?? [];
                        BinaryPrimitives.WriteUInt32BigEndian(target, (uint)payloads.Length);
                        BinaryPrimitives.WriteUInt32BigEndian(target[4..], (uint)payload.Length);
                        payloads.Write(payload);
                        break;
                    default:
                        WriteScalar(target, type, value);
                        break;
                }
                cursor += size;
                if (table.Length + strings.Length + payloads.Length - OffsetBase > ushort.MaxValue)
                    throw TooLarge();
            }
        }

        cursor = HeaderSize;
        foreach (var column in columns)
        {
            table[cursor] = (byte)(0x50 | (column.RawType & 0x0f));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(cursor + 1), AddString(column.Name));
            cursor += 5;
        }

        int dataOffset = table.Length + (int)strings.Length;
        int length = dataOffset + (int)payloads.Length;
        if (length - OffsetBase > ushort.MaxValue)
            throw TooLarge();

        Magic.CopyTo(table);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0x06), (ushort)(length - OffsetBase));
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0x0a), (ushort)(rowOffset - OffsetBase));
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0x0c), (uint)(table.Length - OffsetBase));
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0x10), (uint)(dataOffset - OffsetBase));
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0x14), nameOffset);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0x18), (ushort)columns.Count);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0x1a), (ushort)rowSize);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0x1c), (uint)Rows.Count);

        var output = new byte[length];
        table.CopyTo(output, 0);
        strings.GetBuffer().AsSpan(0, (int)strings.Length).CopyTo(output.AsSpan(table.Length));
        payloads.GetBuffer().AsSpan(0, (int)payloads.Length).CopyTo(output.AsSpan(dataOffset));
        return output;
    }

    private InvalidDataException TooLarge() => new($"UTF table '{Name}' is too large for its size field.");

    private static int ScalarSize(byte type) => (type & 0x0f) switch
    {
        0x00 or 0x01 => 1,
        0x02 or 0x03 => 2,
        0x04 or 0x05 or 0x08 or 0x0a => 4,
        0x06 or 0x0b => 8,
        _ => 0
    };

    private static UtfField ReadField(byte[] data, string name, byte type, int offset, int limit,
        int stringsOffset, int dataOffset, int baseOffset)
    {
        int size = ScalarSize(type);
        var value = ReadSection(data, offset, size, limit);
        object? decoded;
        int valueOffset = offset;
        switch (type & 0x0f)
        {
            case 0x0a:
                decoded = ReadString(data, stringsOffset, dataOffset, BinaryPrimitives.ReadUInt32BigEndian(value));
                break;
            case 0x0b:
                valueOffset = AddOffset(dataOffset, BinaryPrimitives.ReadUInt32BigEndian(value), data.Length);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
                if (length > data.Length - valueOffset)
                    throw new InvalidDataException("CRI @UTF data field exceeds the table.");
                decoded = data.AsSpan(valueOffset, (int)length).ToArray();
                break;
            default:
                decoded = ReadScalar(value, type);
                break;
        }
        return new UtfField(name, type, decoded, checked(baseOffset + valueOffset), size);
    }

    private static object? ReadScalar(ReadOnlySpan<byte> value, byte type) => (type & 0x0f) switch
    {
        0x00 => (long)value[0],
        0x01 => (long)unchecked((sbyte)value[0]),
        0x02 => (long)BinaryPrimitives.ReadUInt16BigEndian(value),
        0x03 => (long)BinaryPrimitives.ReadInt16BigEndian(value),
        0x04 => (long)BinaryPrimitives.ReadUInt32BigEndian(value),
        0x05 => (long)BinaryPrimitives.ReadInt32BigEndian(value),
        0x06 => UnsignedNumber(BinaryPrimitives.ReadUInt64BigEndian(value)),
        0x08 => BinaryPrimitives.ReadSingleBigEndian(value),
        _ => null
    };

    private static object UnsignedNumber(ulong value) => value <= long.MaxValue ? (object)(long)value : value;

    private static void WriteScalar(Span<byte> target, byte type, object? value)
    {
        switch (type & 0x0f)
        {
            case 0x00: target[0] = unchecked((byte)Convert.ToInt64(value)); break;
            case 0x01: target[0] = unchecked((byte)Convert.ToSByte(value)); break;
            case 0x02: BinaryPrimitives.WriteUInt16BigEndian(target, Convert.ToUInt16(value)); break;
            case 0x03: BinaryPrimitives.WriteInt16BigEndian(target, Convert.ToInt16(value)); break;
            case 0x04: BinaryPrimitives.WriteUInt32BigEndian(target, Convert.ToUInt32(value)); break;
            case 0x05: BinaryPrimitives.WriteInt32BigEndian(target, Convert.ToInt32(value)); break;
            case 0x06: BinaryPrimitives.WriteUInt64BigEndian(target, Convert.ToUInt64(value)); break;
            case 0x08:
                float number = Convert.ToSingle(value);
                BinaryPrimitives.WriteSingleBigEndian(target, number == 0 ? 0 : number);
                break;
        }
    }

    private static string ReadString(byte[] data, int start, int end, uint relative)
    {
        if (relative >= end - start)
            return "";
        var contents = data.AsSpan(start + (int)relative, end - start - (int)relative);
        int terminator = contents.IndexOf((byte)0);
        return Encoding.UTF8.GetString(terminator < 0 ? contents : contents[..terminator]);
    }

    private static int AddOffset(int start, uint relative, int length)
    {
        if (relative > length - start)
            throw new InvalidDataException("CRI @UTF offset exceeds the table.");
        return start + (int)relative;
    }

    private static ReadOnlySpan<byte> ReadSection(byte[] data, int offset, int length, int limit)
    {
        if (offset < 0 || offset > limit - length)
            throw new InvalidDataException("CRI @UTF value exceeds its section.");
        return data.AsSpan(offset, length);
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
}
