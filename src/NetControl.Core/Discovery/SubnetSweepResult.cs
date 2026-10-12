using System.Globalization;
using System.Text;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>
/// What a subnet sweep found: every address something answered at, in address order.
/// </summary>
public sealed record SubnetSweepResult(
    NicInfo Nic,
    string Scope,
    IReadOnlyList<SweptHost> Hosts,
    int AddressesPinged,
    bool ArpChecked,
    TimeSpan Duration)
{
    /// <summary>
    /// True when the range is reached through a router: no MAC addresses, and devices that ignore
    /// ping cannot be seen. Said in the summary, because a short list from a routed sweep means
    /// something different from a short list on the laptop's own cable.
    /// </summary>
    public bool IsRouted { get; init; }

    public int AnsweredPing => Hosts.Count(h => h.AnsweredPing);

    /// <summary>In the ARP cache and silent to ping: present, and ignoring ICMP.</summary>
    public int ArpOnly => Hosts.Count(h => !h.AnsweredPing && h.InArp);

    /// <summary>
    /// Warn when nothing was found at all. That says more about the laptop - wrong adapter, wrong
    /// subnet, cable in the wrong switch - than about every device on the segment at once.
    /// </summary>
    public EventSeverity Severity => Hosts.Count == 0 ? EventSeverity.Warn : EventSeverity.Info;

    /// <summary>The event row's message and the status line above the list. One or two sentences.</summary>
    public string Summary
    {
        get
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture,
                $"Swept {AddressesPinged} address(es) on {Scope}{(IsRouted ? " (routed)" : string.Empty)} from [{Nic.Index}] {Nic.Name} in "
                + $"{Duration.TotalSeconds:0.#} s: {Hosts.Count} found");

            if (Hosts.Count > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" ({AnsweredPing} answered ping");

                if (ArpOnly > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $", {ArpOnly} answered ARP only");
                }

                text.Append(')');
            }

            text.Append('.');

            if (IsRouted)
            {
                text.Append(Hosts.Count == 0
                    ? " Nothing answered. Check this PC has a route to that range (route print), and that a firewall "
                        + "between here and there lets ping through."
                    : " Behind a router, so no MAC addresses, and devices that ignore ping are not seen.");
            }
            else if (Hosts.Count == 0)
            {
                text.Append(" Nothing answered at all - check this adapter is plugged into the segment you meant "
                    + "and has an address on it.");
            }
            else if (!ArpChecked)
            {
                text.Append(" The ARP table could not be read, so devices that ignore ping are missing and no MAC "
                    + "addresses are shown.");
            }

            return text.ToString();
        }
    }
}
