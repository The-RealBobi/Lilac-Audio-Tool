using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Level5.AudioTool.Core.Cri;

internal static class CriFiles
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static byte[] Read(string path) => Readable(File.ReadAllBytes(path), Path.GetFileName(path));

    internal static byte[] Readable(byte[] data, string filename)
    {
        ReadOnlySpan<byte> magic = Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".awb" => "AFS2"u8,
            ".acb" => "@UTF"u8,
            ".usm" => "CRID"u8,
            _ => []
        };
        if (magic.IsEmpty || data.AsSpan().StartsWith(magic))
            return data;
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(key, Crc32(Encoding.UTF8.GetBytes(filename), 0));
        var output = (byte[])data.Clone();
        uint crc = 0;
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 3) == 0)
                crc = Crc32(key, (uint)i);
            int shift = (i & 3) * 2;
            uint mask = (((crc >> (shift + 8)) & 3) << 4)
                | ((crc >> shift) << 6)
                | (((crc >> (shift + 16)) & 3) << 2)
                | ((crc >> (shift + 24)) & 3);
            output[i] ^= (byte)mask;
        }
        if (!output.AsSpan().StartsWith(magic))
            throw new InvalidDataException($"Could not decrypt or read {filename} as a CRI file.");
        return output;
    }

    internal static void Write(string path, byte[] data)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string staging = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staging, data);
            File.Move(staging, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    internal static void WriteJson(string path, object data) =>
        Write(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, JsonOptions) + "\n"));

    internal static string SafeName(string name)
    {
        const string reserved = "<>:\"/\\|?*";
        return name.Length == 0 ? "unnamed" : string.Concat(name.Select(ch => reserved.Contains(ch) ? '_' : ch));
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint seed)
    {
        uint crc = ~seed;
        foreach (byte value in data)
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0);
            table[i] = value;
        }
        return table;
    }
}
