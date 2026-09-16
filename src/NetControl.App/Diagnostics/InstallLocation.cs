// UseWPF drops System.IO from the implicit usings; this file is about paths. See CLAUDE.md.
using System.IO;
using Microsoft.Win32;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Whether this copy was installed or is a loose exe, and where the installed one lives.
///
/// <para>The question matters because the two update themselves differently, and getting it wrong
/// is not a no-op. Running the installer from a copy on a USB stick would install a second
/// NetControl into the user's profile and leave the one they are looking at untouched and stale -
/// which looks, from the outside, exactly like an update that did nothing.</para>
///
/// <para><b>Asked of the registry rather than of the path.</b> The installer is per-user by
/// default, but <c>PrivilegesRequiredOverridesAllowed</c> lets a site put it under Program Files
/// instead, and a path comparison against <c>%LOCALAPPDATA%\Programs</c> would call that copy
/// portable and then try to swap a file in a folder the user cannot write to. Inno Setup records
/// the real location under the AppId, so that is what is read; the path check remains only as the
/// answer when the registry cannot be read at all.</para>
/// </summary>
public static class InstallLocation
{
    /// <summary>
    /// Inno Setup's uninstall key. The <c>_is1</c> suffix is Inno's, and the GUID is the
    /// <c>AppId</c> in <c>installer/NetControl.iss</c> - which is why that GUID must never change.
    /// </summary>
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B2934E15-79BE-411A-9305-543B01455706}_is1";

    /// <summary>
    /// Where setup put NetControl, or null if this machine has no installed copy. Read from HKCU
    /// then HKLM, because a per-user install writes to the first and an all-users install to the
    /// second.
    /// </summary>
    public static string? InstalledFolder(ITraceLog? trace = null)
    {
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            return Read(Registry.CurrentUser) ?? Read(Registry.LocalMachine);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            // A locked-down laptop may refuse the read outright. That is not a failure worth
            // stopping for: Describe falls back to the path, which is right on the common machine.
            log.Warn("The uninstall registry key could not be read; falling back to the path.", ex);
            return null;
        }
    }

    /// <summary>
    /// What <paramref name="executablePath"/> is: the installed copy, or a loose one.
    ///
    /// <para>A null or unknown path is reported as <see cref="InstallKind.Portable"/>, deliberately.
    /// Portable is the conservative answer: its update path touches only the folder the exe is
    /// already sitting in, reports honestly when it cannot write there, and can be rolled back.
    /// Guessing Installed would run an installer.</para>
    /// </summary>
    public static InstallKind Describe(string? executablePath, ITraceLog? trace = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return InstallKind.Portable;
        }

        string? folder;

        try
        {
            folder = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
            or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return InstallKind.Portable;
        }

        if (folder is null)
        {
            return InstallKind.Portable;
        }

        if (InstalledFolder(trace) is { } installed)
        {
            return Same(folder, installed) ? InstallKind.Installed : InstallKind.Portable;
        }

        // No registry answer. The default install location is the only thing left worth comparing
        // against, and it is where the overwhelming majority of installed copies are.
        string standard = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "Programs",
            "NetControl");

        return Same(folder, standard) ? InstallKind.Installed : InstallKind.Portable;
    }

    private static string? Read(RegistryKey hive)
    {
        using RegistryKey? key = hive.OpenSubKey(UninstallKey);

        if (key is null)
        {
            return null;
        }

        return key.GetValue("InstallLocation") is string value && value.Length > 0 ? value : null;
    }

    /// <summary>
    /// Two folder paths naming the same folder. Trailing separators differ between what Inno
    /// records and what <c>Path.GetDirectoryName</c> returns, and Windows paths are case-insensitive.
    /// </summary>
    private static bool Same(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
