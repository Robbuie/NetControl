namespace NetControl.Core.Discovery;

/// <summary>
/// What a scan had to say about one row of the plan, or one device that answered it.
///
/// <para>The distinctions are the product, the same way <c>CommissionOutcome</c>'s are. "The plan
/// and the network disagree" is not useful; which of these it is decides whether somebody edits a
/// cell, opens a panel, or stops and finds out what else is on the segment before serving
/// anything.</para>
/// </summary>
public enum PlanFindingKind
{
    /// <summary>
    /// A planned device answered at its planned address. The cheap version of the CIP readback,
    /// and the only finding here that is good news.
    /// </summary>
    Confirmed,

    /// <summary>
    /// Something else is already sitting on an address the plan hands to a device.
    ///
    /// <para><b>This is the check the whole active scan exists for.</b> A duplicate check that only
    /// knows the plan clears an address a switch, an HMI or last year's untracked device is already
    /// holding, because none of those were ever typed into it. The live segment is the only honest
    /// place to ask.</para>
    /// </summary>
    AddressTakenByAnother,

    /// <summary>
    /// A planned address answered, and there is no hardware address to say whether it is the
    /// planned device or a different one. Not the same as a conflict, and not the same as a
    /// confirmation - which is exactly why it is neither.
    /// </summary>
    AddressUnattributable,

    /// <summary>
    /// More than one device answered on a planned address. The fault only a scan can find.
    /// </summary>
    ContestedAddress,

    /// <summary>
    /// A planned device answered from an address that is not the one planned for it. Usually it
    /// simply has not been commissioned yet; occasionally something else moved it.
    /// </summary>
    PlannedDeviceElsewhere,

    /// <summary>
    /// A planned device with no address typed into it yet is on the network and holding one. The
    /// tool still does not fill the cell in - an address comes off a drawing - but it can say what
    /// the device currently thinks.
    /// </summary>
    PlannedDeviceUnaddressed,

    /// <summary>
    /// A device answered that is not in the plan at all. Inventory rather than a fault: most
    /// segments carry switches, HMIs and last year's work, and none of that belongs in a
    /// commissioning plan.
    /// </summary>
    NotInPlan,
}
