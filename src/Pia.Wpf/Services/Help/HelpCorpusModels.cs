namespace Pia.Services.Help;

/// <param name="Path">Corpus-relative and extension-less, e.g. <c>guides/speech</c>.</param>
public record HelpPage(string Path, string Title, string Description, string Url, string Body);

/// <param name="Reference">The handle the tool hands back to the model, e.g. <c>guides/speech#selecting-a-voice</c>.</param>
/// <param name="Heading">Empty for the text above a page's first heading.</param>
public record HelpSection(string Reference, string PagePath, string PageTitle, string Heading, string Body, string Url);

public record HelpHit(string Reference, string PageTitle, string Heading, string Snippet, string Url);

/// <param name="Label">English: the model reads it and answers in the user's language.</param>
/// <param name="Path">Localized: the user reads it and has to find those labels on screen.</param>
public record HelpSettingRow(string Area, string Label, string Value, string Path);
