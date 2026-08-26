using System.Net;
using NetControl.Core.Enip;

namespace NetControl.Tests;

/// <summary>
/// ListIdentity replies a test can state in one line, so a test about what the tool concludes from
/// a reply is not half full of protocol fields it does not care about.
/// </summary>
internal static class Identities
{
    public static ListIdentityReply Reply(
        string address = "192.168.1.51",
        uint serial = 0x00C0FFEE,
        string product = "1734-AENT/B",
        string? sender = null) =>
        new()
        {
            // Same address unless the test says otherwise: a device on a flat segment answers from
            // the address it claims, and a difference between the two is a finding worth arranging
            // deliberately rather than getting by accident.
            Sender = new IPEndPoint(IPAddress.Parse(sender ?? address), 44818),
            Address = IPAddress.Parse(address),
            Port = 44818,
            VendorId = 1,
            DeviceType = 12,
            ProductCode = 167,
            MajorRevision = 11,
            MinorRevision = 3,
            DeviceStatus = 0x0030,
            SerialNumber = serial,
            ProductName = product,
            State = 3,
        };
}
