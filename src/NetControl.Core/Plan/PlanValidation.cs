using System.Diagnostics.CodeAnalysis;
using System.Net;
using NetControl.Core.Persistence;

namespace NetControl.Core.Plan;

/// <summary>
/// Checks a whole plan file before any of it is written, and either returns the devices or every
/// reason it will not.
///
/// <para><b>One pass over the whole file, and nothing is imported if anything is wrong.</b> That is
/// the rule the type exists for. A partial import leaves somebody standing at a panel with some
/// devices planned and some not, and no way to tell which without reading the file against the grid
/// row by row - which is worse than an import that refused and said why.</para>
///
/// <para>Every problem names the line and the likely cause. The checks that need to know about
/// <em>this machine</em> - the laptop's own address, and whether the plan is even on the adapter's
/// segment - run only when the caller supplied an adapter in <see cref="PlanImportContext"/>, which
/// is how a plan for a segment nobody is on yet still imports.</para>
///
/// <para>Deliberately not checked: whether an address is already in use out on the wire. The file
/// cannot know that and neither can this - it is what the scan is for.</para>
/// </summary>
public static class PlanValidation
{
    public static PlanValidationResult Validate(
        IReadOnlyList<PlanCsvRow> rows,
        PlanImportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        context ??= PlanImportContext.None;

        var problems = new List<PlanProblem>();
        var devices = new List<DeviceRecord>(rows.Count);
        var macsSeen = new Dictionary<MacAddress, int>();
        var ipsSeen = new Dictionary<IPAddress, (int Line, string Mac)>();

        foreach (PlanCsvRow row in rows)
        {
            int before = problems.Count;

            MacAddress mac = CheckMac(row, problems, macsSeen);
            IPAddress? ip = CheckIp(row, problems, context);
            IPAddress? mask = CheckMask(row, problems);
            IPAddress? gateway = CheckGateway(row, problems);

            CheckAgainstOwnSubnet(row, ip, mask, problems);
            CheckForDuplicateIp(row, mac, ip, problems, ipsSeen);

            if (problems.Count != before)
            {
                // Keep validating the rest of the file - the point is to report every problem in
                // one go - but this row will not become a device.
                continue;
            }

            devices.Add(new DeviceRecord
            {
                Mac = mac,
                PlannedIp = ip,
                PlannedMask = mask,
                PlannedGateway = gateway,
                HostName = Blank(row.HostName),
                PanelRef = Blank(row.PanelRef),
                Role = Blank(row.Role),
                Notes = Blank(row.Notes),
                Vendor = context.Oui?.Lookup(mac),
            });
        }

        return problems.Count == 0
            ? PlanValidationResult.Imported(devices)
            : PlanValidationResult.Refused(problems);
    }

    private static MacAddress CheckMac(
        PlanCsvRow row,
        List<PlanProblem> problems,
        Dictionary<MacAddress, int> seen)
    {
        if (row.Mac.Length == 0)
        {
            Add(problems, row, "no MAC address. The plan is keyed on the MAC, so a row without one "
                + "cannot be matched to a device - take it off the label or off the module.");
            return MacAddress.Empty;
        }

        if (!MacAddress.TryParse(row.Mac, out MacAddress mac))
        {
            Add(problems, row, $"'{row.Mac}' is not a MAC address. Expected six hex bytes, "
                + "e.g. 00:1D:9C:C7:B0:70.");
            return MacAddress.Empty;
        }

        if (mac.IsEmpty)
        {
            Add(problems, row, "the MAC is all zeros, which is the placeholder for 'not known yet' "
                + "and is never served. Read the real address off the module's label.");
            return MacAddress.Empty;
        }

        if (mac.IsBroadcast)
        {
            Add(problems, row, "FF:FF:FF:FF:FF:FF is the broadcast address, not a device.");
            return MacAddress.Empty;
        }

        if (mac.IsMulticast)
        {
            Add(problems, row, $"{mac} has the group bit set in its first octet, so it is not a real "
                + "adapter address. Check for a transposed pair of digits.");
            return MacAddress.Empty;
        }

        if (seen.TryGetValue(mac, out int first))
        {
            Add(problems, row, $"{mac} is already planned on line {first}. A MAC identifies one "
                + "device, so one of the two rows belongs to something else.");
            return mac;
        }

        seen[mac] = row.LineNumber;
        return mac;
    }

    /// <summary>
    /// The planned address, or null when the cell is empty - which is allowed. A MAC written off a
    /// label with the address still to be decided is a normal row, and the grid shows it amber until
    /// it has one. What is not allowed is an address that is not an address.
    /// </summary>
    private static IPAddress? CheckIp(PlanCsvRow row, List<PlanProblem> problems, PlanImportContext context)
    {
        if (row.Ip.Length == 0)
        {
            return null;
        }

        if (!TryParseIPv4(row.Ip, out IPAddress? ip))
        {
            Add(problems, row, $"'{row.Ip}' is not an IPv4 address. Expected four numbers 0-255, "
                + "e.g. 192.168.1.51.");
            return null;
        }

        if (context.AdapterAddress is { } own && ip.Equals(own))
        {
            Add(problems, row, $"{ip} is this machine's own address on {context.AdapterName}. Serving it "
                + "to a device would leave two things answering for it and take the segment down for both.");
            return null;
        }

        if (context.AdapterSubnet is { } segment && !segment.Contains(ip))
        {
            Add(problems, row, $"{ip} is not on {context.AdapterName}'s subnet ({segment}). The device "
                + "would get an address this machine cannot reach, so the readback after Set static "
                + "would report Unverified for a write that worked. Select the adapter for that "
                + "subnet, or correct the address.");
            return null;
        }

        return ip;
    }

    private static IPAddress? CheckMask(PlanCsvRow row, List<PlanProblem> problems)
    {
        if (row.Mask.Length == 0)
        {
            return null;
        }

        if (!TryParseIPv4(row.Mask, out IPAddress? mask))
        {
            Add(problems, row, $"'{row.Mask}' is not a subnet mask. Expected four numbers 0-255, "
                + "e.g. 255.255.255.0.");
            return null;
        }

        if (!Ipv4Subnet.IsContiguousMask(mask))
        {
            Add(problems, row, $"{mask} is not a valid subnet mask: its set bits have a hole in them. "
                + "A mask is a run of ones followed by a run of zeros, e.g. 255.255.255.0 or 255.255.255.192.");
            return null;
        }

        return mask;
    }

    private static IPAddress? CheckGateway(PlanCsvRow row, List<PlanProblem> problems)
    {
        if (row.Gateway.Length == 0)
        {
            return null;
        }

        if (!TryParseIPv4(row.Gateway, out IPAddress? gateway))
        {
            Add(problems, row, $"'{row.Gateway}' is not an IPv4 address, so it cannot be a gateway. "
                + "Leave the cell empty if the device does not need one.");
            return null;
        }

        return gateway;
    }

    /// <summary>
    /// The two addresses on a segment that belong to the segment rather than to any device. This
    /// needs the row's own mask, so it cannot run when the mask is missing or was rejected - which
    /// is correct: without a mask there is no segment to be at the edge of.
    /// </summary>
    private static void CheckAgainstOwnSubnet(
        PlanCsvRow row,
        IPAddress? ip,
        IPAddress? mask,
        List<PlanProblem> problems)
    {
        if (ip is null || mask is null || !Ipv4Subnet.TryCreate(ip, mask, out Ipv4Subnet subnet))
        {
            return;
        }

        if (ip.Equals(subnet.Network))
        {
            Add(problems, row, $"{ip} is the network address of {subnet}, which no device may hold.");
            return;
        }

        if (ip.Equals(subnet.Broadcast))
        {
            Add(problems, row, $"{ip} is the broadcast address of {subnet}, which no device may hold. "
                + "If the mask is wider than you meant, this is where it shows.");
        }
    }

    private static void CheckForDuplicateIp(
        PlanCsvRow row,
        MacAddress mac,
        IPAddress? ip,
        List<PlanProblem> problems,
        Dictionary<IPAddress, (int Line, string Mac)> seen)
    {
        if (ip is null)
        {
            return;
        }

        if (seen.TryGetValue(ip, out (int Line, string Mac) first))
        {
            Add(problems, row, $"{ip} is already planned for {first.Mac} on line {first.Line}. Two "
                + "devices on one address is the fault that is hardest to find on a panel and easiest "
                + "to prevent here.");
            return;
        }

        seen[ip] = (row.LineNumber, mac.IsEmpty ? "the device above" : mac.ToString());
    }

    private static void Add(List<PlanProblem> problems, PlanCsvRow row, string message) =>
        problems.Add(new PlanProblem(row.LineNumber, message));

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Four dotted decimal octets, and nothing else.
    ///
    /// <para><see cref="IPAddress.TryParse(string, out IPAddress)"/> is not used because it accepts
    /// shorthand: it reads "192.168.1" as 192.168.0.1 and a bare number as an address. Both are
    /// plausible things to find in a half-filled spreadsheet cell, and both would import silently as
    /// an address nobody typed. A commissioning file has to mean exactly what it says.</para>
    /// </summary>
    public static bool TryParseIPv4(string text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;

        Span<Range> parts = stackalloc Range[5];
        ReadOnlySpan<char> span = text;
        if (span.Split(parts, '.') != 4)
        {
            return false;
        }

        Span<byte> octets = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<char> part = span[parts[i]];
            if (part.Length is 0 or > 3)
            {
                return false;
            }

            int value = 0;
            foreach (char c in part)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }

                value = (value * 10) + (c - '0');
            }

            if (value > 255)
            {
                return false;
            }

            octets[i] = (byte)value;
        }

        address = new IPAddress(octets);
        return true;
    }
}
