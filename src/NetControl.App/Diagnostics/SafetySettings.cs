using NetControl.Core.Safety;

namespace NetControl.App.Diagnostics;

/// <summary>
/// The network-safety choices, remembered per PC: listen-only, the send-rate cap, and whether the
/// diagnostic log is verbose. Per PC rather than per project, because they describe how this
/// laptop may behave on whatever network it is plugged into - and a laptop left in listen-only
/// mode should still be in it after a restart.
/// </summary>
public sealed record SafetySettings
{
    public bool ListenOnly { get; init; }

    public int MaxPacketsPerSecond { get; init; } = NetworkGuard.DefaultMaxPacketsPerSecond;

    public bool VerboseLog { get; init; }
}
