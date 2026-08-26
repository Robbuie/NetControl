using System.Net;
using Microsoft.Data.Sqlite;

namespace NetControl.Core.Persistence;

/// <summary>
/// Column readers that tolerate a file somebody has edited by hand.
///
/// A project file is a SQLite database, which means sooner or later an engineer opens it in a
/// browser tool and fixes something at 2am. Reads therefore degrade rather than throw: an
/// unparseable address comes back as null, which makes the row unplanned, which the planner then
/// reports by name. Refusing to open the whole project because one cell is wrong would be the
/// worse failure.
/// </summary>
internal static class SqlRead
{
    public static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>An address column. Null when absent, blank, or not parseable as IPv4.</summary>
    public static IPAddress? Address(SqliteDataReader reader, int ordinal)
    {
        string? text = NullableString(reader, ordinal);

        return !string.IsNullOrWhiteSpace(text)
            && IPAddress.TryParse(text, out IPAddress? address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? address
                : null;
    }

    /// <summary>
    /// An enum stored as its lower-cased member name. Unrecognised text maps to
    /// <paramref name="fallback"/> so a file from a newer build still opens.
    /// </summary>
    public static T EnumValue<T>(SqliteDataReader reader, int ordinal, T fallback)
        where T : struct, Enum =>
        Enum.TryParse(NullableString(reader, ordinal), ignoreCase: true, out T value) ? value : fallback;

    /// <summary>The canonical text form an enum is stored as: the member name, lower-cased.</summary>
    public static string EnumText<T>(T value)
        where T : struct, Enum =>
        value.ToString().ToLowerInvariant();
}
