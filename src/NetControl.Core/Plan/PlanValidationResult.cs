using System.Globalization;
using NetControl.Core.Persistence;

namespace NetControl.Core.Plan;

/// <summary>
/// The outcome of validating a plan file: either the devices, or the reasons there are none.
///
/// <para><b>It cannot hold both.</b> The constructors are private and the two factories are
/// <see cref="Imported"/> and <see cref="Refused"/>, so "a file with one bad row imports nothing"
/// is a property of the type rather than a rule every caller has to remember. Half a panel
/// configured is worse than none: the rows that were fine went in, the row that was not did not,
/// and the person now has to work out which by hand on a live machine.</para>
/// </summary>
public sealed record PlanValidationResult
{
    private PlanValidationResult(IReadOnlyList<DeviceRecord> devices, IReadOnlyList<PlanProblem> problems)
    {
        Devices = devices;
        Problems = problems;
    }

    /// <summary>The rows to write. Empty whenever <see cref="Problems"/> is not.</summary>
    public IReadOnlyList<DeviceRecord> Devices { get; }

    /// <summary>Every problem in the file, in line order. Empty on success.</summary>
    public IReadOnlyList<PlanProblem> Problems { get; }

    public bool IsValid => Problems.Count == 0;

    /// <summary>
    /// One line for the status bar and for the event log, so the two cannot disagree about what
    /// happened.
    /// </summary>
    public string Summary =>
        IsValid
            ? Devices.Count == 1
                ? "1 device imported."
                : string.Create(CultureInfo.InvariantCulture, $"{Devices.Count} devices imported.")
            : Problems.Count == 1
                ? "Nothing imported: 1 problem in the file."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Nothing imported: {Problems.Count} problems in the file.");

    public static PlanValidationResult Imported(IReadOnlyList<DeviceRecord> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return new PlanValidationResult(devices, []);
    }

    public static PlanValidationResult Refused(IReadOnlyList<PlanProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        if (problems.Count == 0)
        {
            throw new ArgumentException("A refusal has to say why.", nameof(problems));
        }

        return new PlanValidationResult([], problems);
    }
}
