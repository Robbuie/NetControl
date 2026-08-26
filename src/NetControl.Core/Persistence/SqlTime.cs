using System.Globalization;

namespace NetControl.Core.Persistence;

/// <summary>
/// The one way timestamps go in and out of the database.
///
/// SQLite has no date type, so these are text. The format is round-trip UTC ("O"), chosen because
/// it is fixed width - which means lexicographic text ordering is chronological ordering, which
/// is what makes <c>IX_Event_Utc</c> and <c>ORDER BY Utc</c> actually work. A shorter or
/// variable-width format would sort wrongly across a fractional-second boundary and nobody would
/// notice until a commissioning report listed events out of order.
/// </summary>
internal static class SqlTime
{
    public static string ToSql(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset FromSql(string text) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset value)
            ? value.ToUniversalTime()
            : throw new PersistenceException($"'{text}' is not a timestamp this build can read.")
            {
                Remediation = "The project file may be corrupt or hand-edited. Restore a backup, or export "
                    + "the plan and start a new project file.",
            };
}
