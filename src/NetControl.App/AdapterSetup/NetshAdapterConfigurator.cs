// UseWPF drops System.IO from the implicit usings; this file writes a script file. See CLAUDE.md.
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using NetControl.Core.AdapterSetup;
using NetControl.Core.Diagnostics;

namespace NetControl.App.AdapterSetup;

/// <summary>
/// Runs a change as one <c>netsh -f</c> script.
///
/// <para><b>The application never runs as administrator, and this does not change that.</b> The
/// script is tried as the user first: a member of the Windows <i>Network Configuration Operators</i>
/// group may change adapter settings without elevation, and on a plant laptop set up that way there
/// is no prompt at all. Only when netsh says it needs elevation is the same script run again with
/// "Run as administrator" - one UAC prompt naming the Network Command Shell, for this change and
/// nothing else.</para>
/// </summary>
public sealed class NetshAdapterConfigurator(ITraceLog? trace = null) : IAdapterConfigurator
{
    /// <summary>ERROR_CANCELLED: the person said No to the elevation prompt.</summary>
    private const int ElevationDeclined = 1223;

    private readonly ITraceLog _trace = trace ?? NullTraceLog.Instance;

    public AdapterSnapshot? Read(int index) => AdapterSnapshot.Read(index);

    public async Task<NetshRun> RunAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        string script = Path.Combine(Path.GetTempPath(), $"netcontrol-netsh-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllLinesAsync(script, lines, Encoding.ASCII, cancellationToken).ConfigureAwait(false);

            NetshRun plain = await RunAsUserAsync(script, cancellationToken).ConfigureAwait(false);
            if (!plain.Started || !NeedsElevation(plain))
            {
                return plain;
            }

            _trace.Info("netsh needs elevation for an adapter change; asking.");
            return await RunElevatedAsync(script, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stray script in %TEMP% holds nothing but the netsh lines, which the record also holds.
            }
        }
    }

    private static string NetshPath => Path.Combine(Environment.SystemDirectory, "netsh.exe");

    private static bool NeedsElevation(NetshRun run) =>
        run.ExitCode != 0
        && (run.Output.Contains("elevation", StringComparison.OrdinalIgnoreCase)
            || run.Output.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            || run.Output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase));

    private async Task<NetshRun> RunAsUserAsync(string script, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(NetshPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(script);

        try
        {
            using Process process = Process.Start(start)
                ?? throw new Win32Exception("netsh could not be started.");

            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string text = ((await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false))).Trim();
            return new NetshRun(true, false, process.ExitCode, text);
        }
        catch (Win32Exception ex)
        {
            _trace.Warn("netsh could not be started.", ex);
            return new NetshRun(false, false, -1, string.Empty, $"netsh could not be started: {ex.Message}");
        }
    }

    private async Task<NetshRun> RunElevatedAsync(string script, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(NetshPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-f \"{script}\"",
        };

        try
        {
            using Process process = Process.Start(start)
                ?? throw new Win32Exception("netsh could not be started.");

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new NetshRun(true, true, process.ExitCode, string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ElevationDeclined)
        {
            return new NetshRun(false, false, -1, string.Empty, "The administrator prompt was declined. Nothing was changed.");
        }
        catch (Win32Exception ex)
        {
            _trace.Warn("netsh could not be started elevated.", ex);
            return new NetshRun(false, false, -1, string.Empty, $"netsh could not be started as administrator: {ex.Message}");
        }
    }
}
