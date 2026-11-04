using System.Xml;

namespace NetControl.Core.Plan;

/// <summary>
/// A project IP workbook (.xlsx) read as candidate plans - one per sheet that lists devices, of
/// which the user imports <b>one</b> (PLAN-IMPORT.md F2).
///
/// <para><b>One sheet at a time, because the cells reuse addresses.</b> SF101, SF102 and SF103 each
/// put their switch at 120.15.106.13: three separate machines, three separate plant networks, one
/// address. Imported together they are a file full of duplicates; imported one per project they
/// are each exactly right.</para>
///
/// <para><b>Which sheets.</b> When the workbook has a <i>Formula Builder</i> tab - the table that
/// already says, per sheet, whether it is included and which columns hold the name, IP and IP2 -
/// that table decides, because it is the workbook's own statement of where its devices are.
/// Without one, any visible sheet with a header row naming a device column and an IP column
/// ("IP", "IP Address" or Rev3's "Final IP1") is offered - and when some of those are port maps
/// (they name a Switch or Port column) only the port maps are, because the others are views built
/// from them: Rev3's per-subnet Net tabs and its IP List mix every cell, and importing one would
/// plan the wrong thing without a single error. Hidden tabs (Rev3's Calc) are never offered.</para>
///
/// <para>This stops at reading. Every row it produces goes through <see cref="PlanValidation"/>
/// exactly as a CSV row does: all or nothing, every problem at once.</para>
/// </summary>
public sealed class PlanWorkbook
{
    private PlanWorkbook(IReadOnlyList<PlanSheet> sheets, bool fromFormulaBuilder)
    {
        Sheets = sheets;
        FromFormulaBuilder = fromFormulaBuilder;
    }

    /// <summary>The sheets that list devices, in tab order. Never empty - a workbook with none is refused.</summary>
    public IReadOnlyList<PlanSheet> Sheets { get; }

    /// <summary>True when the sheets came from the workbook's own Formula Builder table.</summary>
    public bool FromFormulaBuilder { get; }

    /// <summary>
    /// Reads a workbook from disk. Opened shared, so a workbook that is still open in Excel reads
    /// as it was last saved rather than failing with "in use".
    /// </summary>
    /// <exception cref="PlanWorkbookException">Not a workbook, damaged, or holding no device sheet.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
    public static PlanWorkbook Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    /// <inheritdoc cref="Read(string)"/>
    public static PlanWorkbook Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        XlsxWorkbook book;
        try
        {
            book = XlsxWorkbook.Read(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new PlanWorkbookException("That file is not an Excel workbook (.xlsx).", ex)
            {
                Remediation = "Open it in Excel and save it as an Excel Workbook (.xlsx). An older .xls "
                    + "workbook is a different format; a .csv goes through the same Import.",
            };
        }
        catch (XmlException ex)
        {
            throw new PlanWorkbookException($"That workbook is damaged and could not be read: {ex.Message}", ex)
            {
                Remediation = "Open it in Excel, let Excel repair it, and save it again.",
            };
        }

        List<PlanSheet>? listed = FromBuilder(book);
        List<PlanSheet> sheets = listed ?? Detect(book);

        if (sheets.Count == 0)
        {
            throw new PlanWorkbookException(
                "No sheet in that workbook lists devices: none has a header row naming a device column "
                + "and an IP column"
                + (listed is null ? "." : ", and its Formula Builder table includes no sheet that exists."))
            {
                Remediation = "The sheet needs a header row with 'Device' (or 'Name') over the device names "
                    + "and 'IP' (or 'Final IP1') over the addresses. 'IP2' / 'Final IP2', 'Temp IP1' / 'Temp IP2', "
                    + "'IP2 Type', 'Dev Port', 'Switch', 'Port' and 'Notes' are read too.",
            };
        }

        return new PlanWorkbook(sheets, listed is not null);
    }

    /// <summary>
    /// The sheets the workbook's Formula Builder table includes. Null when there is no such table, so
    /// the caller can tell "no builder" from "a builder that includes nothing".
    /// </summary>
    private static List<PlanSheet>? FromBuilder(XlsxWorkbook book)
    {
        XlsxSheet? builder = book.Find("Formula Builder");
        if (builder is null)
        {
            return null;
        }

        for (int header = 1; header <= Math.Min(builder.LastRow, 40); header++)
        {
            Dictionary<string, int> columns = Headers(builder, header);
            if (!columns.TryGetValue("sheetname", out int sheetColumn)
                || !columns.TryGetValue("namecol", out int nameColumn)
                || !columns.TryGetValue("ipcol", out int ipColumn))
            {
                continue;
            }

            int includeColumn = columns.GetValueOrDefault("include");
            int ip2Column = columns.GetValueOrDefault("ip2col");
            int firstColumn = columns.GetValueOrDefault("firstrow");
            int lastColumn = columns.GetValueOrDefault("lastrow");

            List<PlanSheet> sheets = [];
            for (int row = header + 1; row <= builder.LastRow; row++)
            {
                string name = builder.Cell(row, sheetColumn);
                if (name.Length == 0
                    || (includeColumn > 0 && !string.Equals(builder.Cell(row, includeColumn), "yes", StringComparison.OrdinalIgnoreCase))
                    || book.Find(name) is not { } sheet
                    || sheets.Any(s => string.Equals(s.Name, sheet.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                int name1 = XlsxWorkbook.ColumnNumber(builder.Cell(row, nameColumn));
                int ip1 = XlsxWorkbook.ColumnNumber(builder.Cell(row, ipColumn));
                if (name1 == 0 || ip1 == 0)
                {
                    continue;
                }

                int ip2 = ip2Column > 0 ? XlsxWorkbook.ColumnNumber(builder.Cell(row, ip2Column)) : 0;
                int first = Number(builder.Cell(row, firstColumn)) ?? 1;
                int last = Number(builder.Cell(row, lastColumn)) ?? sheet.LastRow;

                sheets.Add(PlanSheet.Read(sheet, name1, ip1, ip2, first, Math.Min(last, sheet.LastRow)));
            }

            return sheets;
        }

        return null;
    }

    /// <summary>
    /// Every visible sheet with a recognisable device/IP header row in its first forty rows - only
    /// the port maps among them when there are any.
    /// </summary>
    private static List<PlanSheet> Detect(XlsxWorkbook book)
    {
        List<(PlanSheet Sheet, bool IsPortMap)> found = [];

        foreach (XlsxSheet sheet in book.Sheets.Where(s => !s.Hidden))
        {
            for (int header = 1; header <= Math.Min(sheet.LastRow, 40); header++)
            {
                Dictionary<string, int> columns = Headers(sheet, header);
                int name = First(columns, "device", "devicename", "name", "hostname", "tag");
                int ip = First(columns, "ip", "ipaddress", "ipv4", "finalip1", "finalip", "ip1");

                if (name == 0 || ip == 0)
                {
                    continue;
                }

                int ip2 = First(columns, "ip2", "ipaddress2", "secondip", "finalip2");
                bool isPortMap = columns.ContainsKey("switch") || columns.ContainsKey("port");
                found.Add((PlanSheet.Read(sheet, name, ip, ip2, header + 1, sheet.LastRow), isPortMap));
                break;
            }
        }

        bool anyPortMap = found.Any(f => f.IsPortMap);
        return [.. found.Where(f => f.IsPortMap || !anyPortMap).Select(f => f.Sheet)];
    }

    /// <summary>Normalised header text to column, for one row. The first column with a name wins.</summary>
    internal static Dictionary<string, int> Headers(XlsxSheet sheet, int row)
    {
        Dictionary<string, int> columns = new(StringComparer.Ordinal);
        for (int column = 1; column <= sheet.LastColumn; column++)
        {
            string name = Normalise(sheet.Cell(row, column));
            if (name.Length > 0)
            {
                columns.TryAdd(name, column);
            }
        }

        return columns;
    }

    /// <summary>"IP2 col" and "ip2_col" are one name - the same loose matching as the CSV header.</summary>
    internal static string Normalise(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    private static int First(Dictionary<string, int> columns, params string[] names)
    {
        foreach (string name in names)
        {
            if (columns.TryGetValue(name, out int column))
            {
                return column;
            }
        }

        return 0;
    }

    private static int? Number(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
        && value > 0
            ? value
            : null;
}
