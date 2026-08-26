namespace NetControl.App.ViewModels;

/// <summary>What a row in the live log is. Drives the icon and the colour, nothing else.</summary>
public enum LogEntryKind
{
    /// <summary>A BOOTP/DHCP request that landed, whether or not it was answered.</summary>
    Request = 0,

    /// <summary>A reply that actually went on the wire.</summary>
    Reply = 1,

    /// <summary>
    /// The listener stopped, or a reply could not be sent. Red, because something did not happen
    /// that was supposed to.
    /// </summary>
    Fault = 2,

    /// <summary>
    /// A non-fatal fault: worth knowing, but the server carried on. An advisory port conflict is
    /// the common one. Kept apart from <see cref="Fault"/> so the log agrees with the interface
    /// bar - the bar grades a Hyper-V Default Switch bind amber, and a red row saying the same
    /// thing would make one of the two a liar.
    /// </summary>
    Warning = 4,

    /// <summary>The tool itself: listener started, project opened, plan loaded.</summary>
    Notice = 3,
}
