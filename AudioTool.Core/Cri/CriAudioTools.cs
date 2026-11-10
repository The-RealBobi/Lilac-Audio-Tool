using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Level5.AudioTool.Core.Cri;

internal sealed class CriAudioTools(CriArguments args)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly object Downloads = new();
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string Execute(string executable, IEnumerable<string> arguments, int timeoutMilliseconds, bool allowNonZero = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"{executable} did not finish in time.");
        }
        string output = stdout.GetAwaiter().GetResult();
        string errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 && !allowNonZero)
            throw new IOException($"{executable} failed ({process.ExitCode}): {errors.Trim()} {output.Trim()}".TrimEnd());
        return output + errors;
    }

    private static bool Probe(string executable, string option, string? expectedText)
    {
        try
        {
            var output = Execute(executable, [option], 3000, allowNonZero: expectedText is not null);
            return expectedText is null || output.Contains(expectedText, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<string> Candidates(string name, string? preferred)
    {
        var executable = Exe(name);
        if (!string.IsNullOrWhiteSpace(preferred))
            yield return preferred;
        yield return executable;
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine("/opt/homebrew/bin", executable);
            yield return Path.Combine("/opt/homebrew/opt", name == "vgmstream-cli" ? "vgmstream" : name, "bin", executable);
            yield return Path.Combine("/usr/local/bin", executable);
        }
        foreach (string root in new[] { args.DataRoot, args.AudioRoot }.Distinct(StringComparer.Ordinal))
        {
            var plugins = Path.Combine(root, "PlugIns");
            yield return Path.Combine(plugins, executable);
            if (!Directory.Exists(plugins))
                continue;
            foreach (string path in Directory.EnumerateFiles(plugins, executable,
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                yield return path;
        }
    }

    public (string Path, string Source) Ffmpeg(bool download)
    {
        string cache = Path.Combine(args.DataRoot, ".cache", "dependencies");
        string cached = Path.Combine(cache, Exe("ffmpeg"));
        foreach (var path in Candidates("ffmpeg", Environment.GetEnvironmentVariable("L5_AUDIO_FFMPEG_PATH")).Append(cached))
            if (Probe(path, "-version", null))
                return (path, "existing");
        if (!download)
            throw new FileNotFoundException("FFmpeg not found.");

        var (asset, digest) = (OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(), OperatingSystem.IsLinux()) switch
        {
            (true, _, _) => ("ffmpeg-darwin-arm64.gz", "6be74d6f449889c2e87a75873894f8520cad56c08ac76f2a628d85b0519daaca"),
            (_, true, _) => ("ffmpeg-win32-x64.gz", "450d66226c79405c724e821f291cab0911e934bfa9fa2231adcab587f3e07b50"),
            (_, _, true) => ("ffmpeg-linux-x64.gz", "17c1ae10b52ac499180679fe6ba77e17642390c4eedb0f1e3b0ac045da55128f"),
            _ => throw new PlatformNotSupportedException("No bundled FFmpeg for this platform."),
        };
        lock (Downloads)
        {
            if (Probe(cached, "-version", null))
                return (cached, "existing");
            Directory.CreateDirectory(cache);
            using var work = new CriAudioTemp(cache);
            var archive = Path.Combine(work.Path, asset);
            using (var content = Http.GetStreamAsync("https://github.com/eugeneware/ffmpeg-static/releases/download/b6.0/" + asset).GetAwaiter().GetResult())
            using (var file = File.Create(archive))
                content.CopyTo(file);
            using var compressed = File.OpenRead(archive);
            if (!Convert.ToHexString(SHA256.HashData(compressed)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The FFmpeg download failed its checksum.");
            compressed.Position = 0;
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            var unpacked = Path.Combine(work.Path, Exe("ffmpeg"));
            using (var file = File.Create(unpacked))
                gzip.CopyTo(file);
            SetExecutable(unpacked);
            if (!Probe(unpacked, "-version", null))
                throw new IOException("The downloaded FFmpeg executable could not start.");
            File.Move(unpacked, cached, overwrite: true);
        }
        return (cached, "downloaded");
    }

    public (string? Path, string Source) Vgmstream(bool download)
    {
        foreach (var candidate in new[] { args.Get("vgmstream") }.OfType<string>()
            .Concat(Candidates("vgmstream-cli", Environment.GetEnvironmentVariable("L5_AUDIO_VGMSTREAM_PATH"))))
        {
            if (Probe(candidate, "-h", "vgmstream"))
                return (candidate, "existing");
        }
        if (!download)
            return (null, "missing");

        lock (Downloads)
        {
            var (asset, platform) = OperatingSystem.IsWindows() ? ("vgmstream-win64.zip", "Windows") :
                OperatingSystem.IsMacOS() ? ("vgmstream-mac-cli.tar.gz", "Mac") :
                ("vgmstream-linux-cli.tar.gz", "Linux");
            var destination = Path.Combine(args.DataRoot, "PlugIns", platform, Exe("vgmstream-cli"));
            if (Probe(destination, "-h", "vgmstream"))
                return (destination, "existing");
            using var work = new CriAudioTemp(Path.GetTempPath());
            var archive = Path.Combine(work.Path, asset);
            var extract = Directory.CreateDirectory(Path.Combine(work.Path, "extract")).FullName;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/vgmstream/vgmstream-releases/releases/latest");
            request.Headers.UserAgent.ParseAdd("LilacAudioTool/1");
            using var response = Http.Send(request);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(response.Content.ReadAsStream());
            var selected = json.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("name").GetString() == asset);
            if (selected.ValueKind == JsonValueKind.Undefined)
                return (null, "download-asset-missing");
            string url = selected.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "https" ||
                address.Host != "github.com" || !address.AbsolutePath.StartsWith("/vgmstream/vgmstream-releases/releases/download/", StringComparison.Ordinal))
                return (null, "download-url-missing");
            using (var content = Http.GetStreamAsync(address).GetAwaiter().GetResult())
            using (var file = File.Create(archive))
                content.CopyTo(file);

            if (asset.EndsWith(".zip", StringComparison.Ordinal))
                ZipFile.ExtractToDirectory(archive, extract);
            else
            {
                using var compressed = File.OpenRead(archive);
                using var stream = new GZipStream(compressed, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(stream, extract, overwriteFiles: false);
            }
            string? source = Directory.EnumerateFiles(extract, Exe("vgmstream-cli"), SearchOption.AllDirectories).FirstOrDefault();
            if (source is null)
                return (null, "download-executable-missing");
            string directory = Directory.CreateDirectory(Path.GetDirectoryName(destination)!).FullName;
            File.Copy(source, destination, overwrite: true);
            if (OperatingSystem.IsWindows())
            {
                foreach (string library in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.dll"))
                    File.Copy(library, Path.Combine(directory, Path.GetFileName(library)), overwrite: true);
            }
            else
                SetExecutable(destination);
            return Probe(destination, "-h", "vgmstream") ? (destination, "downloaded") : (null, "download-invalid");
        }
    }

    public string Hca(params string[] command)
    {
        var runtime = Path.Combine(args.AudioRoot, "tools", "CriHcaToolRuntime", Exe("CriHcaTool"));
        if (File.Exists(runtime))
            return Execute(runtime, command, -1);
        var project = args.Get("hca-tool") ?? Path.Combine(args.AudioRoot, "tools", "CriHcaTool", "CriHcaTool.csproj");
        if (!File.Exists(project))
            throw new FileNotFoundException("The HCA helper is missing.", project);
        return Execute("dotnet", ["run", "--project", project, "--", ..command], -1);
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}

internal sealed class CriAudioTemp : IDisposable
{
    public string Path { get; }

    public CriAudioTemp(string parent)
    {
        Path = Directory.CreateDirectory(System.IO.Path.Combine(parent, "cri_audio_" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
