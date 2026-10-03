using Microsoft.Data.Sqlite;

namespace NetControl.Core.Persistence;

/// <summary>
/// Key and value settings that travel with the project file - what a job needs regardless of which
/// laptop opens it. Callers record each change in <see cref="EventLog"/>; this class only stores.
/// </summary>
public sealed class ProjectSettings
{
    private readonly ProjectStore _store;

    internal ProjectSettings(ProjectStore store) => _store = store;

    /// <summary>The value, or null when it has never been set.</summary>
    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _store.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM Setting WHERE Key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        });
    }

    /// <summary>Sets a value, or removes it when <paramref name="value"/> is null or blank.</summary>
    public void Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _store.InTransaction(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();

            if (string.IsNullOrWhiteSpace(value))
            {
                command.CommandText = "DELETE FROM Setting WHERE Key = $key;";
            }
            else
            {
                command.CommandText = """
                    INSERT INTO Setting (Key, Value) VALUES ($key, $value)
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                    """;
                command.Parameters.AddWithValue("$value", value);
            }

            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Every setting, by key.</summary>
    public IReadOnlyDictionary<string, string> All() => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Setting ORDER BY Key;";

        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            all[reader.GetString(0)] = reader.GetString(1);
        }

        return all;
    });
}
