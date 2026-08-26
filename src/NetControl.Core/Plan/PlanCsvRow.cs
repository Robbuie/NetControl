namespace NetControl.Core.Plan;

/// <summary>
/// One row of a plan CSV, still entirely text.
///
/// <para>Nothing here is parsed, and that is the point. Parsing and reporting are the same pass in
/// <see cref="PlanValidation"/>, so that a malformed address becomes a sentence with a line number
/// on it rather than an exception from somewhere inside the reader. A row that reached this type is
/// a row the file's <em>shape</em> was fine for; whether its contents make sense is a separate
/// question with a separate answer.</para>
///
/// <para>The address columns arrive trimmed, because whitespace around an address is always an
/// accident. The text columns arrive verbatim, because whitespace inside a note might not be, and
/// <see cref="Persistence.DeviceRecord.DisplayName"/> already treats a blank label as no label.</para>
/// </summary>
public sealed record PlanCsvRow
{
    /// <summary>The line in the file this row started on. Every problem reported against it uses this.</summary>
    public required int LineNumber { get; init; }

    public string Mac { get; init; } = string.Empty;

    public string Ip { get; init; } = string.Empty;

    public string Mask { get; init; } = string.Empty;

    public string Gateway { get; init; } = string.Empty;

    public string HostName { get; init; } = string.Empty;

    public string PanelRef { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// True when every cell is blank. A file that has been through a spreadsheet often ends with a
    /// few of these, and they are not rows anybody typed - reporting eight problems against them
    /// would bury the one real problem above.
    /// </summary>
    public bool IsBlank =>
        Mac.Length == 0
        && Ip.Length == 0
        && Mask.Length == 0
        && Gateway.Length == 0
        && string.IsNullOrWhiteSpace(HostName)
        && string.IsNullOrWhiteSpace(PanelRef)
        && string.IsNullOrWhiteSpace(Role)
        && string.IsNullOrWhiteSpace(Notes);
}
