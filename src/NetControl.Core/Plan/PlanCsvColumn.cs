namespace NetControl.Core.Plan;

/// <summary>
/// A column of a plan CSV, after the header has been matched.
///
/// <para><see cref="Ignored"/> is a real member rather than an absence, so that a header name the
/// tool does not know keeps its position in the row. Dropping it would shift every column after it
/// by one, which is the failure mode where an import succeeds and puts the mask in the gateway.</para>
/// </summary>
internal enum PlanCsvColumn
{
    Ignored = 0,
    Mac,
    Ip,
    Mask,
    Gateway,
    HostName,
    PanelRef,
    Role,
    Notes,
}
