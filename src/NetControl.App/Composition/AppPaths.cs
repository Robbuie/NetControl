// UseWPF drops System.IO from the implicit usings, because WPF ships its own Path. This file is
// entirely about paths, so it asks for it back by name - see the note in CLAUDE.md.
using System.IO;

namespace NetControl.App.Composition;

/// <summary>
/// The folders this tool keeps its own things in.
///
/// <para>Under <c>%LOCALAPPDATA%</c> rather than beside the executable, deliberately. The product
/// is a single exe that gets copied onto a plant laptop - often into Downloads, sometimes onto a
/// USB stick, occasionally into a folder the user cannot write to - and a tool that tries to write
/// its log next to itself will one day silently write nothing. Nothing here is a project file:
/// those are wherever the user saved them, which is the point of Save As.</para>
///
/// <para>Local, not roaming: a diagnostic log has no business being synchronised to a domain
/// profile, and a version-check cache is about this machine.</para>
/// </summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\NetControl - created on demand by whatever writes into it.</summary>
    public static string Data { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        "NetControl");

    /// <summary>Where the rolling diagnostic log goes. See <c>TraceLog</c>.</summary>
    public static string Logs { get; } = Path.Combine(Data, "logs");

    /// <summary>
    /// The optional settings file. Absent by default and absent on most machines: everything it can
    /// hold has a working default, and the tool has to run correctly on a laptop where nobody has
    /// ever created it.
    /// </summary>
    public static string SettingsFile { get; } = Path.Combine(Data, "settings.json");
}
