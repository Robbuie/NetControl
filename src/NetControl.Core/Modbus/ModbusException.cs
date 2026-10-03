namespace NetControl.Core.Modbus;

/// <summary>A read that never got an answer to judge: no connection, or silence.</summary>
public sealed class ModbusException : NetControlException
{
    public ModbusException(string message)
        : base(message)
    {
    }

    public ModbusException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
