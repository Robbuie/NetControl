using System.Net;
using System.Text.Json;
using NetControl.Core.Persistence;

namespace NetControl.Core.AdapterSetup;

/// <summary>
/// A saved address for the laptop's adapter, by name - "SF101 cell", "Shop bench". Kept in the
/// project file, so the addresses a job needs travel with the job to whichever laptop opens it.
/// </summary>
/// <param name="Name">What it is called in the list.</param>
/// <param name="Ip">The laptop's address.</param>
/// <param name="Mask">Its mask.</param>
/// <param name="Gateway">Blank for none.</param>
/// <param name="Dns">Blank for none; several separated by commas.</param>
public sealed record AdapterProfile(string Name, string Ip, string Mask, string Gateway = "", string Dns = "")
{
    /// <summary>The project setting the list is stored under.</summary>
    public const string SettingKey = "adapter.profiles";

    /// <summary>The project setting for the laptop's host number on each plan subnet.</summary>
    public const string HostNumberKey = "adapter.hostNumber";

    /// <summary>What a laptop's host number is when nobody has said: high, away from where devices go.</summary>
    public const int DefaultHostNumber = 250;

    /// <summary>The saved profiles in the project, in the order they were saved.</summary>
    public static IReadOnlyList<AdapterProfile> Load(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? json = settings.Get(SettingKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AdapterProfile>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Replaces the saved list.</summary>
    public static void Save(ProjectSettings settings, IEnumerable<AdapterProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);

        List<AdapterProfile> list = [.. profiles];
        settings.Set(SettingKey, list.Count == 0 ? null : JsonSerializer.Serialize(list));
    }

    /// <summary>
    /// One profile per subnet the plan uses, with the laptop at <paramref name="hostNumber"/> on it -
    /// the address to put the laptop on to reach that part of the plan. A subnet whose host number
    /// address is planned for a device, or falls outside it, is offered at the next free number down.
    /// No gateway: the laptop is joining the devices, not routing through them.
    /// </summary>
    public static IReadOnlyList<AdapterProfile> FromPlan(IEnumerable<DeviceRecord> devices, int hostNumber = DefaultHostNumber)
    {
        ArgumentNullException.ThrowIfNull(devices);

        List<DeviceRecord> planned = [.. devices.Where(d => d.PlannedIp is not null)];
        HashSet<IPAddress> used = [.. planned.Select(d => d.PlannedIp!)];

        var subnets = new SortedDictionary<uint, (Ipv4Subnet Subnet, int Count)>();

        foreach (DeviceRecord device in planned)
        {
            IPAddress mask = device.PlannedMask ?? IPAddress.Parse("255.255.255.0");
            if (!Ipv4Subnet.TryCreate(device.PlannedIp, mask, out Ipv4Subnet subnet))
            {
                continue;
            }

            uint key = ToUInt(subnet.Network);
            subnets[key] = subnets.TryGetValue(key, out (Ipv4Subnet Subnet, int Count) seen)
                ? (seen.Subnet, seen.Count + 1)
                : (subnet, 1);
        }

        var profiles = new List<AdapterProfile>();

        foreach ((Ipv4Subnet subnet, int count) in subnets.Values)
        {
            if (HostAddress(subnet, hostNumber, used) is not { } laptop)
            {
                continue;
            }

            profiles.Add(new AdapterProfile(
                $"Plan: {subnet.Network}/{subnet.PrefixLength} ({count} device{(count == 1 ? string.Empty : "s")})",
                laptop.ToString(),
                subnet.Mask.ToString()));
        }

        return profiles;
    }

    private static IPAddress? HostAddress(Ipv4Subnet subnet, int hostNumber, HashSet<IPAddress> used)
    {
        uint network = ToUInt(subnet.Network);
        uint broadcast = ToUInt(subnet.Broadcast);
        uint candidate = network + (uint)Math.Max(1, hostNumber);

        if (candidate >= broadcast)
        {
            candidate = broadcast - 1;
        }

        for (; candidate > network; candidate--)
        {
            IPAddress address = FromUInt(candidate);
            if (!used.Contains(address))
            {
                return address;
            }
        }

        return null;
    }

    private static uint ToUInt(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
