namespace NetControl.Core.Commissioning;

/// <summary>
/// How a commissioning attempt ended.
///
/// <para>The distinctions are the product. "It didn't work" is what the tool being replaced says;
/// which of these it was decides whether somebody opens the panel, retries, or stops trusting the
/// result they were about to write on a drawing.</para>
/// </summary>
public enum CommissionOutcome
{
    /// <summary>
    /// Written and read back off the device. The only outcome that may be reported as success, and
    /// the only one that earns <c>DeviceState.Verified</c>.
    /// </summary>
    Verified,

    /// <summary>Never got a session. Nothing was sent, so nothing changed.</summary>
    Unreachable,

    /// <summary>
    /// Configuration Capability says the interface is not settable - the address is pinned by
    /// switches on the module. <b>Nothing was written.</b> No amount of retrying will help and
    /// somebody has to open the panel, which is worth knowing before spending an afternoon on it.
    /// </summary>
    NotSettable,

    /// <summary>The device refused a write and said why. Carries the CIP status text.</summary>
    Refused,

    /// <summary>
    /// The writes were accepted and the device came back saying something else.
    ///
    /// <para>This is the outcome that justifies the whole readback. A device that returns success
    /// and silently discards the write is real hardware behaviour, and it is the exact failure the
    /// Rockwell tool does not catch - it reports success on the status code alone.</para>
    /// </summary>
    Mismatch,

    /// <summary>
    /// The writes were accepted and the device never answered afterwards. Genuinely ambiguous: it
    /// may have taken the configuration and moved somewhere this laptop has no route to, or it may
    /// be waiting for a power cycle. Never reported as success.
    /// </summary>
    Unverified,
}
