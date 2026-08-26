using System.Globalization;
using System.Text;
using NetControl.Core.Persistence;

namespace NetControl.Core.Plan;

/// <summary>
/// The plan as a CSV file: <c>Mac, Ip, Mask, Gateway, HostName, PanelRef, Role, Notes</c>.
///
/// <para><b>Vendor is deliberately not a column.</b> It is resolved from the MAC against the
/// embedded IEEE table at import and stored on the row - see <see cref="PlanImportContext"/>. A
/// vendor typed into a spreadsheet is a vendor nobody checked, and a plan that disagrees with the
/// OUI table about who made a device is a plan that starts arguments on a bench.</para>
///
/// <para>Reading stops at the shape of the file. What the cells <em>mean</em> is
/// <see cref="PlanValidation"/>'s job, in one pass over the whole file, because half a panel
/// configured is worse than none.</para>
///
/// <para>Columns are matched loosely - case, spaces and punctuation are ignored, and the obvious
/// spellings a person would type are accepted - because the point of import is to take the
/// spreadsheet somebody already has. Columns this does not recognise are ignored rather than
/// refused, for the same reason: a plan sheet also carries part numbers and PO references, and
/// refusing a file over a column the tool does not need would be pedantry. A missing
/// <c>Mac</c> column is refused, because there is no plan without it.</para>
/// </summary>
public static class PlanCsv
{
    /// <summary>The header this writes, and the order the columns come out in.</summary>
    public static IReadOnlyList<string> Header { get; } =
        ["Mac", "Ip", "Mask", "Gateway", "HostName", "PanelRef", "Role", "Notes"];

    /// <summary>
    /// Normalised header name to column. Several spellings map to one column on purpose; see the
    /// type's remarks. <c>Vendor</c> is recognised only so that a file this tool did not write can
    /// carry one and be ignored deliberately rather than by accident.
    /// </summary>
    private static readonly Dictionary<string, PlanCsvColumn> ColumnNames = new(StringComparer.Ordinal)
    {
        ["mac"] = PlanCsvColumn.Mac,
        ["macaddress"] = PlanCsvColumn.Mac,
        ["hardwareaddress"] = PlanCsvColumn.Mac,
        ["ethernetaddress"] = PlanCsvColumn.Mac,

        ["ip"] = PlanCsvColumn.Ip,
        ["ipaddress"] = PlanCsvColumn.Ip,
        ["ipv4"] = PlanCsvColumn.Ip,
        ["address"] = PlanCsvColumn.Ip,

        ["mask"] = PlanCsvColumn.Mask,
        ["subnetmask"] = PlanCsvColumn.Mask,
        ["netmask"] = PlanCsvColumn.Mask,

        ["gateway"] = PlanCsvColumn.Gateway,
        ["defaultgateway"] = PlanCsvColumn.Gateway,
        ["router"] = PlanCsvColumn.Gateway,

        ["hostname"] = PlanCsvColumn.HostName,
        ["host"] = PlanCsvColumn.HostName,

        ["panelref"] = PlanCsvColumn.PanelRef,
        ["panel"] = PlanCsvColumn.PanelRef,
        ["panelreference"] = PlanCsvColumn.PanelRef,
        ["tag"] = PlanCsvColumn.PanelRef,
        ["location"] = PlanCsvColumn.PanelRef,

        ["role"] = PlanCsvColumn.Role,
        ["description"] = PlanCsvColumn.Role,
        ["function"] = PlanCsvColumn.Role,

        ["notes"] = PlanCsvColumn.Notes,
        ["note"] = PlanCsvColumn.Notes,
        ["comment"] = PlanCsvColumn.Notes,
        ["comments"] = PlanCsvColumn.Notes,
        ["remarks"] = PlanCsvColumn.Notes,

        ["vendor"] = PlanCsvColumn.Ignored,
        ["manufacturer"] = PlanCsvColumn.Ignored,
    };

    /// <summary>The plan as CSV text, header first, ready to write to a file.</summary>
    public static string Write(IEnumerable<DeviceRecord> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Write(text, devices);
        return text.ToString();
    }

    /// <summary>The plan as CSV, header first.</summary>
    public static void Write(TextWriter writer, IEnumerable<DeviceRecord> devices)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(devices);

        CsvFile.WriteRecord(writer, [.. Header]);

        foreach (DeviceRecord device in devices)
        {
            CsvFile.WriteRecord(
                writer,
                [
                    device.Mac.IsEmpty ? string.Empty : device.Mac.ToString(),
                    device.PlannedIp?.ToString() ?? string.Empty,
                    device.PlannedMask?.ToString() ?? string.Empty,
                    device.PlannedGateway?.ToString() ?? string.Empty,
                    device.HostName ?? string.Empty,
                    device.PanelRef ?? string.Empty,
                    device.Role ?? string.Empty,
                    device.Notes ?? string.Empty,
                ]);
        }
    }

    /// <summary>Reads a plan file into rows of text. See the type's remarks for what this does not do.</summary>
    /// <exception cref="PlanCsvException">The file has no header, no MAC column, or a duplicated column.</exception>
    public static IReadOnlyList<PlanCsvRow> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Read(reader.ReadToEnd());
    }

    /// <inheritdoc cref="Read(TextReader)"/>
    public static IReadOnlyList<PlanCsvRow> Read(string? text)
    {
        IReadOnlyList<CsvRecord> records = CsvFile.Parse(text);

        if (records.Count == 0)
        {
            throw new PlanCsvException("That file is empty - there is no header row to read the columns from.")
            {
                Remediation = "A plan file starts with a header, e.g. "
                    + string.Join(",", Header) + ".",
            };
        }

        PlanCsvColumn[] columns = MapHeader(records[0]);
        var rows = new List<PlanCsvRow>(records.Count - 1);

        for (int i = 1; i < records.Count; i++)
        {
            PlanCsvRow row = ToRow(records[i], columns);
            if (!row.IsBlank)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// Which column each field of the header is. Unrecognised names become
    /// <see cref="PlanCsvColumn.Ignored"/>; a repeated recognised name is refused, because the
    /// alternative is one of the two silently winning.
    /// </summary>
    private static PlanCsvColumn[] MapHeader(CsvRecord header)
    {
        var columns = new PlanCsvColumn[header.Fields.Count];
        var seen = new Dictionary<PlanCsvColumn, string>();

        for (int i = 0; i < header.Fields.Count; i++)
        {
            string name = header.Fields[i];
            columns[i] = ColumnNames.TryGetValue(Normalise(name), out PlanCsvColumn column)
                ? column
                : PlanCsvColumn.Ignored;

            if (columns[i] == PlanCsvColumn.Ignored)
            {
                continue;
            }

            if (seen.TryGetValue(columns[i], out string? first))
            {
                throw new PlanCsvException(
                    $"The header names the {columns[i]} column twice, as '{first}' and '{name}'.")
                {
                    Remediation = "Delete or rename one of them so it is clear which column the tool should read.",
                };
            }

            seen[columns[i]] = name;
        }

        if (!seen.ContainsKey(PlanCsvColumn.Mac))
        {
            throw new PlanCsvException(
                "That file has no MAC column, so there is nothing to key the plan on. Its header reads: "
                + string.Join(", ", header.Fields.Select(f => $"'{f}'")) + ".")
            {
                Remediation = "Add a Mac column. The columns this reads are " + string.Join(", ", Header) + ".",
            };
        }

        return columns;
    }

    private static PlanCsvRow ToRow(CsvRecord record, PlanCsvColumn[] columns)
    {
        // A short record is normal: a spreadsheet drops trailing empty cells. A long one means the
        // file has more fields than the header described, and the extra ones have no column to be.
        string mac = string.Empty;
        string ip = string.Empty;
        string mask = string.Empty;
        string gateway = string.Empty;
        string hostName = string.Empty;
        string panelRef = string.Empty;
        string role = string.Empty;
        string notes = string.Empty;

        int count = Math.Min(record.Fields.Count, columns.Length);
        for (int i = 0; i < count; i++)
        {
            string value = record.Fields[i];
            switch (columns[i])
            {
                case PlanCsvColumn.Mac: mac = value.Trim(); break;
                case PlanCsvColumn.Ip: ip = value.Trim(); break;
                case PlanCsvColumn.Mask: mask = value.Trim(); break;
                case PlanCsvColumn.Gateway: gateway = value.Trim(); break;
                case PlanCsvColumn.HostName: hostName = value; break;
                case PlanCsvColumn.PanelRef: panelRef = value; break;
                case PlanCsvColumn.Role: role = value; break;
                case PlanCsvColumn.Notes: notes = value; break;
                default: break;
            }
        }

        return new PlanCsvRow
        {
            LineNumber = record.LineNumber,
            Mac = mac,
            Ip = ip,
            Mask = mask,
            Gateway = gateway,
            HostName = hostName,
            PanelRef = panelRef,
            Role = role,
            Notes = notes,
        };
    }

    /// <summary>
    /// Lower-cases a header name and drops everything that is not a letter or digit, so
    /// "MAC Address", "mac_address" and "MACADDRESS" are one name.
    /// </summary>
    private static string Normalise(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
