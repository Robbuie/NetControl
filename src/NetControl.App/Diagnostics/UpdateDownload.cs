namespace NetControl.App.Diagnostics;

/// <summary>
/// What came of trying to fetch an update, in a form the dialog can show and the applier can act
/// on without re-deciding anything.
///
/// <para><b>The type cannot hold both a file and a refusal.</b> There are two factories and no
/// public constructor, so "a download that failed verification has no file to run" is a property of
/// the type rather than a rule every caller has to remember - the same reasoning as
/// <c>PlanValidationResult</c>, and for a much worse failure if it is forgotten.</para>
/// </summary>
public sealed record UpdateDownload
{
    private UpdateDownload()
    {
    }

    /// <summary>The file on disk, verified against its published checksum. Null on a refusal.</summary>
    public string? Path { get; private init; }

    /// <summary>The SHA256 that both the file and the published checksum agreed on.</summary>
    public string? Sha256 { get; private init; }

    /// <summary>Why there is no file. Null when there is one.</summary>
    public string? Problem { get; private init; }

    public bool IsVerified => Path is not null;

    /// <summary>A file that arrived whole and hashed to what the release said it would.</summary>
    public static UpdateDownload Verified(string path, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        return new UpdateDownload { Path = path, Sha256 = sha256 };
    }

    /// <summary>
    /// No file. Everything that went wrong ends up here - no route out, a truncated transfer, a
    /// checksum that was not published, a checksum that did not match - because from the caller's
    /// side they are one outcome: nothing is going to be run.
    /// </summary>
    public static UpdateDownload Refused(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        return new UpdateDownload { Problem = problem };
    }
}
