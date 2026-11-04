using System.IO.Compression;
using System.Security;
using System.Text;

namespace NetControl.Tests;

/// <summary>
/// Writes a minimal .xlsx in memory - the parts Excel writes that <c>PlanWorkbook</c> reads, and no
/// others. Hand-built rather than generated with a spreadsheet library so the tests do not pull in
/// the dependency the reader exists to avoid, and so each test can arrange the awkward cases
/// (shared strings, numbers, missing cell references) deliberately.
/// </summary>
internal sealed class XlsxBuilder
{
    private readonly List<(string Name, string[][] Rows, bool Hidden)> _sheets = [];

    /// <summary>
    /// Adds a sheet. Each row is a list of cells from column A; null leaves a cell out. A cell
    /// written as "#9" is stored as the number 9, and one written as "$text" goes through the shared
    /// string table; anything else is an inline string.
    /// </summary>
    public XlsxBuilder Sheet(string name, params string?[][] rows) => Add(name, hidden: false, rows);

    /// <summary>Adds a sheet as <see cref="Sheet"/> does, with its tab hidden in Excel.</summary>
    public XlsxBuilder HiddenSheet(string name, params string?[][] rows) => Add(name, hidden: true, rows);

    private XlsxBuilder Add(string name, bool hidden, string?[][] rows)
    {
        _sheets.Add((name, rows.Select(r => r.Select(c => c ?? "\0").ToArray()).ToArray(), hidden));
        return this;
    }

    public MemoryStream Build()
    {
        var stream = new MemoryStream();
        List<string> shared = [];

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var workbook = new StringBuilder(
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder(
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

            for (int i = 0; i < _sheets.Count; i++)
            {
                (string name, string[][] rows, bool hidden) = _sheets[i];
                string state = hidden ? " state=\"hidden\"" : string.Empty;
                workbook.Append($"<sheet name=\"{SecurityElement.Escape(name)}\" sheetId=\"{i + 1}\"{state} r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(rows, shared));
            }

            Write(zip, "xl/workbook.xml", workbook.Append("</sheets></workbook>").ToString());
            Write(zip, "xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString());

            var strings = new StringBuilder("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            foreach (string s in shared)
            {
                strings.Append($"<si><t>{SecurityElement.Escape(s)}</t></si>");
            }

            Write(zip, "xl/sharedStrings.xml", strings.Append("</sst>").ToString());
        }

        stream.Position = 0;
        return stream;
    }

    private static string SheetXml(string[][] rows, List<string> shared)
    {
        var xml = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        for (int r = 0; r < rows.Length; r++)
        {
            xml.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                string value = rows[r][c];
                if (value == "\0")
                {
                    continue;
                }

                string reference = $"{(char)('A' + c)}{r + 1}";
                if (value.StartsWith('#'))
                {
                    xml.Append($"<c r=\"{reference}\"><v>{value[1..]}</v></c>");
                }
                else if (value.StartsWith('$'))
                {
                    shared.Add(value[1..]);
                    xml.Append($"<c r=\"{reference}\" t=\"s\"><v>{shared.Count - 1}</v></c>");
                }
                else
                {
                    xml.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value)}</t></is></c>");
                }
            }

            xml.Append("</row>");
        }

        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
