namespace NetControl.Core.Reachability;

/// <summary>A TCP port worth asking about, and what it means when something answers on it.</summary>
/// <param name="Port">The TCP port.</param>
/// <param name="Name">What it is called on a plant floor - "Modbus/TCP", not "mbap".</param>
/// <param name="Meaning">One line on what an open port here tells somebody.</param>
public sealed record ServicePort(int Port, string Name, string Meaning)
{
    /// <summary>
    /// The default list. Short on purpose: these are the services that tell a controls engineer what
    /// a box is and how to get into it, and every one is connected to and closed again with nothing
    /// sent. It is not a port scan and must not grow into one.
    /// </summary>
    public static IReadOnlyList<ServicePort> Industrial { get; } =
    [
        new(21, "FTP", "file transfer - firmware or recipe uploads on some devices"),
        new(22, "SSH", "a managed switch, a gateway or an industrial PC"),
        new(23, "Telnet", "an older managed switch or gateway console - unencrypted"),
        new(80, "HTTP", "the device's own web page - usually diagnostics"),
        new(102, "ISO-TSAP (S7)", "a Siemens S7 controller or something that speaks to one"),
        new(443, "HTTPS", "the device's own web page, encrypted"),
        new(502, "Modbus/TCP", "Modbus registers - drives, meters, gateways"),
        new(4840, "OPC UA", "an OPC UA server"),
        new(5007, "MELSEC MC", "a Mitsubishi Ethernet module's MELSOFT / MC protocol port"),
        new(18245, "GE SRTP", "a GE / Emerson PACSystems controller"),
        new(20000, "DNP3", "a utility-style RTU or protection relay"),
        new(44818, "EtherNet/IP", "CIP explicit messaging - what Set static and the diagnostics read use"),
    ];

    public override string ToString() => $"{Port} {Name}";
}
