using System.Buffers.Binary;
using System.Net;

namespace NetControl.DeviceSim;

/// <summary>
/// Behaviours observed on real hardware. Every one of these has cost somebody an
/// afternoon at some point; reproducing them at a desk is the whole point of the
/// simulator. Add a flag here whenever a new device misbehaves in a new way.
/// </summary>
[Flags]
public enum Quirk
{
    None = 0,

    /// <summary>Rejects a write to attribute 5 while still in BOOTP/DHCP mode.
    /// This is why the real sequence sets attribute 3 first.</summary>
    RejectConfigWhileDynamic = 1 << 0,

    /// <summary>Address is pinned by rotary/DIP switches: ConfigurationSettable is clear
    /// and every write is refused.</summary>
    HardwarePinnedAddress = 1 << 1,

    /// <summary>Accepts writes but holds them pending until an Identity reset.
    /// Reads before the reset still report the OLD values.</summary>
    RequiresResetToApply = 1 << 2,

    /// <summary>Returns success for the write but silently discards it - the readback
    /// is the only thing that catches this, which is exactly why we always read back.</summary>
    LiesAboutWriteSuccess = 1 << 3,

    /// <summary>Slow, thin TCP stack. Adds latency to every response.</summary>
    SlowResponses = 1 << 4,

    /// <summary>Drops the TCP connection partway through a set operation.</summary>
    DropConnectionOnWrite = 1 << 5,

    /// <summary>Does not answer ListIdentity broadcasts, only unicast.</summary>
    IgnoresBroadcastDiscovery = 1 << 6,
}

/// <summary>Class 0xF5 attribute 3, low nibble.</summary>
public enum ConfigMethod : uint { Static = 0, Bootp = 1, Dhcp = 2 }

/// <summary>Class 0xF5 attribute 2 bit flags.</summary>
[Flags]
public enum ConfigCapability : uint
{
    BootpClient           = 1 << 0,
    DnsClient             = 1 << 1,
    DhcpClient            = 1 << 2,
    DhcpDnsUpdate         = 1 << 3,
    ConfigurationSettable = 1 << 4,
    HardwareConfigurable  = 1 << 5,
    InterfaceResetNeeded  = 1 << 6,
    AcdCapable            = 1 << 7,
}

/// <summary>Mutable state of one fake EtherNet/IP adapter.</summary>
public sealed class SimulatedDevice
{
    // --- identity ----------------------------------------------------------
    public ushort VendorId { get; init; } = 1;              // Rockwell Automation
    public ushort DeviceType { get; init; } = 12;           // communications adapter
    public ushort ProductCode { get; init; } = 167;
    public byte MajorRevision { get; init; } = 11;
    public byte MinorRevision { get; init; } = 3;
    public ushort DeviceStatus { get; init; } = 0x0030;
    public uint SerialNumber { get; init; } = 0x00C0FFEE;
    public string ProductName { get; init; } = "SIM-ENBT/A Simulated Adapter";
    public byte[] MacAddress { get; init; } = [0x00, 0x00, 0xBC, 0x5E, 0x11, 0x01];

    // --- network configuration --------------------------------------------
    public IPAddress Ip { get; set; } = IPAddress.Parse("192.168.1.51");
    public IPAddress Mask { get; set; } = IPAddress.Parse("255.255.255.0");
    public IPAddress Gateway { get; set; } = IPAddress.Any;
    public IPAddress NameServer1 { get; set; } = IPAddress.Any;
    public IPAddress NameServer2 { get; set; } = IPAddress.Any;
    public string DomainName { get; set; } = "";
    public string HostName { get; set; } = "sim-adapter";
    public ConfigMethod Method { get; set; } = ConfigMethod.Bootp;

    // --- behaviour ---------------------------------------------------------
    public Quirk Quirks { get; init; } = Quirk.None;
    public int ResponseDelayMs => Quirks.HasFlag(Quirk.SlowResponses) ? 400 : 0;

    /// <summary>Set by RequiresResetToApply: written but not yet visible to a read.</summary>
    private PendingConfig? _pending;

    public ConfigCapability Capability
    {
        get
        {
            var c = ConfigCapability.BootpClient | ConfigCapability.DhcpClient | ConfigCapability.DnsClient;
            if (!Quirks.HasFlag(Quirk.HardwarePinnedAddress)) c |= ConfigCapability.ConfigurationSettable;
            if (Quirks.HasFlag(Quirk.HardwarePinnedAddress)) c |= ConfigCapability.HardwareConfigurable;
            if (Quirks.HasFlag(Quirk.RequiresResetToApply)) c |= ConfigCapability.InterfaceResetNeeded;
            return c;
        }
    }

    public string MacString => string.Join(':', MacAddress.Select(b => b.ToString("X2")));

    // -----------------------------------------------------------------------
    /// <summary>Attribute 5 - Interface Configuration.</summary>
    public byte[] SerializeInterfaceConfig()
    {
        var domain = CipCodec.CipString(DomainName);
        var buf = new byte[20 + domain.Length];
        var s = buf.AsSpan();
        CipCodec.WriteCipIp(s, Ip);
        CipCodec.WriteCipIp(s[4..], Mask);
        CipCodec.WriteCipIp(s[8..], Gateway);
        CipCodec.WriteCipIp(s[12..], NameServer1);
        CipCodec.WriteCipIp(s[16..], NameServer2);
        domain.CopyTo(s[20..]);
        return buf;
    }

    /// <summary>Attribute 5 write. Returns a CIP general status.</summary>
    public byte ApplyInterfaceConfig(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return CipStatus.NotEnoughData;
        if (Quirks.HasFlag(Quirk.HardwarePinnedAddress)) return CipStatus.AttributeNotSettable;

        // The real reason the commissioning sequence writes attribute 3 first.
        if (Quirks.HasFlag(Quirk.RejectConfigWhileDynamic) && Method != ConfigMethod.Static)
            return CipStatus.ObjectStateConflict;

        var cfg = new PendingConfig(
            CipCodec.ReadCipIp(data),
            CipCodec.ReadCipIp(data[4..]),
            CipCodec.ReadCipIp(data[8..]),
            CipCodec.ReadCipIp(data[12..]),
            CipCodec.ReadCipIp(data[16..]));

        if (cfg.Ip.Equals(IPAddress.Any)) return CipStatus.InvalidAttributeValue;

        // Reports success, changes nothing. Only a readback catches it.
        if (Quirks.HasFlag(Quirk.LiesAboutWriteSuccess)) return CipStatus.Success;

        if (Quirks.HasFlag(Quirk.RequiresResetToApply)) { _pending = cfg; return CipStatus.Success; }

        Commit(cfg);
        return CipStatus.Success;
    }

    /// <summary>Attribute 3 write - Configuration Control.</summary>
    public byte ApplyConfigControl(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return CipStatus.NotEnoughData;
        if (Quirks.HasFlag(Quirk.HardwarePinnedAddress)) return CipStatus.AttributeNotSettable;

        uint v = CipCodec.ReadU32(data) & 0xF;
        if (v > 2) return CipStatus.InvalidAttributeValue;

        Method = (ConfigMethod)v;
        return CipStatus.Success;
    }

    /// <summary>Identity object service 0x05. Applies anything held pending.</summary>
    public void ResetDevice()
    {
        if (_pending is { } p) { Commit(p); _pending = null; }
    }

    private void Commit(PendingConfig c)
    {
        Ip = c.Ip;
        Mask = c.Mask;
        Gateway = c.Gateway;
        NameServer1 = c.Ns1;
        NameServer2 = c.Ns2;
    }

    /// <summary>Attribute 1 - interface status. Bit 0..3 = configuration status.</summary>
    public uint InterfaceStatus => Method == ConfigMethod.Static ? 1u : 2u;

    /// <summary>Identity object, Get_Attribute_All.</summary>
    public byte[] SerializeIdentity()
    {
        var name = CipCodec.CipShortString(ProductName);
        // 14 bytes of fixed fields, then the SHORT_STRING (its own length byte included).
        var buf = new byte[14 + name.Length];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, VendorId);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], DeviceType);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], ProductCode);
        s[6] = MajorRevision;
        s[7] = MinorRevision;
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], DeviceStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(s[10..], SerialNumber);
        name.CopyTo(s[14..]);            // SHORT_STRING: length byte at offset 14
        return buf;
    }

    public override string ToString() =>
        $"{ProductName}  mac={MacString}  ip={Ip}/{Mask}  method={Method}" +
        (Quirks == Quirk.None ? "" : $"  quirks={Quirks}");

    private readonly record struct PendingConfig(
        IPAddress Ip, IPAddress Mask, IPAddress Gateway, IPAddress Ns1, IPAddress Ns2);
}
