using Xunit;

namespace Pia.Tests.Integration.ArtifactProbe;

/// <summary><c>ExpectedOutcome</c> is the text the probe prints after the arrow, written by hand: computing it would re-implement the rule this corpus exists to pin.</summary>
internal sealed record DeclarationCase(string Declaration, string ExpectedOutcome);

internal static class DeclarationCorpus
{
    internal const string NotAFileReference = "not a file reference";

    internal static IReadOnlyList<DeclarationCase> Cases { get; } =
    [
        new("report.md", "NOT FOUND"),
        new("a summary saved to report.md", "report.md: NOT FOUND"),
        new("write the digest to report.md.", "report.md: NOT FOUND"),
        new("report.md and again report.md", "report.md: NOT FOUND"),
        new("update src/Api.cs and src/Api.Tests.cs", "src/Api.cs: NOT FOUND; src/Api.Tests.cs: NOT FOUND"),
        new("src/A.cs; src/B.cs; docs/readme.md", "src/A.cs: NOT FOUND; src/B.cs: NOT FOUND; docs/readme.md: NOT FOUND"),
        new("a summary of the Q3 numbers", NotAFileReference),
        new("increase revenue by 12.5", NotAFileReference),
        new("ship v1.0 of the plan", NotAFileReference),
        new("notes", NotAFileReference),
        new(".md", NotAFileReference),
        new("main.c", NotAFileReference),        // extension too short
        new("app.config", NotAFileReference),    // extension too long
        new("page.xhtml", "NOT FOUND"),          // the accepted upper bound
        new("clip.mp4", "NOT FOUND"),
        new("backup.7z", NotAFileReference),     // digit-led extension
        new("todo:Call the vendor about pricing", NotAFileReference),
        new("reminder:Follow up on Monday", NotAFileReference),
        new("todo:Email finance about Q3.xlsx", "Q3.xlsx: NOT FOUND"),
        new("../outside/secret.md", "not a resolvable path inside the assistant files folder (not probed)"),
    ];

    internal static TheoryData<string, string> Rows()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in Cases)
            data.Add(c.Declaration, c.ExpectedOutcome);
        return data;
    }
}
