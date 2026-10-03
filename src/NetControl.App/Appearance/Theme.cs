using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetControl.App.Appearance;

/// <summary>
/// Shared appearance system - themes, accent and chrome density.
///
/// <para>This is the WPF port of the design system in the Redline PDF app
/// (<c>src/css/app.css</c>) and the DWG viewer (<c>src/theme.py</c>), so all three applications
/// read as one product. <b>The catalogs below are copied value for value from theme.py</b> - same
/// token names, same hex, same labels and notes - so a change made in one app can be diffed
/// straight against the others. Only the mechanism differs, and it has to: Qt substitutes
/// <c>var(--bg-1)</c> into a text stylesheet, WPF resolves <c>{DynamicResource bg-1}</c> against a
/// resource dictionary. The tokens are the contract; the delivery is per-toolkit.</para>
///
/// <para><b>The three axes are independent, and that is deliberate.</b> A theme sets the greys.
/// The accent sets one colour. The density sets the chrome metrics. Anything that mixes them - a
/// theme that hardcodes a red, a density that also shifts a colour - means the combinations
/// multiply and half of them look wrong. There are 11 x 6 x 3 = 198 combinations here and none of
/// them needs its own block.</para>
///
/// <para><b>The accent is one channel triple, not a hex.</b> Every tint of it - the fill behind a
/// checked button, the border on an active row, the progress chunk - is derived from the triple by
/// <see cref="AccentTokens"/>. Writing a literal accent-looking colour into a XAML style re-pins
/// that one spot to whatever the default happens to be and it stops tracking the picker, which
/// reads as a picker that half works. <c>ThemeTests</c> fails on a literal colour in
/// <c>Controls.xaml</c>.</para>
///
/// <para><b>Three tokens exist here that theme.py does not have</b>, and they are marked where they
/// are defined: <c>bad</c>, the row tints, and <c>grid-row-h</c>. The viewer apps show a drawing and
/// report faults in a status line; this one is a table of device states, so it needs a red that is
/// not the accent and a set of row washes derived from the status colours. They are worth
/// back-porting to theme.py rather than being kept private here - the same table would help the
/// PDF app's comment list.</para>
/// </summary>
public static class Theme
{
    // ----------------------------------------------------------------------
    //  Catalogs
    // ----------------------------------------------------------------------

    /// <summary>
    /// <c>dark</c> is the base set and every other theme restates only the greys it changes; the
    /// accent and the density come from their own axes. It is listed in <see cref="Themes"/>
    /// anyway - the appearance dialog is built from that catalog, and a catalog that omits the
    /// default is one the UI cannot offer.
    /// </summary>
    private static readonly Dictionary<string, string> BaseTokens = new(StringComparer.Ordinal)
    {
        ["bg-0"] = "#101216",          // app backdrop
        ["bg-1"] = "#171a20",          // chrome
        ["bg-2"] = "#1d2128",          // raised chrome
        ["bg-3"] = "#252a33",          // hover
        ["bg-4"] = "#2f3540",          // active / borders-strong
        ["canvas-bg"] = "#0b0d10",     // panel behind the working area
        ["line"] = "#2a2f38",
        ["line-soft"] = "#22262d",
        ["txt-0"] = "#e7ebf2",
        ["txt-1"] = "#aab3c0",
        ["txt-2"] = "#79828f",
        ["info"] = "#4aa8ff",
        ["good"] = "#46c98b",
        ["warn"] = "#f2c14e",

        // NOT IN theme.py. The viewers report a failure in one status line and can spend the
        // accent on it; this tool paints whole rows red and amber at once, and an accent that
        // doubles as "blocked" would make an armed control and a dead device the same colour.
        // See ReadinessState: red means measured, and it will stop this working.
        ["bad"] = "#ff6b6b",

        ["sel"] = "#4aa8ff",
        ["page-ring"] = "#8c000000",
        ["radius"] = "7",
        ["radius-sm"] = "5",

        // File Manager's radius_lg: the corner of a card - a pane, a panel - sitting on the backdrop.
        ["radius-lg"] = "12",

        // The caption close button. Windows' own red, from File Manager's qss.py: it is what every
        // other window on the screen uses, so it follows neither the theme nor the accent.
        ["close-hover"] = "#c42b1c",
        ["close-press"] = "#b22a1b",
        ["font"] = "Segoe UI, Inter",
        ["mono"] = "Cascadia Mono, Consolas",
    };

    /// <summary>The eleven themes. A theme restates greys and nothing else - see the class remarks.</summary>
    public static IReadOnlyDictionary<string, AppearanceOption> Themes { get; } =
        new Dictionary<string, AppearanceOption>(StringComparer.Ordinal)
        {
            ["dark"] = new(
                "dark", "Dark (CAD pro)", "The default. Neutral greys, the table forward.",
                new Dictionary<string, string>(StringComparer.Ordinal)),

            ["light"] = new(
                "light", "Light", "For bright rooms and shared screens.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#eceef2", ["bg-1"] = "#f7f8fa", ["bg-2"] = "#ffffff",
                    ["bg-3"] = "#e8ebf0", ["bg-4"] = "#d7dbe2", ["canvas-bg"] = "#c9ced6",
                    ["line"] = "#d5d9e0", ["line-soft"] = "#e3e6eb",
                    ["txt-0"] = "#14181f", ["txt-1"] = "#4a525e", ["txt-2"] = "#7b838f",

                    // The status colours are read as text here, not only as dots, and the dark
                    // set is too pale to carry a sentence on white. These four are the values
                    // this app already shipped with before the port, which were chosen against
                    // exactly this background.
                    ["info"] = "#1667c9", ["good"] = "#1b7f3b", ["warn"] = "#b26a00",
                    ["bad"] = "#b00020",
                    ["page-ring"] = "#1e000000",
                }),

            ["paper"] = new(
                "paper", "Warm paper", "Light, off-white. Easier over a long shift.",
                // The backdrop is a desk rather than a screen, and the chrome is knocked off pure
                // white - for people who work on a bright monitor all day.
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#ece7dd", ["bg-1"] = "#f7f3ea", ["bg-2"] = "#fffdf8",
                    ["bg-3"] = "#eae4d7", ["bg-4"] = "#d9d1c0", ["canvas-bg"] = "#c8bfae",
                    ["line"] = "#d8d0c0", ["line-soft"] = "#e6e0d3",
                    ["txt-0"] = "#211d17", ["txt-1"] = "#564f43", ["txt-2"] = "#857c6c",
                    ["info"] = "#1a63b8", ["good"] = "#1f7a41", ["warn"] = "#9a5c00",
                    ["bad"] = "#a8271f",
                    ["page-ring"] = "#283c301e",
                }),

            ["blueprint"] = new(
                "blueprint", "Blueprint", "Deep blue chrome; the table is the only warm thing on screen.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#0a1220", ["bg-1"] = "#0f1b2d", ["bg-2"] = "#142339",
                    ["bg-3"] = "#1b2f4a", ["bg-4"] = "#26405f", ["canvas-bg"] = "#060d17",
                    ["line"] = "#21374f", ["line-soft"] = "#182b40",
                    ["txt-0"] = "#e2ecf8", ["txt-1"] = "#9fb4cc", ["txt-2"] = "#6d8299",
                    ["info"] = "#6cc0ff", ["sel"] = "#6cc0ff", ["bad"] = "#ff7d7d",
                    ["page-ring"] = "#99000000",
                }),

            ["contrast"] = new(
                "contrast", "High contrast", "Maximum separation - an accessibility target, not a style.",
                // Not a style. Text goes to pure white on near-black, every border is a visible
                // line rather than a hint, and the muted grey is lifted until it passes as body
                // text, because in this theme it is being read rather than skimmed.
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#000000", ["bg-1"] = "#0a0a0c", ["bg-2"] = "#141418",
                    ["bg-3"] = "#23232a", ["bg-4"] = "#3a3a45", ["canvas-bg"] = "#000000",
                    ["line"] = "#55555f", ["line-soft"] = "#3d3d46",
                    ["txt-0"] = "#ffffff", ["txt-1"] = "#e4e4ea", ["txt-2"] = "#b6b6c0",
                    ["info"] = "#7cc4ff", ["good"] = "#5ee0a0", ["warn"] = "#ffd75e",
                    ["bad"] = "#ff8f8f", ["sel"] = "#7cc4ff",
                    ["page-ring"] = "#59ffffff",
                }),

            // Six more, ported from File Manager 0.48 (app/theme/tokens.py), where the family look
            // is allowed to grow first. The greys, txt and status colours are File Manager's value
            // for value; canvas-bg, bad and page-ring do not exist there and are this app's own,
            // chosen the same way as the five above - a canvas a step past bg-0, and a red that
            // reads as a row wash and as text on that theme's bg-2.
            ["graphite"] = new(
                "graphite", "Graphite", "True black, for an OLED panel or a dim room.",
                // The steps between the greys are smaller than Dark's because on black a small
                // step already reads.
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#000000", ["bg-1"] = "#070708", ["bg-2"] = "#0e0e11",
                    ["bg-3"] = "#18181c", ["bg-4"] = "#24242a", ["canvas-bg"] = "#000000",
                    ["line"] = "#1f1f24", ["line-soft"] = "#151518",
                    ["txt-0"] = "#ededf0", ["txt-1"] = "#a6a6ae", ["txt-2"] = "#77777f",
                    ["info"] = "#5ab0ff", ["sel"] = "#5ab0ff",
                }),

            ["control"] = new(
                "control", "Control room", "Calm mid greys after the ISA-101 HMI screens; colour only where somebody is needed.",
                // The one theme drawn from this app's own world rather than a drawing office: an
                // ISA-101 high-performance HMI keeps the screen grey so that the only colour on it
                // is a fault. Which is exactly how the device table wants to be read. The status
                // colours are darker than the other themes' because they sit on a light grey.
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#b7babe", ["bg-1"] = "#c6c9cc", ["bg-2"] = "#dcdee1",
                    ["bg-3"] = "#cdd0d4", ["bg-4"] = "#b4b8be", ["canvas-bg"] = "#a9adb2",
                    ["line"] = "#a9aeb4", ["line-soft"] = "#c3c7cc",
                    ["txt-0"] = "#14181c", ["txt-1"] = "#343a41", ["txt-2"] = "#535a63",
                    ["info"] = "#2f5fa8", ["good"] = "#2b8a55", ["warn"] = "#a97a08",
                    ["bad"] = "#b0241b", ["sel"] = "#2f5fa8",
                    ["page-ring"] = "#1e000000",
                }),

            ["phosphor"] = new(
                "phosphor", "Phosphor", "Green on black. Pairs with the Field green accent.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#020604", ["bg-1"] = "#051009", ["bg-2"] = "#08150d",
                    ["bg-3"] = "#0e2216", ["bg-4"] = "#163222", ["canvas-bg"] = "#000302",
                    ["line"] = "#13301f", ["line-soft"] = "#0d2116",
                    ["txt-0"] = "#b4ffc8", ["txt-1"] = "#70dc97", ["txt-2"] = "#4c9f6b",
                    ["info"] = "#39ff88", ["good"] = "#39ff88", ["warn"] = "#e6d34a",
                    ["bad"] = "#ff6b5e", ["sel"] = "#39ff88",
                }),

            ["dusk"] = new(
                "dusk", "Dusk", "Warm dark browns, for the end of a long shift.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#14100e", ["bg-1"] = "#1b1613", ["bg-2"] = "#221c18",
                    ["bg-3"] = "#2c2420", ["bg-4"] = "#3a302a", ["canvas-bg"] = "#0e0b09",
                    ["line"] = "#322923", ["line-soft"] = "#28211c",
                    ["txt-0"] = "#f2e7db", ["txt-1"] = "#c3b4a4", ["txt-2"] = "#928372",
                    ["info"] = "#6fb2ff", ["good"] = "#5cc98b", ["bad"] = "#ff7b6b",
                    ["sel"] = "#6fb2ff",
                }),

            ["frost"] = new(
                "frost", "Frost", "Cool light, steel-blue greys: brighter than Paper, softer than Light.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#dee5ed", ["bg-1"] = "#ecf1f6", ["bg-2"] = "#f8fbfd",
                    ["bg-3"] = "#e5ecf3", ["bg-4"] = "#d1dbe6", ["canvas-bg"] = "#c9d3de",
                    ["line"] = "#cdd7e2", ["line-soft"] = "#e1e8ef",
                    ["txt-0"] = "#131f2b", ["txt-1"] = "#405066", ["txt-2"] = "#67768c",
                    ["info"] = "#3b7dd8", ["good"] = "#2c9c66", ["warn"] = "#b9800e",
                    ["bad"] = "#b3261e", ["sel"] = "#3b7dd8",
                    ["page-ring"] = "#1e000000",
                }),

            ["ink"] = new(
                "ink", "Ink", "Black on white like a printed drawing, with every border a real line.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bg-0"] = "#ebebe9", ["bg-1"] = "#f7f7f5", ["bg-2"] = "#ffffff",
                    ["bg-3"] = "#efefec", ["bg-4"] = "#d4d4d0", ["canvas-bg"] = "#d6d6d3",
                    ["line"] = "#8c8c88", ["line-soft"] = "#c6c6c2",
                    ["txt-0"] = "#0a0a0a", ["txt-1"] = "#2d2d2d", ["txt-2"] = "#5a5a5a",
                    ["info"] = "#0a58ca", ["good"] = "#1d8a4c", ["warn"] = "#a8700f",
                    ["bad"] = "#b00020", ["sel"] = "#0a58ca",
                    ["page-ring"] = "#1e000000",
                }),
        };

    /// <summary>
    /// The six accents. Channel triples, not hex, because every tint is derived with an alpha or
    /// a mix - see the class remarks.
    /// </summary>
    public static IReadOnlyDictionary<string, AccentOption> Accents { get; } =
        new Dictionary<string, AccentOption>(StringComparer.Ordinal)
        {
            ["redline"] = new("redline", "Redline red", Color.FromRgb(255, 91, 74)),
            ["amber"] = new("amber", "Amber", Color.FromRgb(242, 165, 60)),
            ["green"] = new("green", "Field green", Color.FromRgb(70, 201, 139)),
            ["cyan"] = new("cyan", "Cyan", Color.FromRgb(54, 191, 210)),
            ["blue"] = new("blue", "Drafting blue", Color.FromRgb(74, 145, 255)),
            ["violet"] = new("violet", "Violet", Color.FromRgb(154, 122, 255)),
        };

    /// <summary>
    /// One plan on a laptop wants the chrome out of the way; the same app on a 4K panel wants it
    /// legible. Both are the same handful of numbers. <c>normal</c> is the base set and restates
    /// nothing.
    /// </summary>
    private static readonly Dictionary<string, string> BaseMetrics = new(StringComparer.Ordinal)
    {
        ["ui-font"] = "13",
        ["tb-h"] = "34",
        ["row-h"] = "40",
        ["status-h"] = "26",
        ["side-w"] = "268",
        ["tbtn-h"] = "30",
        ["icon"] = "18",
        ["field-h"] = "28",
        ["small-font"] = "11",

        // NOT IN theme.py. A data row is not a toolbar row: row-h is 40px because a toolbar has
        // to be hit with a mouse, and a plan with sixty devices in it wants as many of them on
        // screen as will still read.
        ["grid-row-h"] = "26",
    };

    /// <summary>The three densities.</summary>
    public static IReadOnlyDictionary<string, AppearanceOption> Densities { get; } =
        new Dictionary<string, AppearanceOption>(StringComparer.Ordinal)
        {
            ["compact"] = new(
                "compact", "Compact", "Least chrome - more rows on a laptop.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ui-font"] = "12", ["tb-h"] = "30", ["row-h"] = "34",
                    ["status-h"] = "22", ["side-w"] = "238", ["tbtn-h"] = "26",
                    ["icon"] = "16", ["field-h"] = "24", ["small-font"] = "10",
                    ["grid-row-h"] = "22",
                }),

            ["normal"] = new(
                "normal", "Normal", "The default.",
                new Dictionary<string, string>(StringComparer.Ordinal)),

            ["large"] = new(
                "large", "Large", "Bigger targets and type for high-DPI panels.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ui-font"] = "15", ["tb-h"] = "38", ["row-h"] = "46",
                    ["status-h"] = "30", ["side-w"] = "304", ["tbtn-h"] = "34",
                    ["icon"] = "21", ["field-h"] = "32", ["small-font"] = "12",
                    ["grid-row-h"] = "30",
                }),
        };

    /// <summary>
    /// What this application asks for when nothing has been saved.
    ///
    /// <para>The DWG viewer defaults to Drafting blue and the PDF app to Redline red. This one is
    /// Cyan: the same system, one colour apart, so the three are recognisably a family without
    /// this one pretending to be a markup tool - and cyan is the only accent in the catalog that
    /// collides with none of green-ready, amber-warning and red-blocked.</para>
    /// </summary>
    public static AppearanceChoice Defaults { get; } =
        new(DefaultTheme, DefaultAccent, DefaultDensity, FollowWindows, DefaultLightTheme, DefaultTheme);

    /// <summary>The theme stays what the picker says.</summary>
    public const string FollowOff = "off";

    /// <summary>The theme follows Windows' light or dark app mode. The default for a new install.</summary>
    public const string FollowWindows = "windows";

    private const string DefaultLightTheme = "light";

    /// <summary>
    /// Which themes are light - File Manager's LIGHT_THEMES. Used to suggest a sensible pair, never
    /// to decide one: what is on screen is always a theme somebody picked.
    /// </summary>
    public static IReadOnlySet<string> LightThemes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "light", "paper", "control", "frost", "ink" };

    /// <summary>
    /// The theme to draw with - File Manager's <c>themeswitch.pick</c>. Pure: Windows' mode comes
    /// in as an argument, so the decision is tested without a registry. Unknown (null) means
    /// "keep the picker's theme".
    /// </summary>
    public static string Effective(AppearanceChoice? choice, bool? windowsLight)
    {
        AppearanceChoice c = Normalise(choice);

        if (c.Follow != FollowWindows || windowsLight is null)
        {
            return c.ThemeName!;
        }

        return windowsLight.Value ? c.LightTheme! : c.DarkTheme!;
    }

    // The three names again, as constants. AppearanceChoice holds nullable strings - it has to,
    // because it is also the shape a hand-edited JSON file deserialises into - and the normalisers
    // below need a non-null last resort that the compiler can see is non-null.
    private const string DefaultTheme = "dark";
    private const string DefaultAccent = "cyan";
    private const string DefaultDensity = "normal";

    // ----------------------------------------------------------------------
    //  Normalisers
    //
    //  Everything here takes whatever was in the settings file and hands back something the
    //  resource builder can use. Settings written by a later build, or hand-edited, or left over
    //  from a version where the option did not exist, must not be able to put the app into a
    //  state with no readable chrome. That is why nothing below trusts its input, and why
    //  Apply calls the normalisers rather than the other way round.
    // ----------------------------------------------------------------------

    private static string Pick<T>(IReadOnlyDictionary<string, T> catalog, string? value,
                                  string? fallback, string @default) =>
        value is not null && catalog.ContainsKey(value) ? value
        : fallback is not null && catalog.ContainsKey(fallback) ? fallback
        : @default;

    /// <summary>A stored theme name, or the nearest thing to it that exists.</summary>
    public static string ThemeOf(string? value, string? fallback = null) =>
        Pick(Themes, value, fallback, DefaultTheme);

    /// <summary>A stored accent name, or the nearest thing to it that exists.</summary>
    public static string AccentOf(string? value, string? fallback = null) =>
        Pick(Accents, value, fallback, DefaultAccent);

    /// <summary>A stored density name, or the nearest thing to it that exists.</summary>
    public static string DensityOf(string? value, string? fallback = null) =>
        Pick(Densities, value, fallback, DefaultDensity);

    /// <summary>The whole choice, normalised. Never returns something the catalogs do not hold.</summary>
    public static AppearanceChoice Normalise(AppearanceChoice? choice, AppearanceChoice? fallback = null)
    {
        AppearanceChoice f = fallback ?? Defaults;
        string? follow = choice?.Follow is FollowOff or FollowWindows ? choice.Follow
            : f.Follow is FollowOff or FollowWindows ? f.Follow
            : FollowOff;

        return new AppearanceChoice(
            ThemeOf(choice?.ThemeName, f.ThemeName),
            AccentOf(choice?.Accent, f.Accent),
            DensityOf(choice?.Density, f.Density),
            follow,
            ThemeOf(choice?.LightTheme, f.LightTheme ?? DefaultLightTheme),
            ThemeOf(choice?.DarkTheme, f.DarkTheme ?? DefaultTheme));
    }

    // ----------------------------------------------------------------------
    //  Colour maths
    //
    //  The CSS does this with color-mix() and rgba(); Qt does it in Python. Here it happens once
    //  and the resource dictionary only ever sees a finished colour.
    // ----------------------------------------------------------------------

    /// <summary>Parses <c>#rrggbb</c> or <c>#aarrggbb</c>. Never throws on a token we wrote.</summary>
    public static Color ColorOf(string value) =>
        (Color)ColorConverter.ConvertFromString(value)!;

    /// <summary><paramref name="weight"/> of colour <paramref name="a"/>, the remainder of
    /// <paramref name="b"/> - the equivalent of <c>color-mix(in srgb, ...)</c>.</summary>
    private static string Mix(string a, string b, double weight)
    {
        Color x = ColorOf(a);
        Color y = ColorOf(b);
        double w = Math.Clamp(weight, 0.0, 1.0);
        return Hex(
            (byte)Math.Round(x.R * w + y.R * (1 - w)),
            (byte)Math.Round(x.G * w + y.G * (1 - w)),
            (byte)Math.Round(x.B * w + y.B * (1 - w)));
    }

    private static string Hex(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}");

    /// <summary>
    /// An accent at an alpha, written the way WPF parses it. theme.py emits <c>rgba(r, g, b, a)</c>
    /// for Qt; the colour is identical, only the spelling differs.
    /// </summary>
    private static string Rgba(Color c, double alpha) =>
        string.Create(CultureInfo.InvariantCulture,
            $"#{(byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255):x2}{c.R:x2}{c.G:x2}{c.B:x2}");

    /// <summary>
    /// Every tint the styles are allowed to use, derived from the one triple.
    ///
    /// <para>Public so <c>ThemeTests</c> can assert that all eight actually move when the accent
    /// does - a tint that does not track is the bug this whole arrangement exists to prevent.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> AccentTokens(string accent)
    {
        Color c = Accents[AccentOf(accent)].Rgb;
        string hex = Hex(c.R, c.G, c.B);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accent"] = hex,
            ["accent-dim"] = Mix(hex, "#000000", 0.70),
            ["accent-text"] = Mix(hex, "#ffffff", 0.74),
            ["accent-lift"] = Mix(hex, "#ffb066", 0.62),

            // The gradient top on a primary button. The CSS pins this to a literal #ff6a58, which
            // only works while the accent is the red; derived here so the button tracks the picker
            // like everything else.
            ["accent-hi"] = Mix(hex, "#ffffff", 0.86),
            ["accent-soft"] = Rgba(c, 0.14),
            ["accent-wash"] = Rgba(c, 0.17),
            ["accent-line"] = Rgba(c, 0.45),
            ["accent-glow"] = Rgba(c, 0.80),
        };
    }

    // ----------------------------------------------------------------------
    //  Token resolution
    // ----------------------------------------------------------------------

    /// <summary>
    /// The complete resolved token set for one combination of the three axes.
    ///
    /// <para>Code that needs a colour outside the resource dictionary - a converter, a brush an
    /// adorner paints with - reads it from here rather than hardcoding one, which is what keeps
    /// those surfaces on the theme too.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Tokens(AppearanceChoice? choice = null)
    {
        // Normalise guarantees all three are catalog keys; the null-forgiving operators say so
        // to the compiler, which cannot see through the record's nullable properties.
        AppearanceChoice c = Normalise(choice);
        AppearanceOption theme = Themes[c.ThemeName!];
        AppearanceOption density = Densities[c.Density!];

        var outTokens = new Dictionary<string, string>(BaseTokens, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in theme.Tokens)
        {
            outTokens[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in BaseMetrics)
        {
            outTokens[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in density.Tokens)
        {
            outTokens[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in AccentTokens(c.Accent!))
        {
            outTokens[pair.Key] = pair.Value;
        }

        // Themes are light or dark, and a few rules need to know which - a hover lift that works
        // on #101216 is invisible on #f7f8fa. One flag beats per-theme special cases scattered
        // through the styles.
        outTokens["is-light"] = Lightness(outTokens["bg-1"]) > 127 ? "1" : "0";

        // `accent-text` is the accent lifted towards white, which reads well on the dark chrome
        // and turns into pale-on-pale the moment the chrome is white. app.css solves this with a
        // `body.theme-light` carve-out; doing it as a token instead means the styles that use it
        // never have to know which theme is running, and the warm-paper theme gets the fix free.
        outTokens["accent-on-chrome"] = outTokens["is-light"] == "1"
            ? outTokens["accent-dim"]
            : outTokens["accent-text"];

        // NOT IN theme.py. A whole row tinted by its state is this app's main signal, and a wash
        // has to be derived rather than picked: the pale pastels that worked on white are
        // invisible on #1d2128, and a per-theme table of six of them is six chances to forget one.
        // Weight is low on purpose - the row still has to read as a row, and the dot and the text
        // carry the state at full strength.
        outTokens["row-good"] = Mix(outTokens["good"], outTokens["bg-2"], 0.16);
        outTokens["row-warn"] = Mix(outTokens["warn"], outTokens["bg-2"], 0.16);
        outTokens["row-bad"] = Mix(outTokens["bad"], outTokens["bg-2"], 0.16);

        // A stranger on the network reads differently from a device you are waiting for, and the
        // two were already both amber before the port. Keeping that relationship - same hue,
        // half the strength - says "noted" where the full wash says "deal with this".
        outTokens["row-unknown"] = Mix(outTokens["warn"], outTokens["bg-2"], 0.08);

        // The notice panels sit on chrome rather than on a list, so they mix into bg-1.
        outTokens["notice-warn"] = Mix(outTokens["warn"], outTokens["bg-1"], 0.14);
        outTokens["notice-bad"] = Mix(outTokens["bad"], outTokens["bg-1"], 0.14);

        return outTokens;
    }

    private static int Lightness(string value)
    {
        Color c = ColorOf(value);

        // HSL lightness, which is what QColor.lightness() returns - the midpoint of the largest
        // and smallest channel, scaled to 0-255.
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        return (max + min) / 2;
    }

    // ----------------------------------------------------------------------
    //  Applying
    // ----------------------------------------------------------------------

    private static IReadOnlyDictionary<string, string>? _current;
    private static AppearanceChoice _currentChoice = Defaults;

    /// <summary>
    /// The token set live on screen right now.
    ///
    /// <para>Anything that paints outside the resource dictionary has to look its colours up.
    /// Asking here rather than calling <see cref="Tokens"/> with guessed arguments is what keeps
    /// those surfaces on the same theme as everything else, before the first <see cref="Apply"/>
    /// has run and after every one since.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Current => _current ??= Tokens(Defaults);

    /// <summary>What <see cref="Apply"/> was last given, normalised - including the follow setting.</summary>
    public static AppearanceChoice CurrentChoice => _currentChoice;

    /// <summary>
    /// Put one combination on the running application. Returns its tokens.
    ///
    /// <para>Safe to call again at any time - this is how the appearance dialog gives a live
    /// preview, and it is why nothing here caches a dictionary it then mutates: a second call has
    /// to be able to fully replace the first. The token dictionary is swapped at a known index in
    /// <c>MergedDictionaries</c> so that every <c>{DynamicResource}</c> in the app re-resolves;
    /// the control styles above it never move.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Apply(Application app, AppearanceChoice? choice)
    {
        ArgumentNullException.ThrowIfNull(app);

        AppearanceChoice c = Normalise(choice);

        // Following Windows: the tokens are built for the theme Windows' mode picks, while the
        // choice remembered is still the whole choice - so the next Apply, from a mode change,
        // can pick again.
        string drawn = Effective(c, c.Follow == FollowWindows ? SystemTheme.AppsUseLightTheme() : null);
        IReadOnlyDictionary<string, string> values = Tokens(c with { ThemeName = drawn });

        ThemeResources.Install(app, values);
        ReadinessBrushes.Refresh(values);

        _current = values;
        _currentChoice = c;
        return values;
    }
}
