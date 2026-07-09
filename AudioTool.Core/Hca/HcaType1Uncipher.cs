namespace Level5.AudioTool.Core.Hca;

public static class HcaType1Uncipher
{
    private static readonly byte[] Type1Table = CreateType1Table();

    public static bool TryConvertToPlain(ReadOnlySpan<byte> input, out byte[] output)
    {
        if (input.Length < 0x60 || input[0] != 'H' || input[1] != 'C' || input[2] != 'A' || input[3] != 0)
        {
            output = input.ToArray();
            return false;
        }

        output = input.ToArray();
        var headerSize = ReadBeUInt16(output, 6);
        var fmtOffset = FindChunk(output, [(byte)'f', (byte)'m', (byte)'t', 0], headerSize);
        var compOffset = FindChunk(output, [(byte)'c', (byte)'o', (byte)'m', (byte)'p'], headerSize);
        var ciphOffset = FindChunk(output, [(byte)'c', (byte)'i', (byte)'p', (byte)'h'], headerSize);
        if (fmtOffset < 0 || compOffset < 0 || ciphOffset < 0)
        {
            return false;
        }

        var encryptionType = ReadBeUInt16(output, ciphOffset + 4);
        if (encryptionType == 0)
        {
            return false;
        }
        if (encryptionType != 1)
        {
            throw new InvalidDataException($"Unsupported HCA EncryptionType={encryptionType}");
        }

        var frameCount = ReadBeUInt32(output, fmtOffset + 8);
        var frameSize = ReadBeUInt16(output, compOffset + 4);
        var audioOffset = headerSize;
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frameOffset = audioOffset + frameIndex * frameSize;
            if (frameOffset + frameSize > output.Length)
            {
                throw new InvalidDataException("Truncated HCA frame data.");
            }

            for (var index = 0; index < frameSize; index++)
            {
                output[frameOffset + index] = Type1Table[output[frameOffset + index]];
            }

            WriteBeUInt16(output, frameOffset + frameSize - 2, Crc16(output.AsSpan(frameOffset, frameSize - 2)));
        }

        WriteBeUInt16(output, ciphOffset + 4, 0);
        WriteBeUInt16(output, headerSize - 2, Crc16(output.AsSpan(0, headerSize - 2)));
        return true;
    }

    private static int FindChunk(byte[] data, ReadOnlySpan<byte> chunk, int headerSize)
    {
        for (var offset = 8; offset <= headerSize - chunk.Length; offset++)
        {
            if (data.AsSpan(offset, chunk.Length).SequenceEqual(chunk))
            {
                return offset;
            }
        }

        return -1;
    }

    private static byte[] CreateType1Table()
    {
        var table = new byte[256];
        var value = 0;
        var output = 1;
        for (var index = 0; index < 256; index++)
        {
            value = (value * 13 + 11) % 256;
            if (value != 0 && value != 255)
            {
                table[output++] = (byte)value;
            }
        }

        table[255] = 255;
        return table;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort value = 0;
        foreach (var item in data)
        {
            value ^= (ushort)(item << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x8000) != 0
                    ? (ushort)((value << 1) ^ 0x8005)
                    : (ushort)(value << 1);
            }
        }

        return value;
    }

    private static ushort ReadBeUInt16(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static int ReadBeUInt32(byte[] data, int offset)
    {
        return data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];
    }

    private static void WriteBeUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
