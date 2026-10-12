using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core;
using NetControl.Core.Discovery;

namespace NetControl.App.ViewModels;

/// <summary>
/// One address a scan found something at, as a row.
///
/// <para>Two kinds of evidence can stand behind a row, and either or both may be present: an
/// EtherNet/IP identity (<see cref="Device"/>) - which says what the device is - and a subnet sweep's
/// ping-and-ARP result (<see cref="Host"/>) - which says something is there at all. They are joined
/// by address, so a Rockwell adapter that answered both is one row with a product name and a ping
/// time, and a switch that only answered ping is a row with no product name at all.</para>
///
/// <para>Every cell is text and every judgement is already made in Core - the row states what was
/// found and adds nothing of its own. In particular the reason a device cannot be planned is Core's
/// wording verbatim, so the scan list, the plan grid and any later report cannot explain the same
/// refusal differently.</para>
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

    /// <summary>
    /// What the subnet sweep found at this address. Settable because the sweep finishes after the
    /// identity scan, and joins onto rows that are already on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseText))]
    [NotifyPropertyChangedFor(nameof(MacText))]
    [NotifyPropertyChangedFor(nameof(VendorText))]
    [NotifyPropertyChangedFor(nameof(CanBePlanned))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(Note))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private SweptHost? _host;

    /// <summary>A device that answered the EtherNet/IP identity request.</summary>
    public ScanResultViewModel(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        Device = device;
    }

    /// <summary>Something the subnet sweep found that did not answer as EtherNet/IP.</summary>
    public ScanResultViewModel(SweptHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    /// <summary>The EtherNet/IP identity, or null when this address did not answer as EtherNet/IP.</summary>
    public DiscoveredDevice? Device { get; }

    /// <summary>True when the device identified itself over EtherNet/IP.</summary>
    public bool IsEtherNetIp => Device is not null;

    /// <summary>
    /// The address the row is about: what an EtherNet/IP device says it holds, otherwise the
    /// address that answered. This is what goes into a plan row.
    /// </summary>
    public IPAddress Address => Device?.Address ?? Host!.Address;

    /// <summary>
    /// The MAC to plan against. The identity scan's when it resolved one, otherwise the sweep's -
    /// they came out of the same ARP cache, so where both exist they agree.
    /// </summary>
    public MacAddress Mac =>
        Device is { CanBePlanned: true } ? Device.Mac
        : Host is { CanBePlanned: true } ? Host.Mac
        : Device?.Mac is { IsEmpty: false } mac ? mac
        : Host?.Mac ?? MacAddress.Empty;

    public string AddressText => Address.ToString();

    /// <summary>The product name an EtherNet/IP device gave. Blank for anything else - the tool does not guess.</summary>
    public string ProductText => Device is null
        ? string.Empty
        : Device.ProductName.Length == 0 ? "(unnamed device)" : Device.ProductName;

    public string RevisionText => Device?.Identity.Revision ?? string.Empty;

    public string SerialText => Device?.Identity.SerialText ?? string.Empty;

    public string MacText => Mac.IsEmpty ? string.Empty : Mac.ToString();

    public string VendorText => Device?.Vendor ?? Host?.Vendor ?? string.Empty;

    /// <summary>The ping time, "no reply", or "ARP only". Blank when no sweep ran.</summary>
    public string ResponseText => Host?.HowFound ?? string.Empty;

    public bool CanBePlanned => Device?.CanBePlanned == true || Host?.CanBePlanned == true;

    /// <summary>Why this row cannot become a plan row, in Core's words. Null when it can.</summary>
    public string? PlanningObstacle => CanBePlanned ? null : Device?.PlanningObstacle ?? Host?.PlanningObstacle;

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
            if (Device?.AddressIsContested == true)
            {
                return $"Another device also answered on {Device.Address}. One of them is misconfigured.";
            }

            // Above the claim/sender mismatch and the planning obstacle: those are things the tool
            // cannot do, and this is something on the segment that is already wrong.
            if (PlanNote is { } plan)
            {
                return plan;
            }

            if (Device is not null && !Device.Identity.AddressMatchesSender)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Answered from {Device.SenderAddress} but reports {Device.Address}.");
            }

            if (PlanningObstacle is { } obstacle)
            {
                return obstacle;
            }

            return IsAlreadyPlanned ? "Already in the plan." : string.Empty;
        }
    }

    /// <summary>Whether this row is worth colouring: something about it needs a person to look.</summary>
    public bool NeedsAttention => Device?.AddressIsContested == true || !CanBePlanned || PlanConflict;

    public string Tooltip
    {
        get
        {
            if (Note.Length > 0)
            {
                return Note;
            }

            if (Device is not null)
            {
                return $"{ProductText} at {AddressText}, revision {RevisionText}, serial {SerialText}.";
            }

            return Host!.Sentence + " It did not identify itself over EtherNet/IP - a PC, switch, camera, or a "
                + "device on another protocol.";
        }
    }

    /// <summary>Re-reads <see cref="Note"/> after the plan changed underneath the list.</summary>
    partial void OnIsAlreadyPlannedChanged(bool value)
    {
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnPlanNoteChanged(string? value)
    {
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnPlanConflictChanged(bool value) => OnPropertyChanged(nameof(NeedsAttention));
}
