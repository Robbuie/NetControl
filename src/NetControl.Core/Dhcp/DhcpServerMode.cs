namespace NetControl.Core.Dhcp;

/// <summary>
/// Serve mode is explicit, always. A rogue DHCP server on a plant network is a serious incident,
/// so handing out addresses is something the user turns on, never something that happens because
/// a plan file happened to be loaded.
/// </summary>
public enum DhcpServerMode
{
    /// <summary>Listen and log. Nothing is ever transmitted. The default.</summary>
    Watch = 0,

    /// <summary>Answer requests the assignment policy approves, and only those.</summary>
    Serve = 1,
}
