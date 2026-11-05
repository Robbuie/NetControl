using NetControl.Core.AdapterSetup;

namespace NetControl.App.AdapterSetup;

/// <summary>
/// Reads the laptop's adapters and runs netsh against them. An interface so the view model is
/// tested with a fake: the real one changes this machine's network settings.
/// </summary>
public interface IAdapterConfigurator
{
    /// <summary>The adapter as Windows has it now, or null when it is gone.</summary>
    AdapterSnapshot? Read(int index);

    /// <summary>Runs netsh script lines (no leading "netsh") as one change.</summary>
    Task<NetshRun> RunAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken = default);
}

/// <summary>How a netsh run went.</summary>
/// <param name="Started">False when it never ran - the elevation prompt was declined, or netsh could not be started.</param>
/// <param name="Elevated">True when it needed, and got, administrator rights.</param>
/// <param name="ExitCode">netsh's exit code; 0 is success as netsh sees it.</param>
/// <param name="Output">What netsh printed, when it could be captured. An elevated run's cannot.</param>
/// <param name="Message">Why it did not start, when it did not.</param>
public sealed record NetshRun(bool Started, bool Elevated, int ExitCode, string Output, string? Message = null);
