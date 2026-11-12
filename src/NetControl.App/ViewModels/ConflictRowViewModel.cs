using System.Globalization;
using NetControl.Core.Analysis;

namespace NetControl.App.ViewModels;

/// <summary>One contested address, as the Network health tab lists it.</summary>
public sealed record ConflictRowViewModel(AddressConflict Conflict)
{
    private readonly NetworkFinding _finding = Conflict.ToFinding();

    public string AddressText => Conflict.Address.ToString();

    public string Macs => Conflict.MacsText;

    public string Title => _finding.Title;

    public string Detail => _finding.Detail + (_finding.NextStep is { } next ? Environment.NewLine + next : string.Empty);

    public bool IsError => _finding.Severity == FindingSeverity.Error;

    public string LastSeen => Conflict.LastSeenUtc == DateTimeOffset.MinValue
        ? string.Empty
        : Conflict.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string Heard => string.Join(", ", Conflict.Claimants.Select(c => c.LastSource).Distinct()
        .Select(s => s switch
        {
            AddressObservationSource.ArpTable => "ARP table",
            AddressObservationSource.Gratuitous => "announcement",
            AddressObservationSource.Probe => "probe",
            _ => "ARP on the wire",
        }));
}
