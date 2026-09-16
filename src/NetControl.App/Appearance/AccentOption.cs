using System.Windows.Media;

namespace NetControl.App.Appearance;

/// <summary>
/// One accent: a name and a single colour.
///
/// <para>The colour is held as a <see cref="Color"/> rather than a hex string because every tint
/// of it is derived by mixing - see <see cref="Theme.AccentTokens"/>. Six colour names in a list
/// say much less than six colours do, so the appearance dialog paints this as a swatch.</para>
/// </summary>
/// <param name="Id">The key this is stored under. Never localised, never changed.</param>
/// <param name="Label">What the picker shows.</param>
/// <param name="Rgb">The one colour every accent token is derived from.</param>
public sealed record AccentOption(string Id, string Label, Color Rgb);
