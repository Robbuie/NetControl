using System.Text.Json.Serialization;

namespace NetControl.App.Appearance;

/// <summary>
/// One combination of the three axes - the whole of what the appearance dialog decides and the
/// whole of what gets saved.
///
/// <para><see cref="ThemeName"/> rather than <c>Theme</c>: the type that resolves it is called
/// <see cref="Theme"/>, and a property that shadows a type inside its own namespace is a name
/// collision waiting to happen. The JSON name stays <c>theme</c>, because the file is meant to be
/// readable beside the other apps' settings, which all call it that.</para>
///
/// <para>Nothing here is validated on construction, deliberately. This is also the shape read
/// straight out of a JSON file that anybody may have edited, so it has to be able to hold junk -
/// and every property is nullable because a file written by a build that did not have one of these
/// axes yet must still load. <see cref="Theme.Normalise"/> is the one place junk turns back into
/// something renderable.</para>
/// </summary>
///
/// <para>The last three are File Manager 0.48's "theme changes by itself": <see cref="Follow"/> is
/// <c>off</c> or <c>windows</c>, and when it is <c>windows</c> the theme on screen is
/// <see cref="LightTheme"/> or <see cref="DarkTheme"/> according to Windows' own app mode, and
/// <see cref="ThemeName"/> is what is used if Windows cannot be asked. Same names in the JSON as
/// File Manager's settings, so the two files read alike.</para>
public sealed record AppearanceChoice(
    [property: JsonPropertyName("theme")] string? ThemeName,
    [property: JsonPropertyName("accent")] string? Accent,
    [property: JsonPropertyName("density")] string? Density,
    [property: JsonPropertyName("follow")] string? Follow = null,
    [property: JsonPropertyName("lightTheme")] string? LightTheme = null,
    [property: JsonPropertyName("darkTheme")] string? DarkTheme = null)
{
    /// <summary>
    /// For the diagnostic log. "dark / cyan / normal" answers "what was it set to when this went
    /// wrong" in one line, which is the only question a log ever asks of this record.
    /// </summary>
    public string Describe() =>
        $"{ThemeName ?? "?"} / {Accent ?? "?"} / {Density ?? "?"}"
        + (Follow == Theme.FollowWindows ? $" (following Windows: {LightTheme ?? "?"} / {DarkTheme ?? "?"})" : string.Empty);
}
