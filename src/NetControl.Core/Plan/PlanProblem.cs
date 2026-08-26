using System.Globalization;

namespace NetControl.Core.Plan;

/// <summary>
/// One reason a plan file was not imported, and where to find it.
///
/// <para>It reports a <em>line</em> rather than a spreadsheet row because a line is what a text
/// editor can put a cursor on, and the two differ as soon as a quoted <c>Notes</c> field contains a
/// newline. Where they agree - which is most files - the number is the same either way.</para>
/// </summary>
/// <param name="LineNumber">The line in the file, 1-based, counting the header.</param>
/// <param name="Message">
/// What is wrong, naming the likely cause. The remediation belongs in the same sentence when there
/// is one: "line 7 is wrong" is useless, "line 7: 192.168.1.0 is the network address of
/// 192.168.1.0/24, which no device may hold" is the product.
/// </param>
public sealed record PlanProblem(int LineNumber, string Message)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"line {LineNumber}: {Message}");
}
