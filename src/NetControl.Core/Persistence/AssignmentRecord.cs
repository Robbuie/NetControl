using System.Net;

namespace NetControl.Core.Persistence;

/// <summary>
/// An address that was actually handed to a device, and over which adapter.
///
/// Distinct from <see cref="Dhcp.DeviceAssignment"/> on purpose: that is the <em>plan</em>, this is
/// the <em>history</em>. They usually agree, and the interesting cases are the ones where they do
/// not - a device served twice, or served on an adapter nobody expected. Keeping history separate
/// from intent is what lets the commissioning report say what happened rather than what was meant
/// to happen.
/// </summary>
/// <param name="Id">Row id, 0 for a record not yet written.</param>
/// <param name="DeviceId">The <c>Device</c> row this was served to. Only planned devices are ever served.</param>
/// <param name="ServedIp">The address in the reply's yiaddr field.</param>
/// <param name="NicIndex">Interface index the reply went out of, from IP_PKTINFO on the request.</param>
/// <param name="NicName">Adapter name at the time. Stored as text because adapter indexes get reused.</param>
/// <param name="ServedUtc">When the reply hit the wire.</param>
public sealed record AssignmentRecord(
    long Id,
    long DeviceId,
    IPAddress ServedIp,
    int NicIndex,
    string NicName,
    DateTimeOffset ServedUtc);
