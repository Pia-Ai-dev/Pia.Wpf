namespace Pia.Shared.Knowledge;

public static class KbManagerLimits
{
    public const int MaxContentBytes = 10 * 1024 * 1024;
    public const int MaxInlineContentBytes = 32 * 1024;
    public const int MaxPromptChars = 8_000;
    public const int MaxTitleChars = 500;

    public const string PlainText = "text/plain";
    public const string Markdown = "text/markdown";
}
