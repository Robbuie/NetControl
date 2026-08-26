using System.Buffers.Binary;
using NetControl.Core.Enip;

namespace NetControl.Core.Cip;

/// <summary>
/// What the device said back.
///
/// <para>A non-zero <see cref="GeneralStatus"/> is not an exception. The device understood the
/// request and refused it, and which refusal it was decides what happens next - "attribute not
/// settable" means stop and tell the user to look at the switches, "object state conflict" means
/// the write order was wrong. Throwing would flatten all of that into a stack trace.</para>
/// </summary>
public sealed class CipResponse
{
    private CipResponse(byte replyService, byte generalStatus, ushort[] additionalStatus, byte[] data)
    {
        ReplyService = replyService;
        GeneralStatus = generalStatus;
        AdditionalStatus = additionalStatus;
        Data = data;
    }

    /// <summary>The request's service code with the high bit set, which is what makes it a reply.</summary>
    public byte ReplyService { get; }

    public byte GeneralStatus { get; }

    /// <summary>Vendor-specific extra detail. Usually empty; worth printing when it is not.</summary>
    public IReadOnlyList<ushort> AdditionalStatus { get; }

    public byte[] Data { get; }

    public bool IsSuccess => GeneralStatus == CipGeneralStatus.Success;

    /// <summary>The status in words, with any additional status appended.</summary>
    public string StatusText => CipGeneralStatus.Describe(GeneralStatus, AdditionalStatus);

    public static CipResponse Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            throw new EnipException($"CIP reply is {bytes.Length} bytes; the header alone is 4.");
        }

        // bytes[1] is a reserved byte that must be zero. Not checked: a device putting something
        // there is not a reason to refuse to read a status we can otherwise understand.
        byte replyService = bytes[0];
        byte generalStatus = bytes[2];
        int extraWords = bytes[3];

        if (bytes.Length < 4 + (extraWords * 2))
        {
            throw new EnipException(
                $"CIP reply claims {extraWords} word(s) of additional status but is only {bytes.Length} bytes.");
        }

        var additional = new ushort[extraWords];
        for (int i = 0; i < extraWords; i++)
        {
            additional[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + (i * 2))..]);
        }

        return new CipResponse(replyService, generalStatus, additional, bytes[(4 + (extraWords * 2))..].ToArray());
    }
}
