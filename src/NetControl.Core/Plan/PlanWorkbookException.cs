namespace NetControl.Core.Plan;

/// <summary>
/// The file cannot be read as a plan workbook at all: it is not an .xlsx, it is damaged, or no sheet
/// in it has a device column and an address column. Thrown rather than returned for the same reason
/// as <see cref="PlanCsvException"/> - there are no rows yet to report problems against.
/// </summary>
public sealed class PlanWorkbookException : NetControlException
{
    public PlanWorkbookException(string message) : base(message) { }

    public PlanWorkbookException(string message, Exception? inner) : base(message, inner) { }
}
