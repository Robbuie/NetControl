using System.Net;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;

namespace NetControl.Core.Plan;

/// <summary>
/// What an import needs to know beyond the file itself: who made each device, and what segment the
/// plan is supposed to be on.
///
/// <para>Both parts are optional, and the reason is worth stating. Without an
/// <see cref="Oui"/> table the rows import with no vendor, which is a cosmetic loss. Without an
/// adapter, the three checks that compare the plan against <em>this machine</em> - own address,
/// same subnet, and therefore reachability - simply do not run, and that is the honest way to
/// import a plan for a segment the laptop is not on yet. Passing the adapter is the stricter and
/// more useful choice whenever the laptop is already on the target subnet, which during
/// commissioning it should be.</para>
/// </summary>
public sealed record PlanImportContext
{
    /// <summary>No vendor resolution and no adapter checks. Used by tests and by a plain file read.</summary>
    public static PlanImportContext None { get; } = new();

    /// <summary>Resolves the vendor for each MAC, once, at import. Null leaves every vendor unset.</summary>
    public IOuiLookup? Oui { get; init; }

    /// <summary>
    /// The address of the adapter this plan will be served from. An imported row holding it would
    /// hand the laptop's own address to a device.
    /// </summary>
    public IPAddress? AdapterAddress { get; init; }

    /// <summary>
    /// The adapter's segment. A planned address outside it is a device the readback after Set
    /// static cannot reach, which reports Unverified for a write that worked.
    /// </summary>
    public Ipv4Subnet? AdapterSubnet { get; init; }

    /// <summary>How the adapter is named in a problem message. Null when there is no adapter.</summary>
    public string? AdapterName { get; init; }

    /// <summary>
    /// The context for a selected adapter. Falls back to no subnet checks when the adapter has no
    /// IPv4 address or a mask that is not contiguous - an adapter in that state cannot say what is
    /// on its segment, and guessing would refuse valid rows.
    /// </summary>
    public static PlanImportContext ForAdapter(NicInfo nic, IOuiLookup? oui = null)
    {
        ArgumentNullException.ThrowIfNull(nic);

        return new PlanImportContext
        {
            Oui = oui,
            AdapterAddress = nic.IPv4,
            AdapterSubnet = Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out Ipv4Subnet subnet) ? subnet : null,
            AdapterName = nic.Name,
        };
    }
}
