namespace NetControl.Core.AdapterSetup;

/// <summary>How a new address goes onto the laptop's adapter.</summary>
public enum AdapterIpMode
{
    /// <summary>The adapter becomes static with this address as its only one.</summary>
    Replace,

    /// <summary>
    /// This address is added beside the ones the adapter already has - one cable, two subnets. Windows
    /// only allows it on an adapter that is already static.
    /// </summary>
    AddAddress,

    /// <summary>The adapter goes back to asking a DHCP server, for its address and its DNS.</summary>
    Dhcp,
}
