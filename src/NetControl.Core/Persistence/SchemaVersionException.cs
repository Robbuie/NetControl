namespace NetControl.Core.Persistence;

/// <summary>
/// The file on disk was written by a different build than the one trying to open it.
///
/// This is a separate type because the two directions need different handling and the UI has to
/// tell them apart. A file from an <em>older</em> build migrates forward silently. A file from a
/// <em>newer</em> build cannot be opened at all: downgrading a schema means guessing at data this
/// build has never heard of, and quietly dropping a column out of a customer's commissioning
/// record is not something to do on a hunch.
/// </summary>
public sealed class SchemaVersionException : PersistenceException
{
    public SchemaVersionException(string path, int fileVersion, int supportedVersion)
        : base($"'{path}' was created by a newer version of this tool (schema v{fileVersion}; " +
               $"this build understands up to v{supportedVersion}).")
    {
        Path = path;
        FileVersion = fileVersion;
        SupportedVersion = supportedVersion;
        Remediation = "Update NetControl to at least the version that wrote this project, or open a copy "
            + "made by the older build. This build will not downgrade the file.";
    }

    public string Path { get; }

    /// <summary>Schema version recorded in the file.</summary>
    public int FileVersion { get; }

    /// <summary>Highest schema version this build knows how to read.</summary>
    public int SupportedVersion { get; }
}
