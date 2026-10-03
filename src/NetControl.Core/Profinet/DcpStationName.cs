using System.Text.RegularExpressions;

namespace NetControl.Core.Profinet;

/// <summary>
/// The rules for a NameOfStation (IEC 61158-6-10, DNS-compatible). Checked before a Set is sent,
/// because a device that refuses a name gives a one-byte error code, and one that accepts a name the
/// controller cannot match is a device that never connects.
/// </summary>
public static partial class DcpStationName
{
    public const int MaxLength = 240;
    public const int MaxLabel = 63;

    /// <summary>The problem with a name, or null when it is valid.</summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "A station name is needed.";
        }

        if (name.Length > MaxLength)
        {
            return $"At most {MaxLength} characters.";
        }

        if (name.Any(char.IsUpper))
        {
            return "Lowercase only - a controller compares names in lowercase, and most devices refuse capitals.";
        }

        foreach (string label in name.Split('.'))
        {
            if (label.Length == 0)
            {
                return "No empty parts: no leading, trailing or doubled dots.";
            }

            if (label.Length > MaxLabel)
            {
                return $"Each dot-separated part is at most {MaxLabel} characters.";
            }

            if (!ValidLabel().IsMatch(label))
            {
                return $"'{label}': letters a-z, digits and '-' only, not starting or ending with '-'.";
            }
        }

        if (PortName().IsMatch(name))
        {
            return "A name may not start with 'port-nnn' - that form is reserved for port names.";
        }

        if (AddressLike().IsMatch(name))
        {
            return "A name may not look like an IP address.";
        }

        return null;
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidLabel();

    [GeneratedRegex(@"^port-\d{3}(-\d{5})?(\.|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PortName();

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressLike();
}
