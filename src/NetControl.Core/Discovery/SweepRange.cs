using System.Globalization;
using System.Net;
using NetControl.Core.Plan;
using NetControl.Core.Reachability;

namespace NetControl.Core.Discovery;

/// <summary>
/// A block of addresses somebody typed for a scan to sweep: a subnet in CIDR form, a range, or one
/// address. This is how a scan reaches a subnet that is not on the laptop's own cable - another VLAN
/// behind the plant's layer-3 switch.
///
/// <para>Typed, never inferred. Every other sweep in this tool computes its range from the adapter's
/// address and mask; this is the one place a person names addresses, which is why parsing is strict
/// - "10.20.5" is refused rather than read as 10.20.0.5 - and why a range that is not one host at
/// either end, or is wider than <see cref="SubnetSweepOptions.MaxAddresses"/>, is refused before
/// anything is sent.</para>
/// </summary>
public sealed record SweepRange
{
    private SweepRange(uint first, uint last, string text, IPAddress? mask)
    {
        FirstBits = first;
        LastBits = last;
        Text = text;
        Mask = mask;
    }

    /// <summary>What was typed, tidied: "10.20.5.0/24", "10.20.6.10-10.20.6.50", "10.20.7.4".</summary>
    public string Text { get; }

    /// <summary>The subnet mask, when the range was written as CIDR - so a sweep can refuse its broadcast.</summary>
    public IPAddress? Mask { get; }

    public IPAddress First => FromUInt32(FirstBits);

    public IPAddress Last => FromUInt32(LastBits);

    /// <summary>How many addresses a sweep of this range pings.</summary>
    public int Count => (int)(LastBits - FirstBits + 1);

    private uint FirstBits { get; }

    private uint LastBits { get; }

    /// <summary>Every address in the range, in order.</summary>
    public IEnumerable<IPAddress> Addresses()
    {
        for (uint bits = FirstBits; bits <= LastBits && bits >= FirstBits; bits++)
        {
            yield return FromUInt32(bits);
        }
    }

    public bool Contains(IPAddress? address) =>
        address is not null && TryBits(address, out uint bits) && bits >= FirstBits && bits <= LastBits;

    /// <summary>Whether every address in the range is inside <paramref name="subnet"/>.</summary>
    public bool IsWithin(Ipv4Subnet subnet) => subnet.Contains(First) && subnet.Contains(Last);

    public override string ToString() => Text;

    /// <summary>
    /// Reads a list of ranges, separated by commas, semicolons, spaces or new lines. Accepts
    /// <c>10.20.5.0/24</c>, <c>10.20.6.10-10.20.6.50</c>, <c>10.20.6.10-50</c> and <c>10.20.7.4</c>.
    /// An empty text is an empty list, not a problem.
    /// </summary>
    /// <param name="problem">Every problem at once, one per line, naming the piece that caused it.</param>
    public static bool TryParseList(
        string? text, int maxAddressesEach, out IReadOnlyList<SweepRange> ranges, out string? problem)
    {
        var parsed = new List<SweepRange>();
        var problems = new List<string>();

        foreach (string piece in (text ?? string.Empty).Split(
            [',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParse(piece, maxAddressesEach, out SweepRange? range, out string? why))
            {
                if (!parsed.Any(r => r.FirstBits == range!.FirstBits && r.LastBits == range.LastBits))
                {
                    parsed.Add(range!);
                }
            }
            else
            {
                problems.Add(why!);
            }
        }

        ranges = parsed;
        problem = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
        return problems.Count == 0;
    }

    /// <summary>One range. See <see cref="TryParseList"/> for the forms accepted.</summary>
    public static bool TryParse(string? text, int maxAddresses, out SweepRange? range, out string? problem)
    {
        range = null;
        problem = null;
        string piece = (text ?? string.Empty).Trim();

        if (piece.Length == 0)
        {
            problem = "Nothing was typed.";
            return false;
        }

        uint first;
        uint last;
        IPAddress? mask = null;
        string tidy;

        int slash = piece.IndexOf('/', StringComparison.Ordinal);
        int dash = piece.IndexOf('-', StringComparison.Ordinal);

        if (slash >= 0)
        {
            if (!PlanValidation.TryParseIPv4(piece[..slash], out IPAddress? network) || network is null
                || !int.TryParse(piece[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)
                || prefix is < 1 or > 32
                || !Ipv4Subnet.TryFromPrefix(network, prefix, out Ipv4Subnet subnet))
            {
                problem = $"'{piece}' is not a subnet. Write it like 10.20.5.0/24.";
                return false;
            }

            TryBits(subnet.FirstHost, out first);
            TryBits(subnet.LastHost, out last);
            mask = subnet.Mask;
            tidy = subnet.ToString();
        }
        else if (dash >= 0)
        {
            string left = piece[..dash].Trim();
            string right = piece[(dash + 1)..].Trim();

            if (!PlanValidation.TryParseIPv4(left, out IPAddress? from) || from is null)
            {
                problem = $"'{piece}' does not start with an address. Write a range like 10.20.6.10-10.20.6.50 or 10.20.6.10-50.";
                return false;
            }

            IPAddress? to;
            if (byte.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out byte lastOctet))
            {
                byte[] bytes = from.GetAddressBytes();
                bytes[3] = lastOctet;
                to = new IPAddress(bytes);
            }
            else if (!PlanValidation.TryParseIPv4(right, out to) || to is null)
            {
                problem = $"'{piece}' does not end with an address. Write a range like 10.20.6.10-10.20.6.50 or 10.20.6.10-50.";
                return false;
            }

            TryBits(from, out first);
            TryBits(to, out last);

            if (last < first)
            {
                problem = $"'{piece}' ends before it starts.";
                return false;
            }

            tidy = $"{from}-{to}";
        }
        else
        {
            if (!PlanValidation.TryParseIPv4(piece, out IPAddress? one) || one is null)
            {
                problem = $"'{piece}' is not an address, a range or a subnet.";
                return false;
            }

            TryBits(one, out first);
            last = first;
            tidy = one.ToString();
        }

        long count = (long)last - first + 1;
        if (count > maxAddresses)
        {
            problem = string.Create(
                CultureInfo.InvariantCulture,
                $"'{piece}' is {count:N0} addresses, more than the {maxAddresses:N0} one range may hold. Split it, or narrow it to the part of the plant you mean.");
            return false;
        }

        // Both ends must be one host. Anything in between is then too, since the reserved and
        // multicast blocks sit at the top of the space and 0.0.0.0 at the bottom.
        foreach (uint end in new[] { first, last })
        {
            if (!UnicastTarget.TryCheck(FromUInt32(end), null, out string? why))
            {
                problem = $"'{piece}': {why}";
                return false;
            }
        }

        range = new SweepRange(first, last, tidy, mask);
        return true;
    }

    private static bool TryBits(IPAddress address, out uint bits)
    {
        bits = 0;
        byte[] b = address.GetAddressBytes();
        if (b.Length != 4)
        {
            return false;
        }

        bits = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }

    private static IPAddress FromUInt32(uint bits) =>
        new([(byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits]);
}
