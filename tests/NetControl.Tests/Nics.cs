using System.Net;
using System.Net.NetworkInformation;
using NetControl.Core;
using NetControl.Core.Interfaces;

namespace NetControl.Tests;

/// <summary>Adapter snapshots for tests, so each test only states the part it cares about.</summary>
internal static class Nics
{
    public static NicInfo Healthy(
        int index = 12,
        string address = "192.168.1.10",
        string mask = "255.255.255.0") =>
        new(
            index,
            "Ethernet",
            "Intel(R) Ethernet Connection I219-V",
            MacAddress.Parse("00:1D:9C:C7:B0:70"),
            IPAddress.Parse(address),
            IPAddress.Parse(mask),
            OperationalStatus.Up,
            NetworkInterfaceType.Ethernet,
            1_000_000_000);

    public static NicInfo Down() => Healthy() with { Status = OperationalStatus.Down };

    public static NicInfo NoAddress() => Healthy() with { IPv4 = null, Mask = null };

    public static NicInfo Apipa() => Healthy(address: "169.254.13.44", mask: "255.255.0.0");
}
