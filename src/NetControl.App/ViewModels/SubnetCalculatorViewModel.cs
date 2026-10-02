using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core;
using NetControl.Core.Plan;

namespace NetControl.App.ViewModels;

/// <summary>
/// The subnet calculator (PLAN-TOOLKIT.md F6). Recalculates as it is typed into; sends nothing and
/// stores nothing. The second box answers the question this is usually opened for: is that device on
/// the same subnet as this one?
/// </summary>
public sealed partial class SubnetCalculatorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _other = string.Empty;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string? _network;

    [ObservableProperty]
    private string? _broadcast;

    [ObservableProperty]
    private string? _hosts;

    [ObservableProperty]
    private string? _hostCount;

    [ObservableProperty]
    private string? _mask;

    [ObservableProperty]
    private string? _wildcard;

    /// <summary>"192.168.1.60 is on this subnet" - or why not, or nothing while the box is empty.</summary>
    [ObservableProperty]
    private string? _otherVerdict;

    /// <param name="initial">What to start with - usually the selected adapter's own address and prefix.</param>
    public SubnetCalculatorViewModel(string? initial = null)
    {
        Input = initial ?? string.Empty;
        Recalculate();
    }

    partial void OnInputChanged(string value) => Recalculate();

    partial void OnOtherChanged(string value) => Recalculate();

    private void Recalculate()
    {
        if (!SubnetCalculation.TryParse(Input, out Ipv4Subnet subnet, out string? problem))
        {
            Problem = string.IsNullOrWhiteSpace(Input) ? null : problem;
            Network = Broadcast = Hosts = HostCount = Mask = Wildcard = OtherVerdict = null;
            return;
        }

        Problem = null;
        Network = $"{subnet.Network}/{subnet.PrefixLength.ToString(CultureInfo.InvariantCulture)}";
        Broadcast = subnet.PrefixLength >= 31 ? "none (point-to-point or single host)" : subnet.Broadcast.ToString();
        Hosts = $"{subnet.FirstHost} - {subnet.LastHost}";
        HostCount = subnet.HostCount.ToString("N0", CultureInfo.InvariantCulture);
        Mask = subnet.Mask.ToString();
        Wildcard = subnet.Wildcard.ToString();
        OtherVerdict = Verdict(subnet);
    }

    private string? Verdict(Ipv4Subnet subnet)
    {
        if (string.IsNullOrWhiteSpace(Other))
        {
            return null;
        }

        if (!PlanValidation.TryParseIPv4(Other.Trim(), out IPAddress? other))
        {
            return $"'{Other.Trim()}' is not an IPv4 address.";
        }

        if (!subnet.Contains(other))
        {
            return $"{other} is NOT on {subnet.Network}/{subnet.PrefixLength.ToString(CultureInfo.InvariantCulture)} "
                + "- a device there needs a router, or a different address on this side.";
        }

        if (subnet.PrefixLength < 31 && other.Equals(subnet.Network))
        {
            return $"{other} is this subnet's network address. No device may hold it.";
        }

        if (subnet.PrefixLength < 31 && other.Equals(subnet.Broadcast))
        {
            return $"{other} is this subnet's broadcast address. No device may hold it.";
        }

        return $"{other} is on this subnet.";
    }
}
