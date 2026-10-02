namespace NetControl.App.Serving;

/// <summary>
/// What the TFTP tab remembers between sessions: the things typed once per PC and then left alone.
/// Its own file - <see cref="TftpSettingsStore"/> - not a section of settings.json, which is site
/// configuration the app must never rewrite.
/// </summary>
public sealed record TftpSettings
{
    /// <summary>The backup folder: checked on the server, received into in Accept mode.</summary>
    public string? Folder { get; init; }

    public bool IsBackupServer { get; init; }

    public bool AllowOverwrite { get; init; }

    /// <summary>The last server the probe was pointed at.</summary>
    public string? ProbeServer { get; init; }
}
