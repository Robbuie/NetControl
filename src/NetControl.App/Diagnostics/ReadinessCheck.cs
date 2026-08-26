namespace NetControl.App.Diagnostics;

/// <summary>
/// One line of the interface bar: what was checked, how it stands, and what to do about it.
///
/// "Nothing is arriving" has four common causes - the wrong adapter, a port conflict, the
/// firewall, and a device that is not asking. Three of those are measurable before a single
/// packet lands, and showing each as its own graded line is what turns the bar from decoration
/// into the answer.
/// </summary>
/// <param name="Name">Short label: "Adapter", "UDP/67", "Firewall", "Listener".</param>
/// <param name="State">How this one stands.</param>
/// <param name="Summary">One sentence, already user-facing. Names the cause, not the symptom.</param>
/// <param name="Remediation">The next action, or null when there is nothing to do.</param>
public sealed record ReadinessCheck(
    string Name,
    ReadinessState State,
    string Summary,
    string? Remediation = null)
{
    /// <summary>A check that has not run yet. Grey, and explicitly not good news.</summary>
    public static ReadinessCheck NotChecked(string name, string summary, string? remediation = null) =>
        new(name, ReadinessState.Unknown, summary, remediation);

    /// <summary>Everything the state says, on one line, for a tooltip.</summary>
    public string Detail => Remediation is null
        ? Summary
        : $"{Summary}{Environment.NewLine}{Environment.NewLine}{Remediation}";

    /// <summary>
    /// The worst of a set. Empty means <see cref="ReadinessState.Unknown"/>: no checks is not
    /// the same as all checks passing.
    /// </summary>
    public static ReadinessState Worst(IEnumerable<ReadinessCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        var worst = ReadinessState.Ready;
        bool any = false;

        foreach (ReadinessCheck check in checks)
        {
            any = true;
            if (check.State > worst)
            {
                worst = check.State;
            }
        }

        return any ? worst : ReadinessState.Unknown;
    }
}
