using System.Globalization;

namespace NetControl.OuiPacker;

/// <summary>
/// Options in `--name value` and `--flag` form. Deliberately tiny: a command line parser is a
/// dependency, and this tool exists partly to demonstrate that the engine needs almost none.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first argument is the command; everything after it is options.</summary>
    public CommandLine(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected an option starting with '--', got '{args[i]}'.");
            }

            string name = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);

            _options[name] = hasValue ? args[++i] : null;
        }
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out string? value) ? value : null;

    public string Required(string name) =>
        Value(name) ?? throw new ArgumentException($"--{name} is required and needs a value.");

    public int Int(string name, int fallback) =>
        Value(name) is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : fallback;

    /// <summary>A date as yyyy-MM-dd, read as UTC. Absent means "use the fallback".</summary>
    public DateTime Date(string name, DateTime fallback) =>
        Value(name) is { } text
            ? DateTime.SpecifyKind(
                DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeKind.Utc)
            : fallback;
}
