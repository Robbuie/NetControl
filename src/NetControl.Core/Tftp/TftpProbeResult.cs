using System.Net;

namespace NetControl.Core.Tftp;

/// <summary>
/// What a probe found. The summary is one sentence; the findings are everything else worth knowing,
/// in the order they were learned - including the ones that did not stop the probe, like a server
/// answering from an address it was not asked on.
/// </summary>
public sealed record TftpProbeResult
{
    public bool Succeeded { get; init; }

    public required string Summary { get; init; }

    public string? Remediation { get; init; }

    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>The file the probe wrote, which it leaves on the server.</summary>
    public required string FileName { get; init; }

    /// <summary>Whether the server accepted the write at all - the file exists there if so.</summary>
    public bool WriteAccepted { get; init; }

    /// <summary>The endpoint the server's first answer came from - its transfer identifier.</summary>
    public IPEndPoint? AnsweredFrom { get; init; }

    /// <summary>Time from the write request to the first answer.</summary>
    public TimeSpan? FirstResponse { get; init; }

    public TftpOptions Requested { get; init; } = TftpOptions.None;

    /// <summary>What the server agreed to; null when it never answered, empty when it ignored the options.</summary>
    public TftpOptions? Granted { get; init; }

    public TftpTransferOutcome? Write { get; init; }

    public TftpTransferOutcome? Read { get; init; }

    /// <summary>True when the copy read back hashed the same as what was written; null when not read back.</summary>
    public bool? ReadBackMatched { get; init; }
}
