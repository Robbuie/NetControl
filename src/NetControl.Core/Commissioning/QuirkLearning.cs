namespace NetControl.Core.Commissioning;

/// <summary>
/// Turns how a Set static went into the quirks it proves.
///
/// <para><b>Only from evidence the device itself gave.</b> A device that refused because its
/// address is pinned said so in its own capability attribute; one that held the write pending said
/// so by not applying it; one that answered success and came back unchanged did it in front of the
/// readback. A timeout is not evidence of anything, so slowness is never learned - a laptop on the
/// wrong VLAN looks exactly like a slow device - and is left for somebody to tick by hand.</para>
///
/// <para>Pure, and additive: it never clears a flag. A flag that turns out to be wrong is unticked
/// by a person in the editor, which writes its own event row.</para>
/// </summary>
public static class QuirkLearning
{
    public static LearnedQuirks Learn(CommissionResult result, DeviceQuirks existing)
    {
        ArgumentNullException.ThrowIfNull(result);

        var learned = DeviceQuirks.None;
        var reasons = new List<string>();

        void Note(DeviceQuirks flag, string reason)
        {
            if (!existing.HasFlag(flag) && !learned.HasFlag(flag))
            {
                learned |= flag;
                reasons.Add(reason);
            }
        }

        if (result.Outcome == CommissionOutcome.NotSettable)
        {
            Note(DeviceQuirks.HardwarePinnedAddress,
                "its Configuration Capability says the address cannot be set over the network");
        }

        if (result.ResetPending || (result.IsVerified && result.ResetTheDevice))
        {
            Note(DeviceQuirks.RequiresResetToApply,
                "it held the new configuration until a reset");
        }

        if (result.Outcome == CommissionOutcome.Mismatch && result.WroteToDevice)
        {
            Note(DeviceQuirks.LiesAboutWriteSuccess,
                "it accepted the write and the readback showed it had not taken it");
        }

        if (result.ConnectionDroppedAfterWrite)
        {
            Note(DeviceQuirks.DropsConnectionOnWrite,
                "it dropped the connection after the write had started");
        }

        return new LearnedQuirks(learned, reasons);
    }
}
