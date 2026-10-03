namespace NetControl.Core.Profinet;

/// <summary>
/// How a DCP Set went. The device's own error code is kept and worded, because "set not possible:
/// in operation" and "option not supported" lead to different next steps.
/// </summary>
public sealed record DcpSetResult(bool Accepted, byte? ErrorCode, string Text)
{
    public static DcpSetResult NoAnswer(MacAddress target) =>
        new(false, null, $"{target} did not answer the Set. Nothing is known about whether it took it - read it again before trying once more.");

    public static string ErrorText(byte code) => code switch
    {
        0 => "accepted",
        1 => "option not supported",
        2 => "suboption not supported, or no data set",
        3 => "suboption not set",
        4 => "resource error - the device was busy",
        5 => "set not possible for a local reason - often a name or address locked by the controller's configuration",
        6 => "set not possible: the device is in operation with a controller",
        _ => $"error {code}",
    };
}
