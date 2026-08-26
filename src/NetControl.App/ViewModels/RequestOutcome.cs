namespace NetControl.App.ViewModels;

/// <summary>
/// What happened to one request. The distinction that earns its keep is
/// <see cref="NotInPlan"/> against <see cref="Refused"/>: the first is a device nobody typed in,
/// the second is a device you are waiting for that could not be answered for a stated reason.
/// Colouring both "ignored" would hide the row that matters inside the rows that do not.
/// </summary>
public enum RequestOutcome
{
    /// <summary>Not a request row.</summary>
    None = 0,

    /// <summary>Answered. An address went on the wire.</summary>
    Served = 1,

    /// <summary>In the plan and servable, but the tool is in Watch mode so nothing was sent.</summary>
    Watching = 2,

    /// <summary>The MAC is not in the plan. Serve mode never invents an address for a stranger.</summary>
    NotInPlan = 3,

    /// <summary>In the plan, but the policy declined - wrong subnet, adapter cannot reply, and so on.</summary>
    Refused = 4,

    /// <summary>Arrived on an adapter other than the selected one. Reported, never dropped.</summary>
    WrongAdapter = 5,
}
