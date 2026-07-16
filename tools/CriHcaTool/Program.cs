using System.Globalization;
using System.Text.Json;
using Level5.AudioTool.Core.Hca;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Adx;
using VGAudio.Containers.Hca;
using VGAudio.Containers.Wave;

if (args.Length < 1)
{
    PrintUsage();
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "encode" => Encode(args[1..]),
        "encode-adx" => EncodeAdx(args[1..]),
        "decode" => Decode(args[1..]),
        "decode-adx" => DecodeAdx(args[1..]),
        "inspect" => Inspect(args[1..]),
        "uncipher-type1" => UncipherType1(args[1..]),
        _ => Fail($"Unknown command: {args[0]}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Encode(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("encode requires INPUT.wav OUTPUT.hca");
    }

    var inputPath = args[0];
    var outputPath = args[1];
    var quality = CriHcaQuality.High;
    var bitrate = 0;
    ulong? keyCode = null;
    int? loopStart = null;
    int? loopEnd = null;
    var noLoop = false;

    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--quality" when i + 1 < args.Length:
                quality = Enum.Parse<CriHcaQuality>(args[++i], ignoreCase: true);
                break;
            case "--bitrate" when i + 1 < args.Length:
                bitrate = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--key" when i + 1 < args.Length:
                keyCode = ParseKey(args[++i]);
                break;
            case "--loop-start" when i + 1 < args.Length:
                loopStart = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--loop-end" when i + 1 < args.Length:
                loopEnd = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--no-loop":
                noLoop = true;
                break;
            default:
                return Fail($"Unknown or incomplete option: {args[i]}");
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");

    var waveReader = new WaveReader();
    using var input = File.OpenRead(inputPath);
    var audio = waveReader.ReadFormat(input);
    if (noLoop)
    {
        audio = audio.WithLoop(false);
    }
    else if (loopStart is not null || loopEnd is not null)
    {
        if (loopStart is null || loopEnd is null)
        {
            return Fail("Both --loop-start and --loop-end are required when setting loop points.");
        }

        if (loopStart.Value < 0 || loopEnd.Value <= loopStart.Value || loopEnd.Value > audio.SampleCount)
        {
            return Fail($"Invalid loop range {loopStart.Value}..{loopEnd.Value} for {audio.SampleCount} samples.");
        }

        audio = audio.WithLoop(true, loopStart.Value, loopEnd.Value);
    }

    var configuration = new HcaConfiguration
    {
        Quality = quality,
        TrimFile = false
    };
    if (bitrate > 0)
    {
        configuration.Bitrate = bitrate;
        configuration.LimitBitrate = true;
    }
    if (keyCode is not null)
    {
        configuration.EncryptionKey = new CriHcaKey(keyCode.Value);
    }

    var writer = new HcaWriter();
    using var output = File.Create(outputPath);
    writer.WriteToStream(audio, output, configuration);
    output.Dispose();

    using var encodedInput = File.OpenRead(outputPath);
    var encodedMetadata = new HcaReader().ReadMetadata(encodedInput);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        input = inputPath,
        output = outputPath,
        quality = quality.ToString(),
        bitrate = bitrate > 0 ? bitrate : null as int?,
        frameSize = encodedMetadata.Hca.FrameSize,
        minResolution = encodedMetadata.Hca.MinResolution,
        encrypted = keyCode is not null,
        sampleCount = audio.SampleCount,
        looping = audio.Looping,
        loopStart = audio.Looping ? audio.LoopStart : null as int?,
        loopEnd = audio.Looping ? audio.LoopEnd : null as int?,
        hcaSampleCount = encodedMetadata.Hca.SampleCount,
        hcaSampleRate = encodedMetadata.Hca.SampleRate,
        hcaChannelCount = encodedMetadata.Hca.ChannelCount,
        hcaEncryptionType = encodedMetadata.Hca.EncryptionType,
        hcaLooping = encodedMetadata.Hca.Looping,
        hcaLoopStart = encodedMetadata.Hca.Looping ? encodedMetadata.Hca.LoopStartSample : null as int?,
        hcaLoopEnd = encodedMetadata.Hca.Looping ? encodedMetadata.Hca.LoopEndSample : null as int?,
        outputSize = new FileInfo(outputPath).Length
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static int Inspect(string[] args)
{
    if (args.Length < 1)
    {
        return Fail("inspect requires INPUT.hca");
    }

    var reader = new HcaReader();
    if (args.Length >= 3 && args[1] == "--key")
    {
        reader.Decrypt = true;
        reader.EncryptionKey = new CriHcaKey(ParseKey(args[2]));
    }

    using var input = File.OpenRead(args[0]);
    var metadata = reader.ReadMetadata(input);
    Console.WriteLine(JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static int Decode(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("decode requires INPUT.hca OUTPUT.wav");
    }

    var inputPath = args[0];
    var outputPath = args[1];
    ulong? keyCode = null;
    for (var i = 2; i < args.Length; i++)
    {
        if (args[i] == "--key" && i + 1 < args.Length)
        {
            keyCode = ParseKey(args[++i]);
            continue;
        }

        return Fail($"Unknown or incomplete option: {args[i]}");
    }

    var reader = new HcaReader();
    if (keyCode is not null)
    {
        reader.Decrypt = true;
        reader.EncryptionKey = new CriHcaKey(keyCode.Value);
    }

    using var input = File.OpenRead(inputPath);
    var audio = reader.ReadFormat(input);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    using var output = File.Create(outputPath);
    new WaveWriter().WriteToStream(audio, output);
    return 0;
}

static int DecodeAdx(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("decode-adx requires INPUT.adx OUTPUT.wav");
    }

    var inputPath = args[0];
    var outputPath = args[1];
    using var input = File.OpenRead(inputPath);
    var audio = new AdxReader().ReadFormat(input);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    using var output = File.Create(outputPath);
    new WaveWriter().WriteToStream(audio, output);
    return 0;
}

static int EncodeAdx(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("encode-adx requires INPUT.wav OUTPUT.adx");
    }

    var inputPath = args[0];
    var outputPath = args[1];
    var noLoop = args.Skip(2).Contains("--no-loop", StringComparer.OrdinalIgnoreCase);
    if (args.Skip(2).Any(argument => !string.Equals(argument, "--no-loop", StringComparison.OrdinalIgnoreCase)))
    {
        return Fail("encode-adx only supports --no-loop.");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    var waveReader = new WaveReader();
    using var input = File.OpenRead(inputPath);
    var audio = waveReader.ReadFormat(input);
    if (noLoop)
    {
        audio = audio.WithLoop(false);
    }

    var writer = new AdxWriter();
    using var output = File.Create(outputPath);
    writer.WriteToStream(audio, output, new AdxConfiguration { Version = 4 });
    output.Dispose();

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        codec = "ADX",
        input = inputPath,
        output = outputPath,
        hcaSampleCount = audio.SampleCount,
        hcaSampleRate = audio.SampleRate,
        hcaChannelCount = audio.ChannelCount,
        hcaEncryptionType = 0,
        hcaLooping = audio.Looping,
        outputSize = new FileInfo(outputPath).Length
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static int UncipherType1(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("uncipher-type1 requires INPUT.hca OUTPUT.hca");
    }

    var inputPath = args[0];
    var outputPath = args[1];
    var data = File.ReadAllBytes(inputPath);
    var changed = HcaType1Uncipher.TryConvertToPlain(data, out var outputData);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    File.WriteAllBytes(outputPath, outputData);

    using var input = File.OpenRead(outputPath);
    var metadata = new HcaReader().ReadMetadata(input);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        input = inputPath,
        output = outputPath,
        changed,
        hcaVersion = metadata.Version,
        hcaEncryptionType = metadata.Hca.EncryptionType,
        hcaSampleRate = metadata.Hca.SampleRate,
        hcaChannelCount = metadata.Hca.ChannelCount,
        hcaSampleCount = metadata.Hca.SampleCount,
        outputSize = new FileInfo(outputPath).Length
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static ulong ParseKey(string value)
{
    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        return ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    return ulong.Parse(value, CultureInfo.InvariantCulture);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  CriHcaTool encode INPUT.wav OUTPUT.hca [--quality High] [--bitrate N] [--key KEY] [--loop-start N --loop-end N|--no-loop]");
    Console.Error.WriteLine("  CriHcaTool encode-adx INPUT.wav OUTPUT.adx [--no-loop]");
    Console.Error.WriteLine("  CriHcaTool decode INPUT.hca OUTPUT.wav [--key KEY]");
    Console.Error.WriteLine("  CriHcaTool decode-adx INPUT.adx OUTPUT.wav");
    Console.Error.WriteLine("  CriHcaTool inspect INPUT.hca [--key KEY]");
    Console.Error.WriteLine("  CriHcaTool uncipher-type1 INPUT.hca OUTPUT.hca");
}
