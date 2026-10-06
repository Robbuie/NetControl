using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using NetControl.App.Diagnostics;
using NetControl.App.Help;
using DocumentList = System.Windows.Documents.List;

namespace NetControl.App.Views;

/// <summary>
/// The user guide window. <see cref="HelpDocument"/> decides what the guide says; this turns its
/// blocks into a <see cref="FlowDocument"/> drawn in the current theme's tokens, so the guide
/// switches between light and dark with the rest of the application.
/// </summary>
public partial class HelpWindow : Window
{
    /// <summary>Body text size. A step up from the chrome's, because this is reading, not scanning.</summary>
    private const double BodySize = 14;

    private readonly HelpDocument _document;

    /// <summary>Heading anchor to the paragraph that shows it, for the contents list and #links.</summary>
    private readonly Dictionary<string, Block> _anchors = new(StringComparer.Ordinal);

    public HelpWindow(HelpDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        InitializeComponent();

        _document = document;
        Viewer.Document = BuildDocument();
        SectionList.ItemsSource = document.Sections;
        VersionLine.Text = BuildInfo.Product + " " + BuildInfo.Version;

        PreviewKeyDown += OnWindowKeyDown;
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Scrolls to the section with this anchor, if there is one.</summary>
    public void ShowSection(string anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if (_anchors.TryGetValue(anchor.TrimStart('#'), out Block? block))
        {
            JumpTo(block);
        }
    }

    private FlowDocument BuildDocument()
    {
        var flow = new FlowDocument
        {
            PagePadding = new Thickness(28, 20, 28, 28),
            FontSize = BodySize,
            TextAlignment = TextAlignment.Left,
        };

        // Resource references rather than values, so a theme change while the guide is open repaints it.
        flow.SetResourceReference(FlowDocument.FontFamilyProperty, "font");
        flow.SetResourceReference(FlowDocument.ForegroundProperty, "txt-0");
        flow.SetResourceReference(FlowDocument.BackgroundProperty, "bg-2");

        AddBlocks(flow, 0, _document.IntroductionEnd);

        foreach (HelpSection section in _document.Sections)
        {
            AddBlocks(flow, section.Start, section.End);
        }

        return flow;
    }

    private void AddBlocks(FlowDocument flow, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            Block rendered = _document.Blocks[i] switch
            {
                HelpHeading heading => Heading(heading),
                HelpList list => ListOf(list),
                HelpTable table => TableOf(table),
                HelpCode code => CodeOf(code),
                HelpParagraph paragraph => Text(paragraph.Text, new Thickness(0, 0, 0, 10)),
                _ => new Paragraph(),
            };

            flow.Blocks.Add(rendered);
        }
    }

    private Paragraph Heading(HelpHeading heading)
    {
        (double size, Thickness margin) = heading.Level switch
        {
            1 => (26.0, new Thickness(0, 0, 0, 10)),
            2 => (20.0, new Thickness(0, 26, 0, 8)),
            _ => (16.0, new Thickness(0, 16, 0, 6)),
        };

        Paragraph paragraph = Text(heading.Text, margin);
        paragraph.FontSize = size;
        paragraph.FontWeight = FontWeights.SemiBold;

        _anchors[heading.Anchor] = paragraph;

        return paragraph;
    }

    private Paragraph Text(string markdown, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };

        foreach (HelpSpan span in HelpDocument.ParseInline(markdown))
        {
            paragraph.Inlines.Add(InlineOf(span));
        }

        return paragraph;
    }

    private Inline InlineOf(HelpSpan span)
    {
        Inline inline;

        if (span.Link is { } target)
        {
            var link = new Hyperlink(new Run(span.Text))
            {
                ToolTip = target.StartsWith('#') ? "Go to this section" : HelpDocument.ExternalAddress(target),
            };

            link.SetResourceReference(TextElement.ForegroundProperty, "accent");
            link.Click += (_, _) => FollowLink(target);
            inline = link;
        }
        else
        {
            var run = new Run(span.Text);

            if (span.Code)
            {
                run.SetResourceReference(TextElement.FontFamilyProperty, "mono");
                run.SetResourceReference(TextElement.BackgroundProperty, "bg-3");
            }

            inline = run;
        }

        if (span.Bold)
        {
            inline.FontWeight = FontWeights.SemiBold;
        }

        return inline;
    }

    private DocumentList ListOf(HelpList list)
    {
        var rendered = new DocumentList
        {
            MarkerStyle = list.Numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(24, 0, 0, 0),
        };

        foreach (string item in list.Items)
        {
            rendered.ListItems.Add(new ListItem(Text(item, new Thickness(0, 0, 0, 4))));
        }

        return rendered;
    }

    private Table TableOf(HelpTable table)
    {
        int columns = table.Header?.Count ?? 0;

        foreach (IReadOnlyList<string> row in table.Rows)
        {
            columns = Math.Max(columns, row.Count);
        }

        var rendered = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12) };

        // The README's tables are nearly all "name | what it means": a narrow first column and a
        // wide second. Anything wider shares the width evenly.
        for (int c = 0; c < columns; c++)
        {
            double share = columns == 2 && c == 1 ? 2.4 : 1;
            rendered.Columns.Add(new TableColumn { Width = new GridLength(share, GridUnitType.Star) });
        }

        var group = new TableRowGroup();
        rendered.RowGroups.Add(group);

        if (table.Header is { } header)
        {
            group.Rows.Add(Row(header, columns, isHeader: true));
        }

        foreach (IReadOnlyList<string> row in table.Rows)
        {
            group.Rows.Add(Row(row, columns, isHeader: false));
        }

        return rendered;
    }

    private TableRow Row(IReadOnlyList<string> cells, int columns, bool isHeader)
    {
        var row = new TableRow();

        if (isHeader)
        {
            row.SetResourceReference(TextElement.BackgroundProperty, "bg-3");
            row.FontWeight = FontWeights.SemiBold;
        }

        for (int c = 0; c < columns; c++)
        {
            string text = c < cells.Count ? cells[c] : string.Empty;

            var cell = new TableCell(Text(text, new Thickness(0)))
            {
                Padding = new Thickness(8, 5, 12, 5),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };

            cell.SetResourceReference(TableCell.BorderBrushProperty, "line-soft");
            row.Cells.Add(cell);
        }

        return row;
    }

    private static Paragraph CodeOf(HelpCode code)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = BodySize - 1,
        };

        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "mono");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "bg-3");

        string[] lines = code.Text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[i]));
        }

        return paragraph;
    }

    /// <summary>
    /// A #link moves within the guide. Anything else opens in the browser, by way of
    /// <see cref="HelpDocument.ExternalAddress"/>, which never yields a local path.
    /// </summary>
    private void FollowLink(string target)
    {
        if (target.StartsWith('#'))
        {
            ShowSection(target);
            return;
        }

        Shell.Open(this, HelpDocument.ExternalAddress(target));
    }

    /// <summary>
    /// Puts a heading at the top of the view. BringIntoView alone scrolls only as far as it must,
    /// which leaves a section below the current one sitting at the bottom edge; scrolling to the end
    /// first means the only way to bring it into view is from above, so it lands at the top.
    /// </summary>
    private void JumpTo(Block block)
    {
        if (FindScroller(Viewer) is { } scroller)
        {
            scroller.ScrollToEnd();
            scroller.UpdateLayout();
        }

        block.BringIntoView();
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            if (child is ScrollViewer scroller)
            {
                return scroller;
            }

            if (FindScroller(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnSectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is HelpSection section)
        {
            ShowSection(section.Anchor);
        }
    }

    /// <summary>
    /// A click on the section already selected does not change the selection, so it would otherwise
    /// do nothing after the reader has scrolled away from it.
    /// </summary>
    private void OnSectionClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: HelpSection section } && ReferenceEquals(section, SectionList.SelectedItem))
        {
            ShowSection(section.Anchor);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        List<HelpSection> matches = _document.Sections.Where(section => section.Matches(query)).ToList();

        SectionList.ItemsSource = matches;
        NoMatches.Text = "No section mentions '" + query + "'.";
        NoMatches.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SectionList.Items.Count > 0 && SectionList.Items[0] is HelpSection first)
        {
            SectionList.SelectedItem = first;
            ShowSection(first.Anchor);
            e.Handled = true;
        }
    }

    /// <summary>Escape clears the search first, then closes the guide.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
        }
        else
        {
            Close();
        }

        e.Handled = true;
    }
}
