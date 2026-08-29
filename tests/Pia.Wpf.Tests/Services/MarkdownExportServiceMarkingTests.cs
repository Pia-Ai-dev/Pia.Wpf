using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The exported HTML is the artifact a reader gets without Pia, so the AI marking lives in the file itself.</summary>
public class MarkdownExportServiceMarkingTests
{
    private static MarkdownExportService Sut() =>
        new(Substitute.For<ISettingsService>(), NullLogger<MarkdownExportService>.Instance);

    [Fact]
    public void ToHtml_CarriesGeneratorAndAiGeneratedMeta_AndAVisibleFooterLine()
    {
        var html = Sut().ToHtml("# Hello\n\nbody", "Hello", "OpenAI · gpt-4o");

        Assert.Contains($"<meta name=\"generator\" content=\"{AppVersionInfo.Generator}\">", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"ai-generated\" content=\"true\">", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"ai-model\" content=\"OpenAI · gpt-4o\">", html, StringComparison.Ordinal);
        Assert.Contains($"AI-generated content · {AppVersionInfo.Generator} · OpenAI · gpt-4o", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHtml_WithoutAModel_StillMarksTheDocument_AndOmitsTheModelMeta()
    {
        var html = Sut().ToHtml("body", "Title");

        Assert.Contains("<meta name=\"ai-generated\" content=\"true\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ai-model", html, StringComparison.Ordinal);
        Assert.Contains($"AI-generated content · {AppVersionInfo.Generator}</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHtml_EncodesTheModelLabel()
    {
        var html = Sut().ToHtml("body", "Title", "<b>evil</b>");

        Assert.DoesNotContain("<b>evil</b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;evil&lt;/b&gt;", html, StringComparison.Ordinal);
    }
}
