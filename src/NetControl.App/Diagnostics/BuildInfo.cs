using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Which build this is.
///
/// <para>It exists because of the commissioning record. A project file is an account of what was
/// done to somebody's plant equipment, and an account that cannot say which version of the tool
/// wrote it is missing the one fact needed to interpret it after a bug is found and fixed. The
/// same string goes at the top of the diagnostic log, so a log file and a project file can be
/// matched to each other and to a release.</para>
///
/// <para>Read from this assembly rather than the entry assembly: under a test runner the entry
/// assembly is the runner.</para>
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The informational version - <c>0.5.0</c>, or <c>0.5.0+a1b2c3d</c> when the build was given a
    /// <c>SourceRevisionId</c>. That suffix is the difference between "0.5.0" and "the 0.5.0 that
    /// was on the laptop that Tuesday", so it is kept whole rather than trimmed to three numbers.
    /// </summary>
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The name to put in front of it. Not read from anywhere; this is the product.</summary>
    public const string Product = "NetControl";

    /// <summary>
    /// One line naming the build and the machine it is running on, for the top of the diagnostic
    /// log and for the project file's first event row. The runtime and the OS are here because the
    /// two faults this project has already paid for - WPF under InvariantGlobalization, and a
    /// hand-marshalled native struct - are both things that depend on exactly this.
    /// </summary>
    public static string Describe() =>
        $"{Product} {Version} - {RuntimeInformation.FrameworkDescription} on "
        + $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// The shorter form that goes into the project file: what wrote to it, and nothing about the
    /// machine, which is already in the diagnostic log and is not part of the commissioning record.
    /// </summary>
    public static string Stamp() => $"Opened by {Product} {Version}.";

    /// <summary>
    /// Where the running executable is, for the firewall check's benefit and for a message that has
    /// to tell somebody which copy of the tool they are looking at. Null if it cannot be determined.
    /// </summary>
    public static string? ExecutablePath => Process.GetCurrentProcess().MainModule?.FileName;
}
