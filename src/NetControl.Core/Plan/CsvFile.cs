using System.Text;

namespace NetControl.Core.Plan;

/// <summary>
/// RFC 4180 reading and writing, hand-rolled.
///
/// <para>It is hand-rolled to keep the dependency list short - every package added here is a
/// package somebody has to justify to plant IT - and because the whole of the format that matters
/// is about a hundred lines. Quoted fields and embedded commas are not optional: a plan's
/// <c>Notes</c> column will contain both, and a parser that splits on commas turns "drive, panel 3"
/// into two columns and shifts every field after it.</para>
///
/// <para>Reading is deliberately lenient in one direction and strict in the other. A stray quote
/// inside an unquoted field is taken literally, because a spreadsheet exporting <c>3" conduit</c>
/// is a file worth importing rather than a file worth rejecting. A quote that is never closed is
/// refused, because the alternative is silently swallowing the rest of the file - the reader would
/// return a short, plausible-looking plan and nothing would say why.</para>
/// </summary>
public static class CsvFile
{
    /// <summary>
    /// Record separator on write. Fixed rather than <see cref="Environment.NewLine"/> so a plan
    /// exported on one machine is byte-identical to the same plan exported on another: the file is
    /// a commissioning record, and a diff that is all line endings is a diff nobody reads.
    /// </summary>
    public const string RecordSeparator = "\r\n";

    /// <summary>
    /// Splits CSV text into records. Blank lines are skipped rather than returned as a record of
    /// one empty field, because a trailing newline is normal and an empty row is not data.
    /// </summary>
    /// <exception cref="PlanCsvException">A quoted field is never closed.</exception>
    public static IReadOnlyList<CsvRecord> Parse(string? text)
    {
        var records = new List<CsvRecord>();

        if (string.IsNullOrEmpty(text))
        {
            return records;
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        int line = 1;
        int recordLine = 0;          // 0 = no record in progress
        int quoteOpenedOn = 0;
        bool inQuotes = false;

        // A byte order mark is what a spreadsheet leaves behind when it saves as UTF-8, and it
        // would otherwise become part of the first header name.
        int i = text[0] == '\uFEFF' ? 1 : 0;

        for (; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    bool escaped = i + 1 < text.Length && text[i + 1] == '"';
                    if (escaped)
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    if (recordLine == 0)
                    {
                        recordLine = line;
                    }

                    if (field.Length > 0)
                    {
                        // Only a quote at the start of a field opens a quoted one. A quote inside a
                        // field that has already started is content - a spreadsheet writing
                        // 3" conduit unquoted - and treating it as an opening quote would swallow
                        // the rest of the file and report it as an unterminated quote.
                        field.Append('"');
                        break;
                    }

                    inQuotes = true;
                    quoteOpenedOn = line;
                    break;

                case ',':
                    if (recordLine == 0)
                    {
                        recordLine = line;
                    }

                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                case '\n':
                    if (recordLine != 0)
                    {
                        fields.Add(field.ToString());
                        records.Add(new CsvRecord(recordLine, fields.ToArray()));
                        fields.Clear();
                        field.Clear();
                        recordLine = 0;
                    }

                    // Treat CRLF as one break. A lone CR is also a break: old Mac line endings
                    // still turn up in files that have been through enough hands.
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    line++;
                    break;

                default:
                    if (recordLine == 0)
                    {
                        recordLine = line;
                    }

                    field.Append(c);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new PlanCsvException(
                $"The quoted field starting on line {quoteOpenedOn} is never closed - the file ends inside it.")
            {
                Remediation = "A quote inside a quoted field has to be doubled (\"\"). "
                    + "Check that line for an odd number of quote characters.",
            };
        }

        if (recordLine != 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvRecord(recordLine, fields.ToArray()));
        }

        return records;
    }

    /// <summary>Writes one record, followed by <see cref="RecordSeparator"/>.</summary>
    public static void WriteRecord(TextWriter writer, IReadOnlyList<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(fields);

        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(Escape(fields[i]));
        }

        writer.Write(RecordSeparator);
    }

    /// <summary>
    /// A field as it appears in the file: quoted when it has to be, bare when it does not.
    ///
    /// <para>Leading and trailing whitespace forces quotes even though RFC 4180 does not require
    /// it, because the reader trims the address columns and this is what lets a text field say
    /// exactly what it said before.</para>
    /// </summary>
    public static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        bool needsQuotes = field.AsSpan().IndexOfAny(',', '"', '\r') >= 0
            || field.Contains('\n', StringComparison.Ordinal)
            || char.IsWhiteSpace(field[0])
            || char.IsWhiteSpace(field[^1]);

        if (!needsQuotes)
        {
            return field;
        }

        return string.Concat("\"", field.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }
}
