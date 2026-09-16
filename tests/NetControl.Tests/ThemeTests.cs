using System.Text.RegularExpressions;
using System.Windows.Media;
using NetControl.App.Appearance;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The appearance system's invariants - the port of the DWG viewer's <c>tests/test_theme.py</c>.
///
/// <para>These are the rules that are easy to break by hand and impossible to notice by eye,
/// because breaking one only shows up in the fourth theme somebody tries six months from now:</para>
/// <list type="bullet">
///   <item>every combination of the three axes resolves to a complete token set;</item>
///   <item>every token a XAML file asks for exists, so nothing renders as a blank;</item>
///   <item>no literal colour is left in the styles, so nothing can silently stop tracking the
///         theme or the accent picker;</item>
///   <item>every tint of the accent actually moves when the accent does;</item>
///   <item>the normalisers never hand back junk, whatever is in the settings file.</item>
/// </list>
/// </summary>
public sealed class ThemeTests
{
    private static IEnumerable<AppearanceChoice> All =>
        from t in Theme.Themes.Keys
        from a in Theme.Accents.Keys
        from d in Theme.Densities.Keys
        select new AppearanceChoice(t, a, d);

    /// <summary>
    /// The four keys <see cref="ThemeResources"/> composes rather than copies. They are not in
    /// the token set, so a XAML file may use them and this test may not treat them as missing.
    /// </summary>
    private static readonly string[] Composed =
        ["corner", "corner-sm", "accent-fill", "chrome-fill"];

    [Fact]
    public void Every_combination_resolves()
    {
        foreach (AppearanceChoice choice in All)
        {
            IReadOnlyDictionary<string, string> tokens = Theme.Tokens(choice);

            Assert.NotEmpty(tokens);
            foreach (KeyValuePair<string, string> token in tokens)
            {
                Assert.False(string.IsNullOrWhiteSpace(token.Value),
                    $"{choice.Describe()}: --{token.Key} resolved to nothing");
            }
        }
    }

    /// <summary>
    /// 90 combinations, and every colour token has to be something WPF can actually parse. A
    /// mistyped hex is a XamlParseException at window-open time on one theme only, which is the
    /// worst possible moment to find it.
    /// </summary>
    [Fact]
    public void Every_colour_token_parses()
    {
        foreach (AppearanceChoice choice in All)
        {
            foreach (KeyValuePair<string, string> token in Theme.Tokens(choice))
            {
                if (!token.Value.StartsWith('#'))
                {
                    continue;
                }

                Color parsed = Theme.ColorOf(token.Value);
                Assert.True(parsed.A > 0 || token.Value.StartsWith("#00", StringComparison.Ordinal),
                    $"{choice.Describe()}: --{token.Key} is fully transparent");
            }
        }
    }

    /// <summary>
    /// A hardcoded colour is a spot that stops following the theme.
    ///
    /// <para>The styles are allowed exactly one literal - <c>#ffffff</c>, the tick on a checked
    /// box and the text on the accent-filled primary button, both of which have to stay white
    /// whatever the accent is because the fill is always dark enough to carry them.</para>
    /// </summary>
    [Fact]
    public void No_literal_colour_in_the_styles()
    {
        foreach (string file in XamlFiles)
        {
            IEnumerable<string> literals = Regex
                .Matches(File.ReadAllText(file), @"#[0-9a-fA-F]{3,8}\b")
                .Select(m => m.Value.ToLowerInvariant())
                .Where(v => v != "#ffffff")
                .Distinct();

            Assert.True(!literals.Any(),
                $"{Path.GetFileName(file)} has literal colours in it: {string.Join(", ", literals)}");
        }
    }

    /// <summary>
    /// Every <c>{DynamicResource}</c> in every XAML file has to name a token that exists.
    ///
    /// <para>This is the equivalent of theme.py raising on an unknown <c>var(--…)</c>. WPF does
    /// not raise: a resource it cannot find resolves to nothing at all, so the symptom is a
    /// transparent panel or an invisible label rather than an error, and only on the theme where
    /// somebody was looking.</para>
    /// </summary>
    [Fact]
    public void Every_referenced_token_exists()
    {
        // The default set is enough: a token that exists in one theme exists in all of them,
        // because a theme may only restate what the base set already holds - which is the next
        // test down.
        IReadOnlyDictionary<string, string> tokens = Theme.Tokens();

        foreach (string file in XamlFiles)
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(file), @"\{DynamicResource\s+([A-Za-z0-9._-]+)\s*\}"))
            {
                string key = match.Groups[1].Value;
                string bare = key.EndsWith(".color", StringComparison.Ordinal)
                    ? key[..^".color".Length]
                    : key;

                Assert.True(tokens.ContainsKey(bare) || Composed.Contains(key),
                    $"{Path.GetFileName(file)} asks for --{key}, which no token defines");
            }
        }
    }

    /// <summary>Change the accent and every derived tint has to move with it.</summary>
    [Fact]
    public void Every_accent_tint_tracks_the_picker()
    {
        string[] names = [.. Theme.AccentTokens("redline").Keys];
        Assert.True(names.Length >= 8);

        foreach (string name in names)
        {
            HashSet<string> seen = [.. Theme.Accents.Keys.Select(a => Theme.AccentTokens(a)[name])];
            Assert.True(seen.Count == Theme.Accents.Count, $"--{name} does not track the accent");
        }
    }

    /// <summary>
    /// <c>accent-on-chrome</c> has to contrast with <c>bg-1</c> in every theme.
    ///
    /// <para>This is the one that regressed in the PDF app: the white-lifted accent is fine on
    /// the dark chrome and unreadable on the light one.</para>
    /// </summary>
    [Fact]
    public void Accent_text_is_readable_on_the_chrome_it_sits_on()
    {
        foreach (AppearanceChoice choice in All)
        {
            IReadOnlyDictionary<string, string> tokens = Theme.Tokens(choice);

            int foreground = Lightness(tokens["accent-on-chrome"]);
            int background = Lightness(tokens["bg-1"]);

            Assert.True(Math.Abs(foreground - background) > 40,
                $"{choice.Describe()}: accent text too close to the chrome");
        }
    }

    /// <summary>
    /// Every row tint has to stay a tint. A wash strong enough to fight the text under it makes
    /// the table harder to read, not easier - and the state is already carried by the dot.
    /// </summary>
    [Fact]
    public void Row_tints_stay_close_to_the_surface_they_wash()
    {
        foreach (AppearanceChoice choice in All)
        {
            IReadOnlyDictionary<string, string> tokens = Theme.Tokens(choice);
            int surface = Lightness(tokens["bg-2"]);

            foreach (string tint in new[] { "row-good", "row-warn", "row-bad", "row-unknown" })
            {
                Assert.True(Math.Abs(Lightness(tokens[tint]) - surface) < 90,
                    $"{choice.Describe()}: --{tint} is a fill, not a wash");
            }
        }
    }

    /// <summary>Every density has to restate every metric, or a combination is left half-sized.</summary>
    [Fact]
    public void Densities_change_every_metric_they_claim_to()
    {
        IReadOnlyDictionary<string, string> normal = Theme.Densities["normal"].Tokens;
        Assert.True(normal.Count == 0, "normal is the base set and restates nothing");

        HashSet<string> expected = [.. Theme.Densities["compact"].Tokens.Keys];
        Assert.NotEmpty(expected);

        foreach (KeyValuePair<string, AppearanceOption> density in Theme.Densities)
        {
            if (density.Key == "normal")
            {
                continue;
            }

            Assert.True(expected.SetEquals(density.Value.Tokens.Keys),
                $"{density.Key} is missing a metric the other densities set");
        }
    }

    /// <summary>
    /// A theme may not carry an accent or a density - that is the whole point of keeping the
    /// three axes independent, and it is what stops 90 combinations needing 90 blocks.
    /// </summary>
    [Fact]
    public void Themes_only_restate_colours()
    {
        HashSet<string> metrics = [.. Theme.Densities["compact"].Tokens.Keys];
        HashSet<string> known = [.. Theme.Tokens(new AppearanceChoice("dark", "cyan", "normal")).Keys];

        foreach (KeyValuePair<string, AppearanceOption> theme in Theme.Themes)
        {
            foreach (string key in theme.Value.Tokens.Keys)
            {
                Assert.False(metrics.Contains(key), $"{theme.Key} sets the density metric --{key}");
                Assert.False(key.StartsWith("accent", StringComparison.Ordinal),
                    $"{theme.Key} sets the accent token --{key}");
                Assert.True(known.Contains(key), $"{theme.Key} invents --{key}");
            }
        }
    }

    /// <summary>
    /// Settings from a later build, hand-edited, or from a version where the option did not exist
    /// must not be able to produce unreadable chrome.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("DARK")]
    [InlineData("   ")]
    public void Normalisers_never_return_junk(string? junk)
    {
        Assert.Contains(Theme.ThemeOf(junk), Theme.Themes.Keys);
        Assert.Contains(Theme.AccentOf(junk), Theme.Accents.Keys);
        Assert.Contains(Theme.DensityOf(junk), Theme.Densities.Keys);

        // And a bad value must not swallow the caller's own fallback either.
        Assert.Equal("blue", Theme.AccentOf(junk, "blue"));

        AppearanceChoice normalised = Theme.Normalise(new AppearanceChoice(junk, junk, junk));
        Assert.Contains(normalised.ThemeName!, Theme.Themes.Keys);
        Assert.Contains(normalised.Accent!, Theme.Accents.Keys);
        Assert.Contains(normalised.Density!, Theme.Densities.Keys);
    }

    /// <summary>A null choice is the same as no choice, and gives this app's default.</summary>
    [Fact]
    public void Nothing_saved_gives_the_applications_default()
    {
        Assert.Equal(Theme.Defaults, Theme.Normalise(null));

        // Cyan, and it matters which: the viewer is Drafting blue and the PDF app is Redline red,
        // so a third colour is what makes the three read as a family rather than as one app.
        Assert.Equal("cyan", Theme.Defaults.Accent);
    }

    // ------------------------------------------------------------------

    /// <summary>HSL lightness, 0-255 - the same measure the Qt port uses.</summary>
    private static int Lightness(string value)
    {
        Color c = Theme.ColorOf(value);
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        return (max + min) / 2;
    }

    /// <summary>
    /// Every XAML file in the app, found from the repository root.
    ///
    /// <para>Read off disk rather than out of the assembly on purpose: the compiled BAML has
    /// already resolved what it can, and the thing being guarded here is what somebody typed.</para>
    /// </summary>
    private static IEnumerable<string> XamlFiles
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetControl.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "could not find the repository root from the test output folder");

            string[] files = Directory.GetFiles(
                Path.Combine(directory!.FullName, "src", "NetControl.App"), "*.xaml",
                SearchOption.AllDirectories);

            // obj/ holds the generated copies, and bin/ the ones from the last build; both would
            // double every failure and neither is a file anybody edits.
            return files.Where(f =>
                !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }
    }
}
