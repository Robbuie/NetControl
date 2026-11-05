using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetControl.Core.Plan;

/// <summary>
/// Plan rows as the tab-separated text a spreadsheet puts on the clipboard, both ways.
///
/// <para><b>Why tabs.</b> Excel copies a block of cells as tab-separated lines and pastes the same
/// back into cells, so this is the one format that goes between the grid and the project IP
/// workbook without an import dialog in between. A field holding a tab, a line break or a quote is
/// quoted the way Excel quotes it, and read back the same way.</para>
///
/// <para><b>Reading is shape only.</b> What comes back is <see cref="PlanCsvRow"/>s - every cell
/// still text - and nothing is parsed or validated here. The rows go into the grid exactly as if
/// they had been typed, so a bad address turns red with the same sentence it would have had typed,
/// and nothing that would not store is stored.</para>
///
/// <para><b>Which column is which.</b> A first line whose every cell is a header name a plan CSV
/// would accept is a header, and the columns are matched by name. Otherwise the cells are taken in
/// the grid's own order - MAC, IP, mask, gateway, host name, panel, role, notes - except that a
/// block whose first column holds nothing but IPv4 addresses is taken to start at the IP column.
/// That is what copying the address, mask and gateway columns out of a workbook gives, and reading
/// it as MACs would only turn every row red.</para>
/// </summary>
public static class PlanClipboard
{
    /// <summary>The grid's columns, in the order they are written and, without a header, read.</summary>
    public static IReadOnlyList<string> Header { get; } =
        ["MAC", "IP", "Mask", "Gateway", "Host name", "Panel", "Role", "Notes"];

    private static readonly PlanCsvColumn[] GridOrder =
    [
        PlanCsvColumn.Mac,
        PlanCsvColumn.Ip,
        PlanCsvColumn.Mask,
        PlanCsvColumn.Gateway,
        PlanCsvColumn.HostName,
        PlanCsvColumn.PanelRef,
        PlanCsvColumn.Role,
        PlanCsvColumn.Notes,
    ];

    /// <summary>
    /// The rows as text for the clipboard: a header line, then one line per row, each field in
    /// <see cref="Header"/> order. Rows are written as they are, half-typed cells included - a copy
    /// is a copy of what is on screen.
    /// </summary>
    /// <param name="rows">Each row's eight cells, in <see cref="Header"/> order.</param>
    /// <param name="extraHeader">Read-only columns to append after the eight, e.g. Vendor and State.</param>
    public static string Write(IEnumerable<IReadOnlyList<string?>> rows, IReadOnlyList<string>? extraHeader = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var text = new StringBuilder();
        WriteLine(text, [.. Header, .. extraHeader ?? []]);

        foreach (IReadOnlyList<string?> row in rows)
        {
            WriteLine(text, row);
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a block of tab-separated text into plan rows. Blank lines are dropped. Never throws on
    /// content; an empty or whitespace-only block gives no rows.
    /// </summary>
    public static IReadOnlyList<PlanCsvRow> Read(string? text)
    {
        List<List<string>> lines = Split(text ?? string.Empty);
        lines.RemoveAll(line => line.All(string.IsNullOrWhiteSpace));

        if (lines.Count == 0)
        {
            return [];
        }

        int first = 0;
        PlanCsvColumn[] columns;

        if (TryHeader(lines[0], out PlanCsvColumn[]? named))
        {
            columns = named!;
            first = 1;
        }
        else
        {
            bool startsAtIp = lines.All(line => line.Count == 0 || IsIpv4OrBlank(line[0]))
                && lines.Any(line => line.Count > 0 && !string.IsNullOrWhiteSpace(line[0]));
            columns = startsAtIp ? GridOrder[1..] : GridOrder;
        }

        var rows = new List<PlanCsvRow>();

        for (int i = first; i < lines.Count; i++)
        {
            rows.Add(ToRow(lines[i], columns, lineNumber: i + 1));
        }

        return rows;
    }

    private static bool TryHeader(List<string> line, out PlanCsvColumn[]? columns)
    {
        columns = new PlanCsvColumn[line.Count];
        bool any = false;

        for (int i = 0; i < line.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(line[i]))
            {
                columns[i] = PlanCsvColumn.Ignored;
                continue;
            }

            // Vendor and State are what Write appends; they map to Ignored, which is still a header.
            if (string.Equals(line[i].Trim(), "State", StringComparison.OrdinalIgnoreCase))
            {
                columns[i] = PlanCsvColumn.Ignored;
                continue;
            }

            if (!PlanCsv.TryColumn(line[i], out PlanCsvColumn column))
            {
                columns = null;
                return false;
            }

            columns[i] = column;
            any |= column != PlanCsvColumn.Ignored;
        }

        if (!any)
        {
            columns = null;
        }

        return any;
    }

    private static PlanCsvRow ToRow(List<string> fields, PlanCsvColumn[] columns, int lineNumber)
    {
        string mac = string.Empty, ip = string.Empty, mask = string.Empty, gateway = string.Empty;
        string hostName = string.Empty, panelRef = string.Empty, role = string.Empty, notes = string.Empty;

        for (int i = 0; i < fields.Count && i < columns.Length; i++)
        {
            string value = fields[i];

            switch (columns[i])
            {
                case PlanCsvColumn.Mac: mac = value.Trim(); break;
                case PlanCsvColumn.Ip: ip = value.Trim(); break;
                case PlanCsvColumn.Mask: mask = value.Trim(); break;
                case PlanCsvColumn.Gateway: gateway = value.Trim(); break;
                case PlanCsvColumn.HostName: hostName = value.Trim(); break;
                case PlanCsvColumn.PanelRef: panelRef = value.Trim(); break;
                case PlanCsvColumn.Role: role = value.Trim(); break;
                case PlanCsvColumn.Notes: notes = value.Trim(); break;
            }
        }

        return new PlanCsvRow
        {
            LineNumber = lineNumber,
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

    private static bool IsIpv4OrBlank(string value) =>
        string.IsNullOrWhiteSpace(value)
        || (IPAddress.TryParse(value.Trim(), out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetwork
            && value.Trim().Count(c => c == '.') == 3);

    /// <summary>Lines of fields, honouring Excel's quoting: "a""b" is a"b, and a quoted field may span lines.</summary>
    private static List<List<string>> Split(string text)
    {
        var lines = new List<List<string>>();
        var line = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool fieldStart = true;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when fieldStart:
                    quoted = true;
                    fieldStart = false;
                    break;
                case '\t':
                    line.Add(field.ToString());
                    field.Clear();
                    fieldStart = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    line.Add(field.ToString());
                    field.Clear();
                    lines.Add(line);
                    line = [];
                    fieldStart = true;
                    break;
                default:
                    field.Append(c);
                    fieldStart = false;
                    break;
            }
        }

        if (field.Length > 0 || line.Count > 0)
        {
            line.Add(field.ToString());
            lines.Add(line);
        }

        return lines;
    }

    private static void WriteLine(StringBuilder text, IReadOnlyList<string?> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\t');
            }

            text.Append(Quote(fields[i]));
        }

        text.Append("\r\n");
    }

    private static string Quote(string? field)
    {
        string value = field ?? string.Empty;

        return value.AsSpan().IndexOfAny("\t\r\n\"") < 0
            ? value
            : string.Create(CultureInfo.InvariantCulture, $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
    }
}
