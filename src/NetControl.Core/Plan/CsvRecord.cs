namespace NetControl.Core.Plan;

/// <summary>
/// One record out of a CSV file, with the line it started on.
///
/// <para><see cref="LineNumber"/> is the line a text editor would put the cursor on, which is the
/// whole reason it is carried: every problem this file produces has to be findable by hand. It is
/// the line the record <em>started</em> on, because a quoted field may contain newlines and the
/// record may therefore span several lines - the start is the one worth reporting.</para>
/// </summary>
/// <param name="LineNumber">1-based, counting every physical line including quoted ones.</param>
/// <param name="Fields">The fields, unquoted and unescaped. Never null, may contain empty strings.</param>
public sealed record CsvRecord(int LineNumber, IReadOnlyList<string> Fields);
