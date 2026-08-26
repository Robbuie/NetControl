using NetControl.Core.Cip;

namespace NetControl.Core.Commissioning;

/// <summary>
/// How it went, in a form that can be shown to somebody and written into the commissioning record
/// without being reworded on the way.
/// </summary>
public sealed record CommissionResult
{
    public required CommissionOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public string? Remediation { get; init; }

    /// <summary>What the device reported when read back, when it was reachable to be read.</summary>
    public InterfaceConfig? Readback { get; init; }

    /// <summary>The method the device reported afterwards. Static is what proves BOOTP is off.</summary>
    public ConfigMethod? ReportedMethod { get; init; }

    /// <summary>
    /// Whether anything was written to the device.
    ///
    /// <para>Recorded rather than inferred from the outcome, because "we changed nothing" is the
    /// single most valuable sentence after a failed attempt on live equipment. A refusal at the
    /// capability check leaves this false; a refusal at attribute 5 does not, because attribute 3
    /// has already moved the device to Static.</para>
    /// </summary>
    public bool WroteToDevice { get; init; }

    /// <summary>Only ever true when the caller allowed it. See <see cref="StaticIpRequest.AllowReset"/>.</summary>
    public bool ResetTheDevice { get; init; }

    public bool IsVerified => Outcome == CommissionOutcome.Verified;
}
