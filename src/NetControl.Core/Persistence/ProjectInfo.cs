namespace NetControl.Core.Persistence;

/// <summary>
/// The single <c>Project</c> row: what this file is, and which schema wrote it.
/// One project per file, so there is exactly one of these.
/// </summary>
/// <param name="Id">Always 1. The column exists because the roadmap schema has it, not because there is ever a second row.</param>
/// <param name="Name">Display name. Defaults to the file name, and is editable.</param>
/// <param name="SchemaVersion">Schema version at the time the file was last opened and migrated.</param>
/// <param name="CreatedUtc">When the file was first created, not when it was last opened.</param>
public sealed record ProjectInfo(long Id, string Name, int SchemaVersion, DateTimeOffset CreatedUtc);
