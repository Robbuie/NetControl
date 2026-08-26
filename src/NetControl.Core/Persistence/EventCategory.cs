namespace NetControl.Core.Persistence;

/// <summary>
/// Which subsystem an event came from. Stored as the lower-cased member name.
///
/// This exists so the log can be filtered without string matching on messages. A file written by
/// a later build may contain categories this one has never heard of; readers map those to
/// <see cref="Other"/> rather than failing, because refusing to open a commissioning record over
/// an unrecognised label would be a worse outcome than showing it uncategorised.
/// </summary>
public enum EventCategory
{
    /// <summary>BOOTP/DHCP: requests seen, replies sent, faults.</summary>
    Dhcp,

    /// <summary>EtherNet/IP and CIP: static address writes, readbacks, resets.</summary>
    Cip,

    /// <summary>Discovery and scanning.</summary>
    Scan,

    /// <summary>The application itself: project opened, plan imported, settings changed.</summary>
    App,

    /// <summary>A category written by a build newer than this one.</summary>
    Other,
}
