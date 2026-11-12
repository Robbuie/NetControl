namespace NetControl.Core.Analysis;

/// <summary>Where an address-to-MAC observation came from.</summary>
public enum AddressObservationSource
{
    /// <summary>This PC's ARP table. Read locally; nothing is sent.</summary>
    ArpTable,

    /// <summary>An ARP frame heard on the wire through Npcap.</summary>
    ArpFrame,

    /// <summary>A gratuitous ARP - a device announcing its own address.</summary>
    Gratuitous,

    /// <summary>An RFC 5227 probe - a device asking whether an address is free before taking it.</summary>
    Probe,
}
