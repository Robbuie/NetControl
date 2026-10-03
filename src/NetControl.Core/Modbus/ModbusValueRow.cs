using System.Globalization;

namespace NetControl.Core.Modbus;

/// <summary>
/// One address of a read, decoded every way it is commonly meant. The 32-bit columns pair this
/// register with the next one, and are blank on the last register of a read.
/// </summary>
public sealed record ModbusValueRow
{
    public required int Address { get; init; }

    /// <summary>The Modicon reference: 40001 for holding register 0.</summary>
    public required string Reference { get; init; }

    public required ushort Raw { get; init; }

    public string Unsigned => Raw.ToString(CultureInfo.InvariantCulture);

    public string Signed => ((short)Raw).ToString(CultureInfo.InvariantCulture);

    public string Hex => $"0x{Raw:X4}";

    public string Binary => $"{Convert.ToString(Raw >> 8, 2).PadLeft(8, '0')} {Convert.ToString(Raw & 0xFF, 2).PadLeft(8, '0')}";

    public string? FloatText { get; init; }

    public string? Int32Text { get; init; }

    public string? UInt32Text { get; init; }

    /// <summary>Rows for a whole result.</summary>
    public static IReadOnlyList<ModbusValueRow> From(ModbusReadResult result, ModbusWordOrder order)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rows = new List<ModbusValueRow>(result.Values.Count);
        bool bits = result.Request.IsBits;

        for (int i = 0; i < result.Values.Count; i++)
        {
            int address = result.Request.Start + i;
            string? f = null, s32 = null, u32 = null;

            if (!bits && i + 1 < result.Values.Count)
            {
                uint combined = order == ModbusWordOrder.HighFirst
                    ? ((uint)result.Values[i] << 16) | result.Values[i + 1]
                    : ((uint)result.Values[i + 1] << 16) | result.Values[i];

                float single = BitConverter.UInt32BitsToSingle(combined);
                f = float.IsFinite(single) ? single.ToString("G7", CultureInfo.InvariantCulture) : single.ToString(CultureInfo.InvariantCulture);
                s32 = ((int)combined).ToString(CultureInfo.InvariantCulture);
                u32 = combined.ToString(CultureInfo.InvariantCulture);
            }

            rows.Add(new ModbusValueRow
            {
                Address = address,
                Reference = ReferenceFor(result.Request.TablePrefix, address),
                Raw = result.Values[i],
                FloatText = f,
                Int32Text = s32,
                UInt32Text = u32,
            });
        }

        return rows;
    }

    /// <summary>
    /// 40001-style when the address fits five digits, 6-digit 400001-style when it does not - the
    /// two conventions manuals actually print.
    /// </summary>
    public static string ReferenceFor(int prefix, int address) =>
        address < 9999
            ? (prefix * 10000 + address + 1).ToString(CultureInfo.InvariantCulture)
            : (prefix * 100000 + address + 1).ToString(CultureInfo.InvariantCulture);
}
