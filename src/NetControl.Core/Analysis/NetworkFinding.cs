namespace NetControl.Core.Analysis;

/// <summary>
/// One thing an analysis found, worded for the person standing at the panel: what, why it matters,
/// and what to do next.
/// </summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Area">What part of the path it is about - "This PC", "Cable", "Addresses", "Traffic".</param>
/// <param name="Title">One line.</param>
/// <param name="Detail">The evidence: numbers, addresses, MACs.</param>
/// <param name="NextStep">What to do about it. Null when there is nothing to do.</param>
public sealed record NetworkFinding(
    FindingSeverity Severity,
    string Area,
    string Title,
    string Detail,
    string? NextStep = null)
{
    public string SeverityText => Severity switch
    {
        FindingSeverity.Error => "Problem",
        FindingSeverity.Warn => "Check",
        FindingSeverity.Info => "Note",
        _ => "OK",
    };
}
