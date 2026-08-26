namespace NetControl.App.Diagnostics;

/// <summary>
/// How one precondition for "a request will arrive and we can answer it" currently stands.
///
/// <para>The numeric order is the severity order, and it is the reason the enum is written in
/// this sequence rather than the obvious one: <see cref="Unknown"/> deliberately sorts worse than
/// <see cref="Ready"/>, so that combining checks with a plain maximum can never let a thing we
/// failed to measure be reported as good news. A firewall we could not read is not a firewall
/// that is open, and the bar must never be green because a check quietly returned nothing.</para>
/// </summary>
public enum ReadinessState
{
    /// <summary>Measured, and fine. The only state that shows green.</summary>
    Ready = 0,

    /// <summary>Not measured, or not started yet. Grey. Never green.</summary>
    Unknown = 1,

    /// <summary>Measured, and it might explain a problem later. Amber.</summary>
    Warning = 2,

    /// <summary>Measured, and it will stop this working. Red.</summary>
    Blocked = 3,
}
