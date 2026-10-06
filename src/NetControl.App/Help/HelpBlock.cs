namespace NetControl.App.Help;

/// <summary>
/// One block of the user guide, as read out of the README's Markdown. Only the handful of shapes the
/// README actually uses exist here - headings, paragraphs, lists, tables and code - because the guide
/// is written for this viewer and a general Markdown engine would be a dependency to justify for no
/// gain. Text inside a block is still Markdown at the inline level; see
/// <see cref="HelpDocument.ParseInline"/>.
///
/// <para>No WPF types, so the tests can check what the guide contains without a window.</para>
/// </summary>
public abstract record HelpBlock;

/// <summary>A <c>#</c> heading. <paramref name="Anchor"/> is the GitHub-style slug a <c>#link</c> uses.</summary>
public sealed record HelpHeading(int Level, string Text, string Anchor) : HelpBlock;

/// <summary>Consecutive lines of text, joined with single spaces.</summary>
public sealed record HelpParagraph(string Text) : HelpBlock;

/// <summary>A bulleted (<c>-</c>) or numbered (<c>1.</c>) list. Each item is one paragraph.</summary>
public sealed record HelpList(bool Numbered, IReadOnlyList<string> Items) : HelpBlock;

/// <summary>
/// A pipe table. <paramref name="Header"/> is null when the table has none, or an empty one
/// (<c>| | |</c>), which the README uses for two-column "name - meaning" tables.
/// </summary>
public sealed record HelpTable(IReadOnlyList<string>? Header, IReadOnlyList<IReadOnlyList<string>> Rows) : HelpBlock;

/// <summary>A fenced code block, line breaks and indentation kept exactly.</summary>
public sealed record HelpCode(string Text) : HelpBlock;
