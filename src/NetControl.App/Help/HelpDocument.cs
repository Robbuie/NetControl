// UseWPF drops System.IO from the implicit usings; the README is read from a resource stream.
using System.IO;
using System.Text;
using NetControl.App.Diagnostics;

namespace NetControl.App.Help;

/// <summary>
/// The user guide: the repository's README.md, compiled into the exe as a resource and read into
/// blocks for <c>HelpWindow</c> to show.
///
/// <para><b>One source, two readers.</b> The README is what GitHub shows on the repository page and
/// what Help &gt; User guide shows on a plant laptop with no route out. Keeping them one file is the
/// only way they never disagree: editing the README updates the in-app help at the next build.</para>
///
/// <para>The parser understands the Markdown the README is written in and nothing more: headings,
/// paragraphs, <c>-</c> and <c>1.</c> lists (one level), pipe tables, fenced code, and inline
/// <c>**bold**</c>, <c>`code`</c> and <c>[links](target)</c>. Anything else comes through as plain
/// text, which is the right failure for a help page - readable, never blank.</para>
/// </summary>
public sealed class HelpDocument
{
    /// <summary>The manifest resource name. Set by <c>LogicalName</c> in NetControl.App.csproj.</summary>
    public const string ResourceName = "NetControl.README.md";

    /// <summary>
    /// The README's own table of contents. The help window has its contents list down the side, so
    /// this section is left out of <see cref="Sections"/> and is not shown twice.
    /// </summary>
    private const string ContentsTitle = "Contents";

    /// <summary>Shown when the resource is missing - a build that lost it, never a release.</summary>
    private const string MissingText =
        "# NetControl\n\nThe user guide was not built into this copy of NetControl. It is the README on the "
        + "[repository page](https://github.com/" + BuildInfo.RepositoryOwner + "/" + BuildInfo.RepositoryName + ").";

    private HelpDocument(IReadOnlyList<HelpBlock> blocks, IReadOnlyList<HelpSection> sections, int introductionEnd,
        bool isEmbedded)
    {
        Blocks = blocks;
        Sections = sections;
        IntroductionEnd = introductionEnd;
        IsEmbedded = isEmbedded;
    }

    /// <summary>Every block, in order.</summary>
    public IReadOnlyList<HelpBlock> Blocks { get; }

    /// <summary>The <c>##</c> sections, in order, without the README's own contents list.</summary>
    public IReadOnlyList<HelpSection> Sections { get; }

    /// <summary>Index one past the last block before the first <c>##</c> heading: the title and the opening.</summary>
    public int IntroductionEnd { get; }

    /// <summary>True when this came out of the exe's resource rather than from <see cref="Parse"/> or the fallback.</summary>
    public bool IsEmbedded { get; }

    /// <summary>The guide compiled into this build, or a one-paragraph note if it is missing.</summary>
    public static HelpDocument LoadEmbedded()
    {
        using Stream? stream = typeof(HelpDocument).Assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            return Build(MissingText, isEmbedded: false);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);

        return Build(reader.ReadToEnd(), isEmbedded: true);
    }

    /// <summary>Reads a Markdown string. For the tests, and for anything that is not the README.</summary>
    public static HelpDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return Build(markdown, isEmbedded: false);
    }

    /// <summary>
    /// The address a link opens in a browser. A web or mail link is used as written. Anything else
    /// is a path inside the repository - <c>DEPLOY.md</c>, <c>LICENSE</c> - and is opened on GitHub,
    /// so a link in the guide can never be handed to the shell as a local file to run.
    /// </summary>
    public static string ExternalAddress(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        return "https://github.com/" + BuildInfo.RepositoryOwner + "/" + BuildInfo.RepositoryName + "/blob/main/"
            + target.TrimStart('/');
    }

    /// <summary>The GitHub-style slug for a heading: lower case, spaces to hyphens, punctuation dropped.</summary>
    public static string Anchor(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);

        var slug = new StringBuilder(heading.Length);

        foreach (char c in PlainText(heading).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    /// <summary>The text with every inline marker removed, as a person would read it.</summary>
    public static string PlainText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return string.Concat(ParseInline(markdown).Select(span => span.Text));
    }

    /// <summary>
    /// Splits one block's text into runs. An unmatched marker - a lone backtick, a bracket that is
    /// not a link - is kept as the character it is, rather than swallowing the rest of the line.
    /// </summary>
    public static IReadOnlyList<HelpSpan> ParseInline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var spans = new List<HelpSpan>();
        var run = new StringBuilder();
        bool bold = false;

        void Flush()
        {
            if (run.Length > 0)
            {
                spans.Add(new HelpSpan(run.ToString(), bold, Code: false, Link: null));
                run.Clear();
            }
        }

        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                Flush();
                bold = !bold;
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);

                if (end > i + 1)
                {
                    Flush();
                    spans.Add(new HelpSpan(text[(i + 1)..end], bold, Code: true, Link: null));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                int middle = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                int close = middle < 0 ? -1 : text.IndexOf(')', middle + 2);

                if (middle > i + 1 && close > middle + 2)
                {
                    Flush();
                    string label = text[(i + 1)..middle]
                        .Replace("**", string.Empty, StringComparison.Ordinal)
                        .Replace("`", string.Empty, StringComparison.Ordinal);
                    spans.Add(new HelpSpan(label, bold, Code: false, Link: text[(middle + 2)..close]));
                    i = close + 1;
                    continue;
                }
            }

            run.Append(c);
            i++;
        }

        Flush();

        return spans;
    }

    private static HelpDocument Build(string markdown, bool isEmbedded)
    {
        List<HelpBlock> blocks = ReadBlocks(markdown);
        var sections = new List<HelpSection>();
        int introductionEnd = blocks.Count;

        for (int b = 0; b < blocks.Count; b++)
        {
            if (blocks[b] is not HelpHeading { Level: 2 } heading)
            {
                continue;
            }

            introductionEnd = Math.Min(introductionEnd, b);

            int end = b + 1;

            while (end < blocks.Count && blocks[end] is not HelpHeading { Level: <= 2 })
            {
                end++;
            }

            string title = PlainText(heading.Text);

            if (!string.Equals(title, ContentsTitle, StringComparison.OrdinalIgnoreCase))
            {
                sections.Add(new HelpSection(title, heading.Anchor, b, end, SearchText(blocks, b, end)));
            }
        }

        return new HelpDocument(blocks, sections, introductionEnd, isEmbedded);
    }

    private static List<HelpBlock> ReadBlocks(string markdown)
    {
        var blocks = new List<HelpBlock>();
        var paragraph = new StringBuilder();
        List<string>? items = null;
        bool numbered = false;
        List<string>? tableLines = null;
        StringBuilder? code = null;

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new HelpParagraph(paragraph.ToString()));
                paragraph.Clear();
            }
        }

        void FlushList()
        {
            if (items is { Count: > 0 })
            {
                blocks.Add(new HelpList(numbered, items));
            }

            items = null;
        }

        void FlushTable()
        {
            if (tableLines is not null)
            {
                blocks.Add(ReadTable(tableLines));
            }

            tableLines = null;
        }

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();

            if (code is not null)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(new HelpCode(code.ToString().TrimEnd('\n')));
                    code = null;
                }
                else
                {
                    code.Append(line).Append('\n');
                }

                continue;
            }

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                FlushTable();
                code = new StringBuilder();
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                FlushTable();
                continue;
            }

            if (trimmed[0] == '|')
            {
                FlushParagraph();
                FlushList();
                tableLines ??= new List<string>();
                tableLines.Add(trimmed);
                continue;
            }

            FlushTable();

            if (TryReadHeading(trimmed, out int level, out string headingText))
            {
                FlushParagraph();
                FlushList();
                blocks.Add(new HelpHeading(level, headingText, Anchor(headingText)));
                continue;
            }

            if (TryReadListItem(trimmed, out bool isNumbered, out string itemText))
            {
                FlushParagraph();

                if (items is not null && numbered != isNumbered)
                {
                    FlushList();
                }

                items ??= new List<string>();
                numbered = isNumbered;
                items.Add(itemText);
                continue;
            }

            // An indented line straight after a list item is the item wrapping onto the next line.
            if (items is { Count: > 0 } && line.Length > trimmed.Length)
            {
                items[^1] = items[^1] + " " + trimmed;
                continue;
            }

            FlushList();

            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(trimmed);
        }

        if (code is not null)
        {
            blocks.Add(new HelpCode(code.ToString().TrimEnd('\n')));
        }

        FlushParagraph();
        FlushList();
        FlushTable();

        return blocks;
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        level = 0;

        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is >= 1 and <= 6 && level < line.Length && line[level] == ' ')
        {
            text = line[(level + 1)..].Trim();
            return true;
        }

        level = 0;
        text = string.Empty;
        return false;
    }

    private static bool TryReadListItem(string line, out bool numbered, out string text)
    {
        if (line.Length > 2 && (line[0] is '-' or '*') && line[1] == ' ')
        {
            numbered = false;
            text = line[2..].Trim();
            return true;
        }

        int digits = 0;

        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 2 < line.Length && line[digits] == '.' && line[digits + 1] == ' ')
        {
            numbered = true;
            text = line[(digits + 2)..].Trim();
            return true;
        }

        numbered = false;
        text = string.Empty;
        return false;
    }

    private static HelpTable ReadTable(List<string> lines)
    {
        var rows = new List<IReadOnlyList<string>>();
        int rowsBeforeDivider = -1;

        foreach (string line in lines)
        {
            string[] cells = SplitRow(line);

            if (IsDivider(cells))
            {
                if (rowsBeforeDivider < 0)
                {
                    rowsBeforeDivider = rows.Count;
                }

                continue;
            }

            rows.Add(cells);
        }

        IReadOnlyList<string>? header = null;

        if (rowsBeforeDivider == 1)
        {
            header = rows[0];
            rows.RemoveAt(0);

            if (header.All(string.IsNullOrWhiteSpace))
            {
                header = null;
            }
        }

        return new HelpTable(header, rows);
    }

    private static string[] SplitRow(string line)
    {
        string body = line.Trim();

        if (body.StartsWith('|'))
        {
            body = body[1..];
        }

        if (body.EndsWith('|'))
        {
            body = body[..^1];
        }

        return body.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    private static bool IsDivider(string[] cells) =>
        cells.Length > 0 && cells.All(cell => cell.Length > 0 && cell.All(c => c is '-' or ':' or ' '));

    private static string SearchText(List<HelpBlock> blocks, int start, int end)
    {
        var text = new StringBuilder();

        for (int b = start; b < end; b++)
        {
            string piece = blocks[b] switch
            {
                HelpHeading heading => heading.Text,
                HelpParagraph paragraph => paragraph.Text,
                HelpList list => string.Join(' ', list.Items),
                HelpTable table => string.Join(' ', (table.Header ?? Array.Empty<string>()).Concat(table.Rows.SelectMany(row => row))),
                HelpCode code => code.Text,
                _ => string.Empty,
            };

            text.Append(PlainText(piece)).Append(' ');
        }

        return text.ToString();
    }
}
