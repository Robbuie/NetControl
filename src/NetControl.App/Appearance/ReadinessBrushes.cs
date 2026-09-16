using System.Windows.Media;
using NetControl.App.Diagnostics;

namespace NetControl.App.Appearance;

/// <summary>
/// The four readiness colours, as brushes that follow the theme.
///
/// <para><b>These four objects are created once and never replaced.</b> Everything else in the app
/// gets its colours through <c>{DynamicResource}</c>, which WPF re-resolves when the token
/// dictionary is swapped - but a value converter hands back an object and is never asked again, so
/// a converter that returned a frozen brush would pin the interface bar to whatever theme was
/// running when the window opened. A <see cref="SolidColorBrush"/> is a
/// <see cref="System.Windows.Freezable"/>: leave it unfrozen, change its
/// <see cref="SolidColorBrush.Color"/>, and every element painted with it repaints. So the theme
/// moves and the bindings never have to know.</para>
///
/// <para>The mapping itself lives here rather than in four XAML triggers so that the interface bar,
/// the plan grid and the log cannot drift apart on what amber means.</para>
/// </summary>
public static class ReadinessBrushes
{
    /// <summary>Measured, and fine. The only state that shows green.</summary>
    public static SolidColorBrush Ready { get; } = new(Colors.Gray);

    /// <summary>Not measured, or not started yet. Grey. Never green.</summary>
    public static SolidColorBrush Unknown { get; } = new(Colors.Gray);

    /// <summary>Measured, and it might explain a problem later. Amber.</summary>
    public static SolidColorBrush Warning { get; } = new(Colors.Gray);

    /// <summary>Measured, and it will stop this working. Red.</summary>
    public static SolidColorBrush Blocked { get; } = new(Colors.Gray);

    /// <summary>The brush for one state. Anything unexpected is grey - never green.</summary>
    public static SolidColorBrush For(ReadinessState state) => state switch
    {
        ReadinessState.Ready => Ready,
        ReadinessState.Warning => Warning,
        ReadinessState.Blocked => Blocked,
        _ => Unknown,
    };

    /// <summary>
    /// Repaints the four from a token set. Called by <see cref="Theme.Apply"/> and by nothing
    /// else, so there is one moment at which the app's colours change.
    /// </summary>
    public static void Refresh(IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        Ready.Color = Theme.ColorOf(tokens["good"]);
        Warning.Color = Theme.ColorOf(tokens["warn"]);
        Blocked.Color = Theme.ColorOf(tokens["bad"]);

        // txt-2, the muted grey, rather than a grey of its own: "we did not measure this" should
        // read exactly as quiet as the other things on screen nobody needs to act on.
        Unknown.Color = Theme.ColorOf(tokens["txt-2"]);
    }
}
