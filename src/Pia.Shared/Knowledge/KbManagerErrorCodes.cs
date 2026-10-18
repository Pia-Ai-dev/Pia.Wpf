namespace Pia.Shared.Knowledge;

public static class KbManagerErrorCodes
{
    public const string NotAKbManager = "not_a_kb_manager";
    public const string NotFound = "not_found";
    public const string KnowledgeDisabled = "knowledge_disabled";
    public const string DocumentTooLarge = "document_too_large";
    public const string DuplicateContent = "duplicate_content";
    public const string ContentRequired = "content_required";
    public const string UnsupportedContentType = "unsupported_content_type";
    public const string TitleRequired = "title_required";
    public const string TitleTooLong = "title_too_long";
    public const string SourceUriTooLong = "source_uri_too_long";
    public const string PromptRequired = "prompt_required";
    public const string PromptTooLong = "prompt_too_long";
    public const string QuotaExceeded = "quota_exceeded";
}
