using NetControl.App.Help;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Help &gt; User guide. The guide is the README compiled into the exe, so these check two things:
/// that it really is in there, and that the small Markdown reader turns it into what the help window
/// expects - sections to list, tables and lists with their text intact, and links that go somewhere.
/// </summary>
public class HelpDocumentTests
{
    [Fact]
    public void TheReadmeIsCompiledIntoTheApplication()
    {
        HelpDocument guide = HelpDocument.LoadEmbedded();

        Assert.True(guide.IsEmbedded, "README.md is not an embedded resource - check NetControl.App.csproj.");
        Assert.Contains(guide.Sections, section => section.Title == "Safety first");
        Assert.Contains(guide.Sections, section => section.Title == "Troubleshooting");
        Assert.True(guide.Sections.Count >= 10);
    }

    [Fact]
    public void TheReadmesOwnContentsListIsLeftToTheWindow()
    {
        HelpDocument guide = HelpDocument.LoadEmbedded();

        Assert.DoesNotContain(guide.Sections, section => section.Title == "Contents");
    }

    /// <summary>A #link to a heading that was renamed is a dead click in the help window and on GitHub.</summary>
    [Fact]
    public void EveryLinkWithinTheGuideLandsOnAHeading()
    {
        HelpDocument guide = HelpDocument.LoadEmbedded();

        var anchors = guide.Blocks.OfType<HelpHeading>().Select(heading => heading.Anchor).ToHashSet(StringComparer.Ordinal);

        var internalLinks = guide.Blocks
            .SelectMany(block => TextOf(block))
            .SelectMany(text => HelpDocument.ParseInline(text))
            .Select(span => span.Link)
            .OfType<string>()
            .Where(link => link.StartsWith('#'))
            .ToList();

        Assert.NotEmpty(internalLinks);
        Assert.All(internalLinks, link => Assert.Contains(link[1..], anchors));
    }

    [Theory]
    [InlineData("Safety first", "safety-first")]
    [InlineData("Watching and serving BOOTP/DHCP", "watching-and-serving-bootpdhcp")]
    [InlineData("Quick start: commissioning a panel", "quick-start-commissioning-a-panel")]
    [InlineData("Set static and Enable BOOTP/DHCP", "set-static-and-enable-bootpdhcp")]
    public void AnchorsAreTheSlugsGitHubMakes(string heading, string anchor) =>
        Assert.Equal(anchor, HelpDocument.Anchor(heading));

    [Fact]
    public void ReadsHeadingsParagraphsListsTablesAndCode()
    {
        HelpDocument guide = HelpDocument.Parse(
            "# Title\n\nOne line\nwrapped.\n\n## Part\n\n- first\n  continued\n- second\n\n1. one\n2. two\n\n"
            + "| Name | Meaning |\n|---|---|\n| **A** | the first |\n\n| | |\n|---|---|\n| x | y |\n\n```\n  indented\nnext\n```\n");

        Assert.Collection(
            guide.Blocks,
            block => Assert.Equal(new HelpHeading(1, "Title", "title"), block),
            block => Assert.Equal("One line wrapped.", Assert.IsType<HelpParagraph>(block).Text),
            block => Assert.Equal(new HelpHeading(2, "Part", "part"), block),
            block =>
            {
                HelpList list = Assert.IsType<HelpList>(block);
                Assert.False(list.Numbered);
                Assert.Equal(new[] { "first continued", "second" }, list.Items);
            },
            block =>
            {
                HelpList list = Assert.IsType<HelpList>(block);
                Assert.True(list.Numbered);
                Assert.Equal(new[] { "one", "two" }, list.Items);
            },
            block =>
            {
                HelpTable table = Assert.IsType<HelpTable>(block);
                Assert.Equal(new[] { "Name", "Meaning" }, table.Header!);
                Assert.Equal(new[] { "**A**", "the first" }, Assert.Single(table.Rows));
            },
            block =>
            {
                HelpTable table = Assert.IsType<HelpTable>(block);
                Assert.Null(table.Header);
                Assert.Equal(new[] { "x", "y" }, Assert.Single(table.Rows));
            },
            block => Assert.Equal("  indented\nnext", Assert.IsType<HelpCode>(block).Text));

        HelpSection part = Assert.Single(guide.Sections);
        Assert.Equal("Part", part.Title);
        Assert.Equal(2, guide.IntroductionEnd);
    }

    [Fact]
    public void ReadsBoldCodeAndLinksInsideText()
    {
        IReadOnlyList<HelpSpan> spans = HelpDocument.ParseInline(
            "Press **Set static**, run `netsh` or see **[the releases](https://example.test/r)** [here](#safety-first).");

        Assert.Equal(
            new[]
            {
                new HelpSpan("Press ", false, false, null),
                new HelpSpan("Set static", true, false, null),
                new HelpSpan(", run ", false, false, null),
                new HelpSpan("netsh", false, true, null),
                new HelpSpan(" or see ", false, false, null),
                new HelpSpan("the releases", true, false, "https://example.test/r"),
                new HelpSpan(" ", false, false, null),
                new HelpSpan("here", false, false, "#safety-first"),
                new HelpSpan(".", false, false, null),
            },
            spans);
    }

    [Fact]
    public void AStrayMarkerIsKeptAsText()
    {
        Assert.Equal("a ` b [c] (d)", HelpDocument.PlainText("a ` b [c] (d)"));
    }

    [Fact]
    public void SearchFindsTheSectionsThatMentionAWord()
    {
        HelpDocument guide = HelpDocument.LoadEmbedded();

        List<string> npcap = guide.Sections.Where(section => section.Matches("npcap")).Select(section => section.Title).ToList();

        Assert.Contains("PROFINET", npcap);
        Assert.Contains("Passive listening", npcap);
        Assert.DoesNotContain("Subnet calculator", npcap);
        Assert.Equal(guide.Sections.Count, guide.Sections.Count(section => section.Matches("  ")));
    }

    /// <summary>A link to a repository file must open on GitHub, never be handed to the shell as a local path.</summary>
    [Theory]
    [InlineData("https://npcap.com", "https://npcap.com")]
    [InlineData("DEPLOY.md", "https://github.com/Robbuie/NetControl/blob/main/DEPLOY.md")]
    [InlineData("LICENSE", "https://github.com/Robbuie/NetControl/blob/main/LICENSE")]
    [InlineData("C:\\Windows\\notepad.exe", "https://github.com/Robbuie/NetControl/blob/main/C:\\Windows\\notepad.exe")]
    public void LinksOutsideTheGuideOpenInABrowser(string target, string address) =>
        Assert.Equal(address, HelpDocument.ExternalAddress(target));

    private static IEnumerable<string> TextOf(HelpBlock block) => block switch
    {
        HelpHeading heading => new[] { heading.Text },
        HelpParagraph paragraph => new[] { paragraph.Text },
        HelpList list => list.Items,
        HelpTable table => (table.Header ?? Array.Empty<string>()).Concat(table.Rows.SelectMany(row => row)),
        _ => Array.Empty<string>(),
    };
}
