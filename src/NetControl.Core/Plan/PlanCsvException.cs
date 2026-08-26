namespace NetControl.Core.Plan;

/// <summary>
/// The file is not a plan: no header, a column nothing recognises, a duplicated column, or a
/// quoted field that never closes.
///
/// <para>These are thrown rather than returned as row problems because they are not problems
/// <em>with</em> rows - there are no rows yet. A row problem says "line 14 is wrong and here is
/// why"; this says "this file cannot be read as a plan at all", and there is nothing useful to
/// list underneath it.</para>
/// </summary>
public sealed class PlanCsvException : NetControlException
{
    public PlanCsvException(string message) : base(message) { }

    public PlanCsvException(string message, Exception? inner) : base(message, inner) { }
}
