namespace NetControl.App.Help;

/// <summary>
/// One <c>##</c> section of the guide: an entry in the help window's contents list.
/// </summary>
/// <param name="Title">The heading, as plain text.</param>
/// <param name="Anchor">The slug a <c>#link</c> to this section uses.</param>
/// <param name="Start">Index of the heading in <see cref="HelpDocument.Blocks"/>.</param>
/// <param name="End">Index one past the section's last block.</param>
/// <param name="SearchText">Everything in the section, for the search box.</param>
public sealed record HelpSection(string Title, string Anchor, int Start, int End, string SearchText)
{
    /// <summary>
    /// Whether the section mentions <paramref name="query"/>, ignoring case. An empty query matches
    /// everything, so clearing the search box brings the whole list back.
    /// </summary>
    public bool Matches(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        string trimmed = query.Trim();

        return trimmed.Length == 0
            || Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || SearchText.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }
}
