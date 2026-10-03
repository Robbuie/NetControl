using System.Buffers.Binary;
using System.Net;
using System.Text;
using NetControl.Core;
using NetControl.Core.Capture;
using NetControl.Core.Interfaces;
using NetControl.Core.Profinet;

namespace NetControl.Tests;

/// <summary>
/// A frame channel with PROFINET devices on the far end: they answer Identify, Get and Set the way
/// the spec says, and every frame the tool sends is kept so a test can assert on the wire - which
/// device it went to, and that nothing went where it should not.
///
/// <para>Written from the spec, like DcpFrame itself, so a shared misreading would pass both. The
/// byte-level tests in DcpFrameTests pin the layout with literal bytes for that reason.</para>
/// </summary>
internal sealed class SimulatedDcpNetwork : IFrameChannel, ICaptureProvider
{
    public static readonly MacAddress Laptop = MacAddress.Parse("00:15:5D:00:00:01");

    public event EventHandler<FrameEventArgs>? FrameReceived;

    public event EventHandler<string>? Faulted;

    public List<Device> Devices { get; } = [];

    public List<byte[]> Sent { get; } = [];

    public MacAddress LocalMac => Laptop;

    public string AdapterName => "[7] Simulated";

    public bool Available { get; set; } = true;

    public CaptureAvailability Check() =>
        Available ? new CaptureAvailability(true, "Npcap (simulated)") : CaptureAvailability.NotInstalled;

    public IFrameChannel Open(NicInfo nic, string filter, bool promiscuous) =>
        Available ? this : throw new CaptureException(CaptureAvailability.NotInstalled.Headline);

    public void Dispose()
    {
        // Reopened per operation by the PROFINET tab; the same object serves every one.
    }

    public void Send(byte[] frame)
    {
        Sent.Add(frame);

        if (!EthernetFrame.TryRead(frame, out EthernetFrame header) || header.EtherType != EthernetFrame.Profinet)
        {
            return;
        }

        ReadOnlySpan<byte> dcp = frame.AsSpan(header.PayloadOffset);
        if (!DcpFrame.TryReadHeader(dcp, out DcpFrame.Header request))
        {
            return;
        }

        foreach (Device device in Devices.ToList())
        {
            bool forMe = header.Destination == device.Mac || header.Destination == DcpFrame.IdentifyMulticast;
            if (!forMe)
            {
                continue;
            }

            byte[]? answer = request.ServiceId switch
            {
                DcpFrame.ServiceIdentify when request.FrameId == DcpFrame.FrameIdIdentifyRequest
                    => device.Silent ? null : Response(device, DcpFrame.FrameIdIdentifyResponse, DcpFrame.ServiceIdentify, request.Xid, device.Blocks()),
                DcpFrame.ServiceGet => Response(device, DcpFrame.FrameIdGetSet, DcpFrame.ServiceGet, request.Xid, device.Blocks()),
                DcpFrame.ServiceSet => Response(device, DcpFrame.FrameIdGetSet, DcpFrame.ServiceSet, request.Xid, device.ApplySet(dcp, request.DataLength)),
                _ => null,
            };

            if (answer is not null)
            {
                FrameReceived?.Invoke(this, new FrameEventArgs(answer, DateTimeOffset.UtcNow));
            }
        }
    }

    /// <summary>Makes the compiler happy about an event no test raises.</summary>
    public void Fault(string why) => Faulted?.Invoke(this, why);

    private static byte[] Response(Device device, ushort frameId, byte service, uint xid, byte[] blocks)
    {
        var frame = new byte[14 + 12 + blocks.Length];
        EthernetFrame.Write(frame, Laptop, device.Mac, EthernetFrame.Profinet);
        Span<byte> dcp = frame.AsSpan(14);
        BinaryPrimitives.WriteUInt16BigEndian(dcp, frameId);
        dcp[2] = service;
        dcp[3] = DcpFrame.TypeResponseSuccess;
        BinaryPrimitives.WriteUInt32BigEndian(dcp[4..], xid);
        BinaryPrimitives.WriteUInt16BigEndian(dcp[10..], (ushort)blocks.Length);
        blocks.CopyTo(dcp[12..]);
        return frame;
    }

    internal sealed class Device
    {
        public required MacAddress Mac { get; init; }

        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = "ET200SP";

        public IPAddress Address { get; set; } = IPAddress.Any;

        public IPAddress Mask { get; set; } = IPAddress.Any;

        public IPAddress Gateway { get; set; } = IPAddress.Any;

        /// <summary>Answers a Set with this error code instead of applying it.</summary>
        public byte? RefuseWith { get; set; }

        /// <summary>Answers a Set with success and keeps the old value - the lie the readback is for.</summary>
        public bool Lies { get; set; }

        public bool Silent { get; set; }

        public int Signals { get; private set; }

        public byte[] Blocks()
        {
            var blocks = new List<byte>();
            Add(blocks, 0x02, 0x02, [0x00, 0x00, .. Encoding.ASCII.GetBytes(Name)]);
            Add(blocks, 0x02, 0x01, [0x00, 0x00, .. Encoding.ASCII.GetBytes(Type)]);
            Add(blocks, 0x02, 0x03, [0x00, 0x00, 0x00, 0x2A, 0x03, 0x13]);
            Add(blocks, 0x01, 0x02, [0x00, 0x01, .. Address.GetAddressBytes(), .. Mask.GetAddressBytes(), .. Gateway.GetAddressBytes()]);
            return [.. blocks];
        }

        public byte[] ApplySet(ReadOnlySpan<byte> dcp, int length)
        {
            ReadOnlySpan<byte> block = dcp.Slice(12, length);
            byte option = block[0];
            byte sub = block[1];
            int blockLength = BinaryPrimitives.ReadUInt16BigEndian(block[2..]);
            ReadOnlySpan<byte> data = block.Slice(6, blockLength - 2);   // past the qualifier

            byte code = RefuseWith ?? 0;

            if (code == 0 && !Lies)
            {
                switch ((option, sub))
                {
                    case (0x02, 0x02):
                        Name = Encoding.ASCII.GetString(data);
                        break;
                    case (0x01, 0x02):
                        Address = new IPAddress(data[..4]);
                        Mask = new IPAddress(data.Slice(4, 4));
                        Gateway = new IPAddress(data.Slice(8, 4));
                        break;
                    case (0x05, 0x03):
                        Signals++;
                        break;
                }
            }

            var response = new List<byte>();
            Add(response, 0x05, 0x04, [option, sub, code]);
            return [.. response];
        }

        private static void Add(List<byte> into, byte option, byte sub, byte[] data)
        {
            into.Add(option);
            into.Add(sub);
            into.Add((byte)(data.Length >> 8));
            into.Add((byte)data.Length);
            into.AddRange(data);
            if ((data.Length & 1) != 0)
            {
                into.Add(0);
            }
        }
    }
}
