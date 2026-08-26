using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core;
using NetControl.Core.Discovery;

namespace NetControl.App.ViewModels;

/// <summary>
/// One device a scan found, as a row.
///
/// <para>Every cell is text and every judgement is already made in Core - the row states what
/// <see cref="DiscoveredDevice"/> found and adds nothing of its own. In particular the reason a
/// device cannot be planned is <see cref="DiscoveredDevice.PlanningObstacle"/> verbatim, so the
/// scan list, the plan grid and any later report cannot explain the same refusal differently.</para>
/// </summary>
public sealed partial class ScanResultViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isAlreadyPlanned;

    /// <summary>
    /// What the plan has to say about this device, from <see cref="PlanConformance"/>. Null until a
    /// scan has been compared against a plan, and null for a device the plan says nothing about.
    /// The wording is Core's, verbatim - the row states findings, it does not make them.
    /// </summary>
    [ObservableProperty]
    private string? _planNote;

    /// <summary>
    /// True when the plan and this device disagree about who owns an address. Kept apart from
    /// <see cref="PlanNote"/> because a confirmation is also a plan note, and colouring a row for
    /// good news is how a colour stops meaning anything.
    /// </summary>
    [ObservableProperty]
    private bool _planConflict;

    public ScanResultViewModel(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        Device = device;
    }

    public DiscoveredDevice Device { get; }

    public MacAddress Mac => Device.Mac;

    /// <summary>What the device says its address is. This is what goes into the plan row.</summary>
    public string AddressText => Device.Address.ToString();

    public string ProductText => Device.ProductName.Length == 0 ? "(unnamed device)" : Device.ProductName;

    public string RevisionText => Device.Identity.Revision;

    public string SerialText => Device.Identity.SerialText;

    public string MacText => Device.Mac.IsEmpty ? string.Empty : Device.Mac.ToString();

    public string VendorText => Device.Vendor ?? string.Empty;

    public bool CanBePlanned => Device.CanBePlanned;

    /// <summary>
    /// The one thing worth saying about this row beyond its identity: it shares an address with
    /// something else, its MAC could not be resolved, or it is already in the plan.
    ///
    /// <para>A contested address comes first. It is the only finding here that means something on
    /// the segment is wrong right now, rather than something the tool cannot do.</para>
    /// </summary>
    public string Note
    {
        get
        {
            if (Device.AddressIsContested)
            {
                return $"Another device also answered on {Device.Address}. One of them is misconfigured.";
            }

            // Above the claim/sender mismatch and the planning obstacle: those are things the tool
            // cannot do, and this is something on the segment that is already wrong.
            if (PlanNote is { } plan)
            {
                return plan;
            }

            if (!Device.Identity.AddressMatchesSender)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Answered from {Device.SenderAddress} but reports {Device.Address}.");
            }

            if (Device.PlanningObstacle is { } obstacle)
            {
                return obstacle;
            }

            return IsAlreadyPlanned ? "Already in the plan." : string.Empty;
        }
    }

    /// <summary>Whether this row is worth colouring: something about it needs a person to look.</summary>
    public bool NeedsAttention => Device.AddressIsContested || !Device.CanBePlanned || PlanConflict;

    public string Tooltip =>
        Note.Length > 0
            ? Note
            : $"{ProductText} at {AddressText}, revision {RevisionText}, serial {SerialText}.";

    /// <summary>Re-reads <see cref="Note"/> after the plan changed underneath the list.</summary>
    partial void OnIsAlreadyPlannedChanged(bool value) => OnPropertyChanged(nameof(Note));

    partial void OnPlanNoteChanged(string? value)
    {
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnPlanConflictChanged(bool value) => OnPropertyChanged(nameof(NeedsAttention));
}
