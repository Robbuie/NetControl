using System.Text;

namespace NetControl.OuiPacker;

/// <summary>
/// Just enough RFC 4180 to read the IEEE registry files.
///
/// A naive Split(',') looks like it works right up until it does not: the Organization Address
/// column is full of commas and is therefore quoted, and a handful of registrants have a quote
/// character in their own name, which the file escapes by doubling it. Getting that wrong shifts
/// every later column by one for exactly those rows, so a few vendors end up named after their own
/// street address. That is the kind of bug nobody finds by reading the code.
/// </summary>
internal static class CsvFields
{
    /// <summary>
    /// Splits one CSV record. Embedded newlines are not supported - the IEEE files do not use
    /// them, and supporting them would mean this could no longer work a line at a time.
    /// </summary>
    public static List<string> Split(string line)
    {
        var fields = new List<string>(4);
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');   // "" inside a quoted field is one literal quote
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
