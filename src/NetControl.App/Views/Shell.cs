// UseWPF drops System.IO from the implicit usings; FileNotFoundException is caught below.
using System.IO;
using System.Windows;

namespace NetControl.App.Views;

/// <summary>
/// Hands a URL or a folder to Windows to open with whatever the user has chosen for it.
///
/// <para><c>UseShellExecute</c> is required for that and is the point. <b>It is never given a path
/// to an executable</b>, which is the line between opening a page and running something somebody
/// downloaded - the one place this application does run a downloaded file is
/// <c>UpdateApplier</c>, which goes through a verified <c>UpdateDownload</c> and does not use the
/// shell at all.</para>
/// </summary>
internal static class Shell
{
    public static void Open(Window owner, string target)
    {
        try
        {
            using System.Diagnostics.Process? started = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or ObjectDisposedException or FileNotFoundException)
        {
            // A locked-down laptop with no default browser, or a policy that blocks the shell. The
            // address is in the message so it can be typed somewhere else.
            MessageBox.Show(
                owner,
                $"Could not open {target}: {ex.Message}",
                "NetControl",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
