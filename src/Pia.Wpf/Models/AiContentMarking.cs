namespace Pia.Models;

/// <summary>
/// The machine-readable marking every file Pia writes with model output carries (AI Act Art. 50(2)):
/// who generated it and that the content is AI-generated. Same keys in YAML frontmatter and HTML meta.
/// </summary>
public static class AiContentMarking
{
    public const string GeneratorKey = "generator";
    public const string AiGeneratedKey = "aiGenerated";
    public const string AiModelKey = "aiModel";

    /// <summary>Two YAML lines, each LF-terminated, for insertion into a frontmatter block.</summary>
    public static string YamlLines() =>
        $"{GeneratorKey}: {AppVersionInfo.Generator}\n{AiGeneratedKey}: true\n";
}
