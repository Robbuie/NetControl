using System.Globalization;
using System.Net;
using NetControl.Core.Plan;

namespace NetControl.Core;

/// <summary>
/// The subnet calculator: what a person types, read the way a person means it.
///
/// <para>"192.168.1.51/24", "192.168.1.51 255.255.255.0" and "192.168.1.51/255.255.255.0" all mean
/// the same thing and are all accepted. Addresses are parsed by <see cref="PlanValidation.TryParseIPv4"/>
/// rather than <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>, for the reason the plan
/// already gives: the framework reads "192.168.1" as 192.168.0.1, and a calculator that silently
/// answers a different question from the one typed is worse than one that refuses.</para>
/// </summary>
public static class SubnetCalculation
{
    private static readonly char[] Separators = ['/', ' ', '\t'];

    /// <summary>Parses one line - an address and a prefix or mask - into a checked subnet.</summary>
    /// <param name="text">What was typed.</param>
    /// <param name="subnet">The subnet, carrying the typed address as its host.</param>
    /// <param name="problem">Why it did not parse, in a sentence. Null on success.</param>
    public static bool TryParse(string? text, out Ipv4Subnet subnet, out string? problem)
    {
        subnet = default;
        problem = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "Type an address and a prefix or mask, e.g. 192.168.1.51/24 or 192.168.1.51 255.255.255.0.";
            return false;
        }

        string[] parts = text.Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            problem = $"'{text.Trim()}' needs an address and a prefix or mask - e.g. 192.168.1.51/24.";
            return false;
        }

        if (!PlanValidation.TryParseIPv4(parts[0], out IPAddress? address))
        {
            problem = $"'{parts[0]}' is not an IPv4 address. Expected four numbers, e.g. 192.168.1.51.";
            return false;
        }

        if (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix))
        {
            if (Ipv4Subnet.TryFromPrefix(address, prefix, out subnet))
            {
                return true;
            }

            problem = $"/{prefix} is not a prefix length. It runs from /0 to /32.";
            return false;
        }

        if (!PlanValidation.TryParseIPv4(parts[1], out IPAddress? mask))
        {
            problem = $"'{parts[1]}' is neither a prefix length nor a subnet mask.";
            return false;
        }

        if (!Ipv4Subnet.TryCreate(address, mask, out subnet))
        {
            problem = $"{mask} is not a valid subnet mask - its ones are not contiguous. A mask is a run of ones "
                + "then a run of zeros, e.g. 255.255.255.0 or 255.255.255.192.";
            return false;
        }

        return true;
    }
}
