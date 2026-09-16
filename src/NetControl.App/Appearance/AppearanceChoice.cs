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
public sealed record AppearanceChoice(
    [property: JsonPropertyName("theme")] string? ThemeName,
    [property: JsonPropertyName("accent")] string? Accent,
    [property: JsonPropertyName("density")] string? Density)
{
    /// <summary>
    /// For the diagnostic log. "dark / cyan / normal" answers "what was it set to when this went
    /// wrong" in one line, which is the only question a log ever asks of this record.
    /// </summary>
    public string Describe() => $"{ThemeName ?? "?"} / {Accent ?? "?"} / {Density ?? "?"}";
}
