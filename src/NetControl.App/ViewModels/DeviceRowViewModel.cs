using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;

namespace NetControl.App.ViewModels;

/// <summary>
/// One editable row of the plan.
///
/// <para><b>Every cell is text.</b> A grid cell holds whatever the user has typed so far, and
/// half of "192.168.1." is not an <see cref="IPAddress"/>. Binding a typed property would make WPF
/// swallow the value and paint a red box with no sentence in it; keeping the text and doing the
/// parsing here means the row can say "'192.168.1.999' is not an IPv4 address" and still remember
/// what was typed while it is being fixed.</para>
///
/// <para><b>Two kinds of wrong, kept apart.</b> <see cref="Problem"/> means the row cannot be
/// stored at all, so nothing is written. <see cref="ServeNote"/> means it stores fine but will
/// never be answered - a MAC written down at 7am with the address still to be decided is a normal
/// row, not an error. Colouring those the same is how a grid full of amber gets ignored.</para>
///
/// <para>No WPF types, and no <see cref="ProjectStore"/> either: the row validates, the grid
/// writes. That keeps every rule in here testable without a window or a file.</para>
/// </summary>
public sealed partial class DeviceRowViewModel : ObservableObject
{
    /// <summary>
    /// The properties a user can type into. A change to one of these revalidates and asks the grid
    /// to commit; a change to <see cref="Problem"/> or <see cref="Vendor"/> must not, or the two
    /// would call each other forever.
    /// </summary>
    private static readonly HashSet<string> EditableFields =
    [
        nameof(MacText),
        nameof(IpText),
        nameof(MaskText),
        nameof(GatewayText),
        nameof(HostName),
        nameof(PanelRef),
        nameof(Role),
        nameof(Notes),
    ];

    private DeviceRecord? _built;

    /// <summary>True while <see cref="Load"/> is filling the row in. See the comment there.</summary>
    private bool _loading;

    [ObservableProperty]
    private string _macText = string.Empty;

    [ObservableProperty]
    private string _ipText = string.Empty;

    [ObservableProperty]
    private string _maskText = string.Empty;

    [ObservableProperty]
    private string _gatewayText = string.Empty;

    [ObservableProperty]
    private string _hostName = string.Empty;

    [ObservableProperty]
    private string _panelRef = string.Empty;

    [ObservableProperty]
    private string _role = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    /// <summary>
    /// Resolved from the OUI table by the grid when the MAC changes, and stored with the row.
    /// Read-only in the grid: it is derived, and a hand-typed vendor that disagrees with the MAC
    /// would be worse than none.
    /// </summary>
    [ObservableProperty]
    private string? _vendor;

    /// <summary>
    /// How far this device has got. Set by the grid, which is the only thing that can see the
    /// <c>Assignment</c> table and the live log at once.
    /// </summary>
    [ObservableProperty]
    private DeviceState _state = DeviceState.Planned;

    /// <summary>
    /// The last ping of the planned address, this session. Not stored and not a state: an echo reply
    /// proves something answers at that address, not that it is the device the plan meant - which is
    /// what the scan and the CIP read are for. Null until somebody pings.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReachText))]
    [NotifyPropertyChangedFor(nameof(ReachTooltip))]
    [NotifyPropertyChangedFor(nameof(ReachIsSilent))]
    private PingOutcome? _reach;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyPropertyChangedFor(nameof(IsServable))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServable))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private string? _serveNote;

    private DeviceRowViewModel()
    {
    }

    /// <summary>Raised when a cell the user can type into has changed. The grid commits on this.</summary>
    public event EventHandler? Edited;

    /// <summary>Row id in the project file. 0 until the row has been written for the first time.</summary>
    public long Id { get; internal set; }

    /// <summary>
    /// Flags carried through an edit untouched. Nothing sets these yet - they arrive with a device
    /// that misbehaved on a bench - but an edit that silently dropped them would be a quiet way to
    /// lose the workaround that made a panel come up.
    /// </summary>
    public DeviceQuirks Quirks { get; internal set; }

    /// <summary>The MAC as parsed, or <see cref="MacAddress.Empty"/> while the cell does not parse.</summary>
    public MacAddress Mac { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>Stores cleanly and will be answered in Serve mode. Anything else is amber or red.</summary>
    public bool IsServable => Problem is null && ServeNote is null;

    /// <summary>
    /// True while the row is an empty placeholder - added with the button and not typed into yet.
    /// Not an error, and not written to the file either.
    /// </summary>
    public bool IsBlank =>
        IsEmpty(MacText) && IsEmpty(IpText) && IsEmpty(MaskText) && IsEmpty(GatewayText)
        && IsEmpty(HostName) && IsEmpty(PanelRef) && IsEmpty(Role) && IsEmpty(Notes);

    public string StatusTooltip => Problem ?? ServeNote ?? "Planned, valid, and will be served.";

    /// <summary>"3 ms", "no reply", or blank before anybody has pinged.</summary>
    public string ReachText => Reach?.Text ?? string.Empty;

    public string? ReachTooltip => Reach?.Sentence;

    /// <summary>Pinged and nothing answered - amber in the grid, never red: plenty of devices ignore ping.</summary>
    public bool ReachIsSilent => Reach is { Replied: false };

    public static DeviceRowViewModel FromRecord(DeviceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var row = new DeviceRowViewModel
        {
            Id = record.Id,
            Quirks = record.Quirks,
        };

        row.Load(record);
        return row;
    }

    /// <summary>An empty row, waiting to be typed into.</summary>
    public static DeviceRowViewModel Blank() => new();

    /// <summary>
    /// The row as something that can be stored, or nothing when <see cref="Problem"/> says why not.
    /// A blank row yields nothing and is not a problem.
    /// </summary>
    public bool TryBuild(out DeviceRecord? record)
    {
        record = _built;
        return record is not null;
    }

    /// <summary>
    /// Fills the row in from a stored record without that counting as an edit.
    ///
    /// <para>The flag is the whole point. Assigning through the properties is the only way in -
    /// the generator's own analyser forbids touching the backing fields, and rightly, since a
    /// field written behind the property's back is a field the UI never hears about. But each
    /// assignment raises <see cref="Edited"/>, and a row that announces nine edits while being
    /// loaded is a row the grid writes straight back to the file it just came from.</para>
    /// </summary>
    private void Load(DeviceRecord record)
    {
        _loading = true;

        try
        {
            MacText = record.Mac.IsEmpty ? string.Empty : record.Mac.ToString();
            IpText = record.PlannedIp?.ToString() ?? string.Empty;
            MaskText = record.PlannedMask?.ToString() ?? string.Empty;
            GatewayText = record.PlannedGateway?.ToString() ?? string.Empty;
            HostName = record.HostName ?? string.Empty;
            PanelRef = record.PanelRef ?? string.Empty;
            Role = record.Role ?? string.Empty;
            Notes = record.Notes ?? string.Empty;
            Vendor = record.Vendor;
        }
        finally
        {
            _loading = false;
        }

        // Once, at the end, rather than nine times on the way through.
        Revalidate();
    }

    /// <summary>
    /// Re-reads every cell. Called on each edit, and by the grid after a write so the row reflects
    /// what the file now holds.
    /// </summary>
    internal void Revalidate()
    {
        _built = null;
        Mac = MacAddress.Empty;

        if (IsBlank)
        {
            // Nothing typed yet. Not an error, and nothing to store.
            Problem = null;
            ServeNote = "Empty row - type a MAC to add it to the plan.";
            return;
        }

        if (IsEmpty(MacText))
        {
            Problem = "This row has no MAC address, and the plan is keyed on the MAC.";
            ServeNote = null;
            return;
        }

        if (!MacAddress.TryParse(MacText, out MacAddress mac))
        {
            Problem = $"'{MacText.Trim()}' is not a MAC address. Expected six hex bytes, e.g. 00:1D:9C:C7:B0:70.";
            ServeNote = null;
            return;
        }

        if (mac.IsEmpty || mac.IsBroadcast || mac.IsMulticast)
        {
            Problem = $"'{mac}' cannot identify a single device, so it must never be served.";
            ServeNote = null;
            return;
        }

        if (!TryAddress(IpText, "IP address", out IPAddress? ip, out string? failure)
            || !TryAddress(MaskText, "subnet mask", out IPAddress? mask, out failure)
            || !TryAddress(GatewayText, "gateway", out IPAddress? gateway, out failure))
        {
            Problem = failure;
            ServeNote = null;
            return;
        }

        Mac = mac;
        Problem = null;

        var built = new DeviceRecord
        {
            Id = Id,
            Mac = mac,
            PlannedIp = ip,
            PlannedMask = mask,
            PlannedGateway = gateway,
            HostName = Cell(HostName),
            PanelRef = Cell(PanelRef),
            Role = Cell(Role),
            Vendor = Vendor,
            Quirks = Quirks,
            Notes = Cell(Notes),
        };

        _built = built;

        // The same check the planner runs, so a row that reads as fine here can never turn out to
        // be one of the "3 of 14 planned devices will not be served" surprises at serve time.
        ServeNote = built.TryToAssignment(out _, out string? problem) ? null : problem;
    }

    /// <summary>
    /// An edit to a cell revalidates and asks the grid to write. Overriding here rather than
    /// implementing eight <c>OnXChanged</c> partials keeps the rule in one place, and the set of
    /// editable names is what stops the properties this sets from re-entering.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_loading || e.PropertyName is null || !EditableFields.Contains(e.PropertyName))
        {
            return;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsBlank)));
        Revalidate();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsEmpty(string? value) => string.IsNullOrWhiteSpace(value);

    private static string? Cell(string? value) => IsEmpty(value) ? null : value!.Trim();

    /// <summary>
    /// A blank address cell is a plan in progress, not a mistake. Anything else has to be IPv4:
    /// this tool hands out BOOTP replies, and BOOTP has nowhere to put a v6 address.
    /// </summary>
    private static bool TryAddress(string text, string what, out IPAddress? address, out string? failure)
    {
        address = null;
        failure = null;

        if (IsEmpty(text))
        {
            return true;
        }

        string trimmed = text.Trim();

        if (!IPAddress.TryParse(trimmed, out IPAddress? parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            failure = $"'{trimmed}' is not an IPv4 {what}. Expected four numbers, e.g. 192.168.1.51.";
            return false;
        }

        address = parsed;
        return true;
    }
}
