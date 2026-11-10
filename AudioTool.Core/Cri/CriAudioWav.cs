using System.Buffers.Binary;
using System.Text;

namespace Level5.AudioTool.Core.Cri;

internal sealed class CriAudioWav
{
    private readonly byte[] bytes;
    private readonly int sampleOffset;
    private readonly int sampleLength;
    public int SampleRate { get; }
    public int Channels { get; }
    public int SampleWidth { get; }
    public int FrameCount => sampleLength / FrameSize;
    public int FrameSize => checked(Channels * SampleWidth);
    public (int Start, int End)? Loop { get; }

    private CriAudioWav(byte[] bytes, int sampleOffset, int sampleLength,
        int sampleRate, int channels, int sampleWidth, (int, int)? loop)
    {
        this.bytes = bytes;
        this.sampleOffset = sampleOffset;
        this.sampleLength = sampleLength;
        SampleRate = sampleRate;
        Channels = channels;
        SampleWidth = sampleWidth;
        Loop = loop;
    }

    public static CriAudioWav Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected a RIFF/WAVE audio file.");

        int channels = 0, sampleRate = 0, sampleWidth = 0, dataOffset = 0, dataLength = 0;
        (int, int)? loop = null;
        for (int offset = 12; offset <= bytes.Length - 8;)
        {
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
            var id = bytes.AsSpan(offset, 4);
            offset += 8;
            if (size > bytes.Length - offset)
                throw new InvalidDataException("Truncated WAVE chunk.");
            var chunk = bytes.AsSpan(offset, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16)
                    throw new InvalidDataException("Truncated WAVE format.");
                int format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                if (format == 0xfffe && size >= 40 &&
                    chunk[24..40].SequenceEqual(new byte[] { 1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71 }))
                    format = 1;
                if (format != 1)
                    throw new InvalidDataException("WAVE file is not integer PCM.");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                sampleWidth = (BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) + 7) / 8;
            }
            else if (dataOffset == 0 && id.SequenceEqual("data"u8))
            {
                dataOffset = offset;
                dataLength = size;
            }
            else if (loop is null && id.SequenceEqual("smpl"u8) && size >= 60 &&
                BinaryPrimitives.ReadUInt32LittleEndian(chunk[28..]) != 0)
            {
                loop = (checked((int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[44..])),
                    checked((int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[48..]) + 1));
            }
            offset = checked(offset + size + (size & 1));
        }
        if (channels <= 0 || sampleRate <= 0 || sampleWidth is < 1 or > 4 || dataOffset == 0)
            throw new InvalidDataException("WAVE file has no usable integer PCM format or sample data.");
        return new CriAudioWav(bytes, dataOffset, dataLength, sampleRate, channels, sampleWidth, loop);
    }

    public Dictionary<string, object?> Info(string path)
    {
        var peaks = new List<double>();
        var framesPerBucket = Math.Max(1, FrameCount / 512);
        double maxValue = SampleWidth == 1 ? 128d : (1L << (SampleWidth * 8 - 1)) - 1;
        for (var frame = 0; frame < FrameCount && peaks.Count < 512; frame += framesPerBucket)
        {
            var frames = Math.Min(framesPerBucket, FrameCount - frame);
            var data = bytes.AsSpan(sampleOffset + frame * FrameSize, frames * FrameSize);
            long peak = 0;
            for (int offset = 0; offset < data.Length; offset += SampleWidth)
            {
                var sample = data[offset..];
                int value = SampleWidth switch
                {
                    1 => sample[0] - 128,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample),
                    3 => (sample[0] << 8 | sample[1] << 16 | sample[2] << 24) >> 8,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(sample),
                };
                peak = Math.Max(peak, Math.Abs((long)value));
            }
            peaks.Add(Math.Min(1d, peak / maxValue));
        }
        return new()
        {
            ["path"] = path, ["sample_count"] = FrameCount, ["sample_rate"] = SampleRate,
            ["channels"] = Channels, ["sample_width_bytes"] = SampleWidth,
            ["duration_seconds"] = (double)FrameCount / SampleRate, ["peaks"] = peaks,
            ["loop"] = Loop is { } loop ? new { start = loop.Start, end = loop.End, duration_samples = loop.End - loop.Start } : null,
        };
    }

    public int WriteLoop(string path, int playStart, int loopStart, int loopEnd, int requested)
    {
        int first = loopEnd - playStart, repeat = loopEnd - loopStart;
        int repeats = Math.Max(0, checked(requested - first + repeat - 1) / repeat);
        int written = checked(first + repeats * repeat);
        int dataSize = checked(written * FrameSize);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = new BinaryWriter(File.Create(path), Encoding.UTF8, leaveOpen: false);
        output.Write("RIFF"u8);
        output.Write(checked(dataSize + 36));
        output.Write("WAVEfmt "u8);
        output.Write(16);
        output.Write((ushort)1);
        output.Write(checked((ushort)Channels));
        output.Write(SampleRate);
        output.Write(checked(SampleRate * FrameSize));
        output.Write(checked((ushort)FrameSize));
        output.Write(checked((ushort)(SampleWidth * 8)));
        output.Write("data"u8);
        output.Write(dataSize);
        output.Write(bytes.AsSpan(sampleOffset + playStart * FrameSize, first * FrameSize));
        for (int i = 0; i < repeats; i++)
            output.Write(bytes.AsSpan(sampleOffset + loopStart * FrameSize, repeat * FrameSize));
        return written;
    }
}
