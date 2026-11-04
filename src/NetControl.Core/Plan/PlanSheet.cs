using System.Net;

namespace NetControl.Core.Plan;

/// <summary>
/// One sheet of a project IP workbook, read into the devices it plans - see
/// <see cref="PlanWorkbook"/>.
///
/// <para><b>What becomes a row.</b> Every row with an address in the IP column; and, when the
/// sheet has an IP2 column, a second row for each IP2, named <c>&lt;device&gt; (IP2)</c>. An IP2 is
/// normally a second network port - a robot's streaming port - and a second port is a second MAC, so
/// it is a second device as far as BOOTP is concerned. When the sheet has an <i>IP2 Type</i> column
/// and it says "Same port - ..." the IP2 is a second address on the <em>same</em> MAC (a fieldbus
/// slave), which BOOTP cannot give out separately: it is recorded in the main row's notes instead.</para>
///
/// <para><b>Temp before Final.</b> A Rev3 sheet has Final IP1/IP2 and Temp IP1/IP2. A Temp address
/// is only filled while the device is actually running on it, so when there is one it is the address
/// planned, and the Final address goes in the notes. After cutover the Temp cells are cleared and a
/// re-import plans the finals.</para>
///
/// <para><b>Dev Port</b> - which port of a multi-port device the row is - is added to the PanelRef,
/// so four rows named <c>SF101PCE01 RC1 3M</c> can still be told apart.</para>
///
/// <para><b>What does not.</b> A row with no address - a spare port, a patch-through - is skipped
/// and counted, not reported: it plans nothing. The same name on the same address twice is one
/// device listed twice (a switch in the switch table and again in the port map) and is merged.
/// The same address under two <em>different</em> names is left in, because that is the conflict
/// the import exists to refuse.</para>
///
/// <para>No MAC is read: the workbook comes off the drawings and has none. Every row arrives
/// awaiting its MAC (PLAN-IMPORT.md F1), which is filled in later from the request log or a scan.</para>
/// </summary>
public sealed class PlanSheet
{
    private readonly IReadOnlyList<Entry> _entries;

    private PlanSheet(string name, IReadOnlyList<Entry> entries, int skipped, int merged, int temporary, int samePort)
    {
        Name = name;
        _entries = entries;
        SkippedWithoutAddress = skipped;
        MergedRepeats = merged;
        TemporaryAddresses = temporary;
        SamePortAddresses = samePort;
        Subnets = [.. entries.Select(e => SubnetKey(e.Ip)).OfType<string>().Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The tab name, e.g. SF101.</summary>
    public string Name { get; }

    /// <summary>How many plan rows this sheet becomes, IP2 rows included.</summary>
    public int DeviceCount => _entries.Count;

    /// <summary>How many of <see cref="DeviceCount"/> are second ports.</summary>
    public int SecondPortCount => _entries.Count(e => e.IsSecondPort);

    /// <summary>Named rows with no address - spare ports and the like. Skipped, not refused.</summary>
    public int SkippedWithoutAddress { get; }

    /// <summary>The same name at the same address listed more than once, folded into one row.</summary>
    public int MergedRepeats { get; }

    /// <summary>
    /// How many rows were planned on their Temp address rather than their Final one. The final
    /// address is in the row's notes; re-import after cutover to move the plan onto it.
    /// </summary>
    public int TemporaryAddresses { get; }

    /// <summary>
    /// Second addresses on the same port as the main one ("Same port - Slave"): one MAC, so recorded
    /// in the main row's notes rather than planned as a row of their own.
    /// </summary>
    public int SamePortAddresses { get; }

    /// <summary>
    /// Each /24 the sheet's addresses fall in, as its first three octets ("192.168.1"), in the order
    /// first seen. The import asks for a mask and gateway per entry. An address that is not an
    /// address belongs to none, and is reported by validation instead.
    /// </summary>
    public IReadOnlyList<string> Subnets { get; }

    /// <summary>
    /// The sheet as plan rows, each with the mask and gateway of its subnet. The line number on
    /// each row is the sheet's own row number, so a problem points at the row to fix in Excel.
    /// </summary>
    /// <param name="subnets">Keyed as <see cref="Subnets"/> is. A subnet not in it gets no mask.</param>
    public IReadOnlyList<PlanCsvRow> ToRows(IReadOnlyDictionary<string, PlanSubnetSettings> subnets)
    {
        ArgumentNullException.ThrowIfNull(subnets);

        return [.. _entries.Select(entry =>
        {
            PlanSubnetSettings? settings = SubnetKey(entry.Ip) is { } key && subnets.TryGetValue(key, out PlanSubnetSettings? found)
                ? found
                : null;

            return new PlanCsvRow
            {
                LineNumber = entry.Row,
                Ip = entry.Ip,
                Mask = settings?.Mask.Trim() ?? string.Empty,
                Gateway = settings?.Gateway.Trim() ?? string.Empty,
                HostName = entry.Name,
                PanelRef = entry.Where,
                Notes = entry.Notes,
            };
        })];
    }

    /// <summary>"192.168.1" for 192.168.1.51; null for anything that is not four octets.</summary>
    public static string? SubnetKey(string ip) =>
        PlanValidation.TryParseIPv4(ip.Trim(), out IPAddress? address)
            ? string.Join('.', address.GetAddressBytes().Take(3))
            : null;

    internal static PlanSheet Read(XlsxSheet sheet, int nameColumn, int ipColumn, int ip2Column, int firstRow, int lastRow)
    {
        // The header row is the nearest row above the data whose name cell says what the column is.
        // Switch, Port, Notes and the Rev3 columns (Temp IP1/IP2, IP2 Type, Dev Port) are read from it
        // by name, so a sheet that lays them out differently still reads - and one that has none of
        // them still imports, with less on each row.
        Dictionary<string, int> headers = [];
        for (int row = firstRow - 1; row >= Math.Max(1, firstRow - 5); row--)
        {
            if (sheet.Cell(row, nameColumn).Length > 0 && sheet.Cell(row, ipColumn).Length > 0)
            {
                headers = PlanWorkbook.Headers(sheet, row);
                break;
            }
        }

        int switchColumn = headers.GetValueOrDefault("switch");
        int portColumn = headers.GetValueOrDefault("port");
        int notesColumn = headers.TryGetValue("notes", out int n) ? n : headers.GetValueOrDefault("note");
        int temp1Column = headers.TryGetValue("tempip1", out int t1) ? t1 : headers.GetValueOrDefault("tempip");
        int temp2Column = headers.GetValueOrDefault("tempip2");
        int ip2TypeColumn = headers.GetValueOrDefault("ip2type");
        int devPortColumn = headers.GetValueOrDefault("devport");

        List<Entry> entries = [];
        HashSet<(string Name, string Ip)> seen = [];
        int skipped = 0;
        int merged = 0;
        int temporary = 0;
        int samePort = 0;

        for (int row = Math.Max(1, firstRow); row <= lastRow; row++)
        {
            string name = sheet.Cell(row, nameColumn);
            string final1 = sheet.Cell(row, ipColumn);
            string final2 = ip2Column > 0 ? sheet.Cell(row, ip2Column) : string.Empty;
            string temp1 = sheet.Cell(row, temp1Column);
            string temp2 = sheet.Cell(row, temp2Column);
            string ip2Type = sheet.Cell(row, ip2TypeColumn);

            // While the plant addresses are not live, a device runs on its temporary 192.168 address,
            // and that is the one it has to be given - the final address goes in the notes.
            string ip = temp1.Length > 0 ? temp1 : final1;
            string ip2 = temp2.Length > 0 ? temp2 : final2;

            // "Same port - Slave" / "Same port - Other": a second address on the same cable, so the
            // same MAC. BOOTP gives a MAC one address, so it cannot be a row of its own; it is
            // recorded on the main row. A row whose only address is on its IP2 has nothing else to
            // be, so the address becomes the row's.
            bool ip2OnSamePort = PlanWorkbook.Normalise(ip2Type).StartsWith("sameport", StringComparison.Ordinal);
            if (ip2OnSamePort && ip.Length == 0)
            {
                (ip, final1, temp1) = (ip2, final2, temp2);
                (ip2, final2, temp2) = (string.Empty, string.Empty, string.Empty);
            }

            if (ip.Length == 0 && ip2.Length == 0)
            {
                if (name.Length > 0)
                {
                    skipped++;
                }

                continue;
            }

            string where = Where(sheet.Name, sheet.Cell(row, switchColumn), sheet.Cell(row, portColumn), sheet.Cell(row, devPortColumn));
            string notes = sheet.Cell(row, notesColumn);

            if (ip.Length > 0)
            {
                List<string> extra = [];
                if (temp1.Length > 0)
                {
                    temporary++;
                    extra.Add(final1.Length > 0 && final1 != temp1 ? $"Temp address - final {final1}" : "Temp address - final not assigned");
                }

                if (ip2OnSamePort && ip2.Length > 0)
                {
                    samePort++;
                    extra.Add($"Also {ip2} on the same port ({ip2Type}"
                        + (temp2.Length > 0 && final2.Length > 0 && final2 != temp2 ? $", final {final2})" : ")"));
                }

                if (seen.Add((name.ToUpperInvariant(), ip)))
                {
                    entries.Add(new Entry(row, name, ip, where, Join(notes, extra), IsSecondPort: false));
                }
                else
                {
                    merged++;
                }
            }

            if (ip2.Length > 0 && !ip2OnSamePort)
            {
                List<string> extra = [];
                if (temp2.Length > 0)
                {
                    temporary++;
                    extra.Add(final2.Length > 0 && final2 != temp2 ? $"Temp address - final {final2}" : "Temp address - final not assigned");
                }

                string second = name.Length == 0 ? "(IP2)" : $"{name} (IP2)";
                if (seen.Add((second.ToUpperInvariant(), ip2)))
                {
                    entries.Add(new Entry(row, second, ip2, where, Join(notes, extra), IsSecondPort: true));
                }
                else
                {
                    merged++;
                }
            }
        }

        return new PlanSheet(sheet.Name, entries, skipped, merged, temporary, samePort);
    }

    /// <summary>
    /// "SF101 ES1 port 9" - how somebody finds the device: the cell, the switch, the port - and, for a
    /// multi-port device, which of its ports: "SF101 Direct (Motion)".
    /// </summary>
    private static string Where(string sheet, string switchName, string port, string devPort)
    {
        string where = (switchName.Length, port.Length) switch
        {
            (0, 0) => sheet,
            (0, _) => $"{sheet} port {port}",
            (_, 0) => $"{sheet} {switchName}",
            _ => $"{sheet} {switchName} port {port}",
        };

        return devPort.Length == 0 ? where : $"{where} ({devPort})";
    }

    /// <summary>The sheet's own notes, then what the import adds, separated as the workbook does.</summary>
    private static string Join(string notes, List<string> extra) =>
        string.Join(" | ", extra.Prepend(notes).Where(part => part.Length > 0));

    private sealed record Entry(int Row, string Name, string Ip, string Where, string Notes, bool IsSecondPort);
}
