namespace NetControl.App.Help;

/// <summary>
/// A run of text inside a block: plain, <c>**bold**</c>, <c>`code`</c> or a <c>[link](target)</c>.
/// Bold can wrap a link or code, which is why it is a flag rather than a kind.
/// </summary>
/// <param name="Text">What is shown, with the Markdown markers removed.</param>
/// <param name="Bold">Inside <c>**</c>.</param>
/// <param name="Code">Inside backticks: shown in the monospaced font.</param>
/// <param name="Link">The link target, or null when this is not a link.</param>
public sealed record HelpSpan(string Text, bool Bold, bool Code, string? Link);
