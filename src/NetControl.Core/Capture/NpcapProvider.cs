using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Capture;

/// <summary>
/// Frame channels through Npcap, when it is installed. Nothing here is touched until somebody opens
/// a tab that needs it, and every failure comes back as a sentence rather than a crash.
/// </summary>
public sealed class NpcapProvider : ICaptureProvider
{
    public CaptureAvailability Check()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CaptureAvailability.NotWindows;
        }

        return CheckOnWindows();
    }

    public IFrameChannel Open(NicInfo nic, string filter, bool promiscuous)
    {
        ArgumentNullException.ThrowIfNull(nic);
        ArgumentNullException.ThrowIfNull(filter);

        if (!OperatingSystem.IsWindows())
        {
            throw new CaptureException(CaptureAvailability.NotWindows.Headline);
        }

        return OpenOnWindows(nic, filter, promiscuous);
    }

    [SupportedOSPlatform("windows")]
    private static CaptureAvailability CheckOnWindows()
    {
        (NpcapNative? native, string? problem) = NpcapNative.Instance;

        if (native is null)
        {
            return problem is null
                ? CaptureAvailability.NotInstalled
                : new CaptureAvailability(false, problem, "Reinstall Npcap from npcap.com and restart NetControl.");
        }

        string version = NpcapNative.Text(native.LibVersion());

        return AdminOnly()
            ? new CaptureAvailability(
                true,
                $"{version} - installed for administrators only.",
                "Capture works only when NetControl is run as administrator. Reinstall Npcap without the administrators-only option to avoid that.",
                version)
            : new CaptureAvailability(true, version, Version: version);
    }

    [SupportedOSPlatform("windows")]
    private static IFrameChannel OpenOnWindows(NicInfo nic, string filter, bool promiscuous)
    {
        (NpcapNative? native, string? problem) = NpcapNative.Instance;

        if (native is null)
        {
            throw new CaptureException(problem ?? CaptureAvailability.NotInstalled.Headline)
            {
                Remediation = CaptureAvailability.NotInstalled.Remediation,
            };
        }

        string device = DeviceNameFor(nic)
            ?? throw new CaptureException($"Windows no longer lists [{nic.Index}] {nic.Name}, so there is nothing to open.")
            {
                Remediation = "Pick the adapter again at the top of the window.",
            };

        return NpcapChannel.Open(native, device, nic.Mac, $"[{nic.Index}] {nic.Name}", filter, promiscuous);
    }

    /// <summary>
    /// Npcap names an adapter <c>\Device\NPF_{GUID}</c>, where the GUID is the one Windows gives the
    /// interface. Matched on the IPv4 index, which is how <see cref="NicInfo"/> identifies an adapter
    /// everywhere else in the tool.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? DeviceNameFor(NicInfo nic)
    {
        foreach (NetworkInterface candidate in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPv4InterfaceProperties? ipv4;

            try
            {
                ipv4 = candidate.GetIPProperties().GetIPv4Properties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            if (ipv4 is not null && ipv4.Index == nic.Index)
            {
                return $@"\Device\NPF_{candidate.Id}";
            }
        }

        return null;
    }

    /// <summary>Npcap's own record of the "administrators only" install option.</summary>
    [SupportedOSPlatform("windows")]
    private static bool AdminOnly()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap\Parameters");
            return key?.GetValue("AdminOnly") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
