using System.Globalization;
using NetControl.Core.Persistence;

namespace NetControl.Core.DeviceHealth;

/// <summary>A diagnostics read and what it means, together. The event row and the screen are both built from this.</summary>
public sealed record DeviceHealthResult(DeviceHealthReport Report, IReadOnlyList<HealthFinding> Findings)
{
    /// <summary>The worst finding, or Info when there are none.</summary>
    public EventSeverity Severity =>
        Findings.Count == 0 ? EventSeverity.Info : Findings.Max(finding => finding.Severity);

    public int Warnings => Findings.Count(finding => finding.Severity == EventSeverity.Warn);

    public int Errors => Findings.Count(finding => finding.Severity == EventSeverity.Error);

    /// <summary>One line for the record and the status line: what it is, and the worst thing found.</summary>
    public string Summary
    {
        get
        {
            string ports = Report.Ports.Count switch
            {
                0 => "no link information",
                1 => Report.Ports[0].Summary,
                _ => string.Join("; ", Report.Ports.Select(p =>
                    string.Create(CultureInfo.InvariantCulture, $"port {p.Instance} {p.Summary}"))),
            };

            HealthFinding? worst = Findings.Count == 0 ? null : Findings[0];

            string verdict = (Errors, Warnings) switch
            {
                (0, 0) => "nothing wrong found",
                (0, _) => string.Create(CultureInfo.InvariantCulture, $"{Warnings} warning(s)"),
                _ => string.Create(CultureInfo.InvariantCulture, $"{Errors} fault(s), {Warnings} warning(s)"),
            };

            return $"{Report.Title}: {ports}. {verdict}"
                + (worst is { Severity: not EventSeverity.Info } ? $" - {worst.Message}" : ".");
        }
    }
}
