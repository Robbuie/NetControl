using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace NetControl.Core.Plan;

/// <summary>
/// The cell values of an Excel workbook (.xlsx), and nothing else.
///
/// <para><b>No package, on purpose.</b> An .xlsx file is a zip of XML parts, and both halves of that
/// are in the base library - <see cref="ZipArchive"/> and <see cref="XDocument"/>. A spreadsheet
/// library would be the second third-party dependency in the engine, to read four columns of text;
/// see CLAUDE.md on what every package costs with plant IT.</para>
///
/// <para>It reads the <em>cached</em> value of every cell - what Excel last showed - and never
/// evaluates a formula. Excel always writes that value when it saves, so a workbook somebody saved
/// reads exactly as it looked on their screen. Styles, number formats, merged cells and hidden
/// state are all ignored (hidden state is reported, not acted on): a number reads as its plain
/// invariant digits, which is what an address, a port or a row number needs.</para>
///
/// <para>The XML is read with DTD processing prohibited and no resolver, because a workbook is a
/// file somebody emailed.</para>
/// </summary>
internal sealed class XlsxWorkbook
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private XlsxWorkbook(IReadOnlyList<XlsxSheet> sheets) => Sheets = sheets;

    /// <summary>The worksheets, in the order the workbook lists its tabs.</summary>
    public IReadOnlyList<XlsxSheet> Sheets { get; }

    public XlsxSheet? Find(string name) =>
        Sheets.FirstOrDefault(sheet => string.Equals(sheet.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <exception cref="InvalidDataException">The stream is not a zip archive.</exception>
    /// <exception cref="XmlException">A part of it is not well-formed XML.</exception>
    public static XlsxWorkbook Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        XDocument workbook = Load(zip, "xl/workbook.xml")
            ?? throw new InvalidDataException("The file has no xl/workbook.xml part, so it is not an Excel workbook.");

        IReadOnlyList<string> shared = ReadSharedStrings(zip);
        Dictionary<string, string> targets = ReadRelationships(zip);

        List<XlsxSheet> sheets = [];
        foreach (XElement sheet in workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet") ?? Enumerable.Empty<XElement>())
        {
            string name = (string?)sheet.Attribute("name") ?? string.Empty;
            string? id = (string?)sheet.Attribute(OfficeRel + "id");

            if (id is null || !targets.TryGetValue(id, out string? target))
            {
                continue;
            }

            // A chart sheet or a macro sheet is listed here too, and has no sheetData - it simply
            // comes back empty, which is what it is as far as a plan is concerned.
            XDocument? part = Load(zip, PartPath(target));
            if (part is not null)
            {
                // "hidden" and "veryHidden" are both workbook plumbing - a calculation tab, a lookup
                // list - and never somebody's device list.
                string state = (string?)sheet.Attribute("state") ?? "visible";
                bool hidden = !string.Equals(state, "visible", StringComparison.OrdinalIgnoreCase);
                sheets.Add(new XlsxSheet(name, ReadCells(part, shared), hidden));
            }
        }

        return new XlsxWorkbook(sheets);
    }

    /// <summary>
    /// "AB" is column 28. Returns 0 for anything that is not one to three letters, which no real
    /// column is.
    /// </summary>
    public static int ColumnNumber(string letters)
    {
        string text = letters.Trim();
        if (text.Length is 0 or > 3)
        {
            return 0;
        }

        int column = 0;
        foreach (char c in text)
        {
            char upper = char.ToUpperInvariant(c);
            if (upper is < 'A' or > 'Z')
            {
                return 0;
            }

            column = (column * 26) + (upper - 'A' + 1);
        }

        return column;
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        ZipArchiveEntry? entry = zip.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        };

        using Stream stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    /// <summary>
    /// Relationship targets are relative to <c>xl/</c>, or absolute from the package root when they
    /// start with a slash. Both spellings are in the wild.
    /// </summary>
    private static string PartPath(string target) =>
        target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

    private static Dictionary<string, string> ReadRelationships(ZipArchive zip)
    {
        Dictionary<string, string> targets = new(StringComparer.Ordinal);
        XDocument? rels = Load(zip, "xl/_rels/workbook.xml.rels");

        foreach (XElement rel in rels?.Root?.Elements(PackageRel + "Relationship") ?? Enumerable.Empty<XElement>())
        {
            if ((string?)rel.Attribute("Id") is { } id && (string?)rel.Attribute("Target") is { } target)
            {
                targets[id] = target;
            }
        }

        return targets;
    }

    /// <summary>
    /// The shared string table. Rich text is split into runs, each with its own <c>t</c>; phonetic
    /// guides (<c>rPh</c>) carry a <c>t</c> too and are not part of the text, so they are left out.
    /// </summary>
    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        XDocument? table = Load(zip, "xl/sharedStrings.xml");
        if (table?.Root is null)
        {
            return [];
        }

        return [.. table.Root.Elements(Main + "si").Select(Text)];
    }

    private static string Text(XElement container) =>
        string.Concat(container
            .Descendants(Main + "t")
            .Where(t => t.Parent?.Name != Main + "rPh")
            .Select(t => t.Value));

    private static Dictionary<(int Row, int Column), string> ReadCells(XDocument part, IReadOnlyList<string> shared)
    {
        Dictionary<(int Row, int Column), string> cells = [];
        XElement? data = part.Root?.Element(Main + "sheetData");
        if (data is null)
        {
            return cells;
        }

        // Both the row and the cell reference are optional in the format. Where one is missing the
        // position is the one after the previous, which is what Excel itself assumes.
        int rowNumber = 0;
        foreach (XElement row in data.Elements(Main + "row"))
        {
            rowNumber = int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out int r)
                ? r
                : rowNumber + 1;

            int column = 0;
            foreach (XElement cell in row.Elements(Main + "c"))
            {
                column = ColumnOf((string?)cell.Attribute("r")) is var parsed and > 0 ? parsed : column + 1;

                string value = Value(cell, shared);
                if (value.Length > 0)
                {
                    cells[(rowNumber, column)] = value;
                }
            }
        }

        return cells;
    }

    /// <summary>The column of a cell reference such as "D12", or 0 when there is none.</summary>
    private static int ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return 0;
        }

        int letters = 0;
        while (letters < reference.Length && char.IsAsciiLetter(reference[letters]))
        {
            letters++;
        }

        return letters == 0 ? 0 : ColumnNumber(reference[..letters]);
    }

    private static string Value(XElement cell, IReadOnlyList<string> shared)
    {
        string type = (string?)cell.Attribute("t") ?? "n";
        string? raw = (string?)cell.Element(Main + "v");

        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && index >= 0 && index < shared.Count
                    ? shared[index]
                    : string.Empty;

            case "inlineStr":
                return cell.Element(Main + "is") is { } inline ? Text(inline) : string.Empty;

            case "b":
                return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : string.Empty;

            case "str":
            case "e":
                return raw ?? string.Empty;

            default:
                return Number(raw);
        }
    }

    /// <summary>
    /// A numeric cell as plain digits: 9, not 9.0 and not 9E+00. Anything that is not a number the
    /// invariant culture can read is returned as written, so it reaches validation and is reported
    /// rather than vanishing.
    /// </summary>
    private static string Number(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return raw;
        }

        return Math.Abs(number) < 1e15 && number == Math.Floor(number)
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
    }
}

/// <summary>One worksheet's cell values, by one-based row and column.</summary>
internal sealed class XlsxSheet
{
    private readonly Dictionary<(int Row, int Column), string> _cells;

    public XlsxSheet(string name, Dictionary<(int Row, int Column), string> cells, bool hidden = false)
    {
        Name = name;
        Hidden = hidden;
        _cells = cells;
        LastRow = cells.Count == 0 ? 0 : cells.Keys.Max(key => key.Row);
        LastColumn = cells.Count == 0 ? 0 : cells.Keys.Max(key => key.Column);
    }

    public string Name { get; }

    /// <summary>True when Excel hides the tab (hidden or very hidden).</summary>
    public bool Hidden { get; }

    /// <summary>The last row holding anything. 0 for an empty sheet.</summary>
    public int LastRow { get; }

    public int LastColumn { get; }

    /// <summary>The cell's value, trimmed; empty when the cell is empty or absent.</summary>
    public string Cell(int row, int column) =>
        column > 0 && _cells.TryGetValue((row, column), out string? value) ? value.Trim() : string.Empty;
}
