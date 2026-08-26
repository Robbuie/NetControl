using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core;
using NetControl.Core.Discovery;

namespace NetControl.App.ViewModels;

/// <summary>
/// What the last scan found, as a list.
///
/// <para><b>It is deliberately not the request log.</b> "This device asked for an address" and
/// "this device answered when I asked who was there" are different facts about different devices -
/// a device that answers a scan already has an address and does not need BOOTP - and a list that
/// mixed them would be a list that misleads. They share one thing: the same "add to plan" action,
/// which is why both gestures end up in <see cref="MainViewModel"/>.</para>
///
/// <para>This replaces its contents on each scan rather than accumulating. A scan is a snapshot of
/// a segment at a moment; keeping the previous one alongside would mean a device that has since
/// been unplugged sitting in the list looking present. The event log is where the history goes.</para>
/// </summary>
public sealed partial class ScanResultsViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanResultViewModel? _selectedRow;

    /// <summary>True while a scan is in flight, so the button can say so and not be pressed twice.</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>
    /// One line about the last scan: what went out, what came back, and anything odd about it.
    /// Starts as an explanation of what the button does, because an empty list with no caption
    /// looks like a feature that failed rather than one nobody has used yet.
    /// </summary>
    [ObservableProperty]
    private string _status =
        "No scan yet. Scan asks every EtherNet/IP device on the selected adapter to identify itself.";

    public ObservableCollection<ScanResultViewModel> Rows { get; } = [];

    public bool HasResults => Rows.Count > 0;

    /// <summary>Replaces the list with what a scan found.</summary>
    public void Apply(DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Rows.Clear();

        foreach (DiscoveredDevice device in result.Devices)
        {
            Rows.Add(new ScanResultViewModel(device));
        }

        SelectedRow = null;
        Status = result.Summary;
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Marks the rows whose devices are in the plan. Called after a scan and after anything that
    /// changes the plan, so the list does not invite you to add the same device twice.
    /// </summary>
    public void ApplyPlan(Func<MacAddress, bool> isPlanned)
    {
        ArgumentNullException.ThrowIfNull(isPlanned);

        foreach (ScanResultViewModel row in Rows)
        {
            row.IsAlreadyPlanned = !row.Mac.IsEmpty && isPlanned(row.Mac);
        }
    }

    public void Clear()
    {
        Rows.Clear();
        SelectedRow = null;
        Status = "Cleared.";
        OnPropertyChanged(nameof(HasResults));
    }
}
