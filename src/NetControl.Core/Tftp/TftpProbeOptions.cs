using System.Globalization;
using System.Net;

namespace NetControl.Core.Tftp;

/// <summary>
/// What a probe writes, where, and how big. PLAN-TFTP.md part E4.
///
/// <para><b>The probe is the only thing in this tool that writes to plant infrastructure over
/// TFTP</b>, so its rules are hard ones: it writes exactly one file, to the name the user typed, and
/// it never deletes anything - TFTP has no delete, and a tool that tidied up after itself on a plant
/// server would be one bug away from an incident. The file it leaves is reported by name.</para>
/// </summary>
public sealed record TftpProbeOptions
{
    /// <summary>
    /// 40 MiB: past the 32 MiB a 512-byte block counter can address, so one default run answers
    /// the rollover question for this server permanently.
    /// </summary>
    public const long DefaultSizeBytes = 40L * 1024 * 1024;

    public required IPAddress Server { get; init; }

    public int ServerPort { get; init; } = TftpLimits.ServerPort;

    /// <summary>
    /// The local address to leave from - the adapter on the robot network, so the probe takes the
    /// same path the controller's backup does. Null lets Windows choose.
    /// </summary>
    public IPAddress? LocalAddress { get; init; }

    /// <summary>The file to write on the server. Typed by the user; never derived from a real backup name.</summary>
    public required string FileName { get; init; }

    public long SizeBytes { get; init; } = DefaultSizeBytes;

    /// <summary>
    /// A block size to ask for, or null to ask for none - which is what a plain RFC 1350 client
    /// such as a controller does, and so the default.
    /// </summary>
    public int? BlockSize { get; init; }

    /// <summary>Read the file back afterwards and compare it, byte for byte, by hash.</summary>
    public bool ReadBack { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxRetries { get; init; } = TftpTransfer.DefaultMaxRetries;

    /// <summary>Unmistakably a test file, and unique, so it can never overwrite a real backup.</summary>
    public static string DefaultFileName(DateTimeOffset now) =>
        "netcontrol-probe-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".bin";
}
