using System.Globalization;

namespace Level5.AudioTool.Core.Cri;

internal sealed class CriArguments
{
    private static readonly HashSet<string> Flags =
    [
        "summary", "no-loop", "no-vgmstream-download", "no-ffmpeg-download",
        "keep-hca", "match-original-hca-profile", "memory", "patch-cue-lengths"
    ];
    private readonly List<string> _positions = [];
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    internal CriArguments(IReadOnlyList<string> arguments, string audioRoot, string dataRoot)
    {
        if (arguments.Count == 0)
            throw new ArgumentException("Missing audio command.", nameof(arguments));
        Command = arguments[0];
        AudioRoot = audioRoot;
        DataRoot = dataRoot;

        for (int i = 1; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                _positions.Add(arg);
                continue;
            }
            var key = arg[2..];
            if (!_options.TryGetValue(key, out var values))
                _options.Add(key, values = []);
            if (!Flags.Contains(key))
            {
                if (++i >= arguments.Count)
                    throw new ArgumentException($"Missing value for --{key}.");
                values.Add(arguments[i]);
            }
        }
    }

    internal string Command { get; }
    internal string AudioRoot { get; }
    internal string DataRoot { get; }
    internal string Position(int index) => index < _positions.Count
        ? _positions[index] : throw new ArgumentException($"Missing argument {index + 1} for {Command}.");
    internal bool Has(string name) => _options.ContainsKey(name);
    internal IReadOnlyList<string> All(string name) => _options.TryGetValue(name, out var values) ? values : [];
    internal string? Get(string name) => All(name).LastOrDefault();
    internal string Require(string name) => Get(name) ?? throw new ArgumentException($"Missing --{name}.");
    internal int? Int(string name) => Get(name) is { } value ? ParseInteger(value) : null;
    internal int Int(string name, int fallback) => Int(name) ?? fallback;

    internal static int ParseInteger(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(value, CultureInfo.InvariantCulture);
}
