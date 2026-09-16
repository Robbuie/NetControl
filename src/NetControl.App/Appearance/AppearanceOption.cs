namespace NetControl.App.Appearance;

/// <summary>
/// One entry in the theme or density catalog: what it is called, what it is for, and the tokens
/// it restates.
///
/// <para>The note is not decoration. Five themes are unguessable from their names - "Warm paper"
/// says nothing about who it is for - so the appearance dialog shows this line under the control,
/// which is the same thing the PDF app's settings modal does.</para>
/// </summary>
/// <param name="Id">The key this is stored under. Never localised, never changed.</param>
/// <param name="Label">What the picker shows.</param>
/// <param name="Note">One line saying what it is for, shown under the picker.</param>
/// <param name="Tokens">Only the tokens this option restates; everything else comes from the base set.</param>
public sealed record AppearanceOption(
    string Id,
    string Label,
    string Note,
    IReadOnlyDictionary<string, string> Tokens);
