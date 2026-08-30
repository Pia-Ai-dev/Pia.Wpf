using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class MarkdownExportServiceTests
{
    private static MarkdownExportService NewService(string? filesFolder)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = filesFolder });
        return new MarkdownExportService(settings, NullLogger<MarkdownExportService>.Instance);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pia-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ToHtml_RendersMarkdownBody_AndTitle()
    {
        var svc = NewService(null);

        var html = svc.ToHtml("# Heading\n\nHello **world**", "MyDoc");

        Assert.Contains("<title>MyDoc</title>", html);
        Assert.Contains("Hello", html);
        Assert.Contains("<strong>world</strong>", html);
    }

    [Fact]
    public void ToHtml_IncludesLightAndDarkThemesAndToggle()
    {
        var svc = NewService(null);

        var html = svc.ToHtml("# Title\n\nbody", "Doc");

        // Both theme token sets are present...
        Assert.Contains(":root {", html);
        Assert.Contains("html.dark {", html);
        // ...and the in-page toggle is wired up.
        Assert.Contains("id=\"theme-toggle\"", html);
        Assert.Contains("classList.toggle(\"dark\")", html);
    }

    [Fact]
    public void ToHtml_FooterLinksToPiaSite()
    {
        var svc = NewService(null);

        var html = svc.ToHtml("# Title\n\nbody", "Doc");

        Assert.Contains("href=\"https://pia-ai.de\"", html);
        Assert.Contains("Personal Intelligent Assistant", html);
    }

    [Fact]
    public void ToHtml_HtmlEncodesTitle()
    {
        var svc = NewService(null);

        var html = svc.ToHtml("body", "a<b> & c");

        Assert.Contains("<title>a&lt;b&gt; &amp; c</title>", html);
    }

    [Fact]
    public async Task ExportAsync_WritesFile_WithDerivedTitle()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var path = await svc.ExportAsync("# Derived Heading\n\nbody", title: null, fallbackTitle: "Pia answer", workingSubpath: null, ct: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            Assert.StartsWith("pia-answer-", Path.GetFileName(path));
            var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("<title>Derived Heading</title>", content);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_NoHeading_UsesFirstLineAsTitle()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var path = await svc.ExportAsync("first line here\n\nmore body", title: null, fallbackTitle: "Pia answer", workingSubpath: null, ct: TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("<title>first line here</title>", content);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_BlankMarkdown_FallsBackToFallbackTitle()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var path = await svc.ExportAsync("   \n  \n", title: null, fallbackTitle: "Pia answer", workingSubpath: null, ct: TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("<title>Pia answer</title>", content);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_WritesIntoExportsSubfolder()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var path = await svc.ExportAsync("# Heading\n\nbody", title: null, fallbackTitle: "Pia answer", workingSubpath: null, ct: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "Exports"), Path.GetDirectoryName(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_SameSecond_DoesNotOverwrite()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var first = await svc.ExportAsync("# One", null, "Pia answer", null, ct: TestContext.Current.CancellationToken);
            var second = await svc.ExportAsync("# Two", null, "Pia answer", null, ct: TestContext.Current.CancellationToken);

            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportToVaultAsync_WritesMarkdownUnderTheVaultSourcesExportsFolder()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var path = await svc.ExportToVaultAsync("# Heading\n\nbody", "My notes", "Pia answer", TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "Vault", "sources", "Exports"), Path.GetDirectoryName(path));
            Assert.Equal("My notes.md", Path.GetFileName(path));
            // Verbatim: auto-ingest reads the RAW layer and compiles it; nothing is prepended here.
            Assert.Equal("# Heading\n\nbody", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A blank files folder is the default install, and it must still resolve inside the vault.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VaultExportsFolderFor_BlankSetting_StaysInsideTheDefaultVault(string? configured)
    {
        var folder = MarkdownExportService.VaultExportsFolderFor(configured);

        Assert.Equal(
            Path.Combine(AssistantWorkspace.VaultRootFor(AssistantWorkspace.DefaultRoot), "sources", "Exports"),
            folder);
    }

    [Theory]
    [InlineData(@"..\..\escape")]
    [InlineData("../../escape")]
    [InlineData(@"C:\Windows\evil")]
    public async Task ExportToVaultAsync_ContainsTraversalNames(string typed)
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var path = await svc.ExportToVaultAsync("body", typed, "Pia answer", TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "Vault", "sources", "Exports"), Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("notes.md", "notes.md")]
    [InlineData("notes.MD", "notes.md")]
    [InlineData("report.v2", "report.v2.md")]
    [InlineData("   ", "Pia answer.md")]
    public async Task ExportToVaultAsync_NormalizesTheTypedName(string typed, string expected)
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var path = await svc.ExportToVaultAsync("body", typed, "Pia answer", TestContext.Current.CancellationToken);

            Assert.Equal(expected, Path.GetFileName(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Every file under sources/ is its own source document, so a suffixed re-export would have ingest
    /// compile the same answer twice.
    /// </summary>
    [Fact]
    public async Task ExportToVaultAsync_SameName_ReplacesTheEarlierExport()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);

            var first = await svc.ExportToVaultAsync("# One", "notes", "Pia answer", TestContext.Current.CancellationToken);
            var second = await svc.ExportToVaultAsync("# Two", "notes", "Pia answer", TestContext.Current.CancellationToken);

            Assert.Equal(first, second);
            Assert.Equal("notes.md", Path.GetFileName(second));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(second)!, "*.md"));
            Assert.Equal("# Two", await File.ReadAllTextAsync(second, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The user picked the path in a Save dialog, so it is written exactly as given.</summary>
    [Fact]
    public async Task ExportToPathAsync_WritesHtmlToTheGivenPath()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);
            var target = Path.Combine(dir, "sub", "chosen name.html");

            await svc.ExportToPathAsync("# Heading\n\nbody", target, "Pia answer", ct: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(target));
            var html = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);
            Assert.Contains("<title>Heading</title>", html);
            Assert.Contains("ai-generated", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportToPathAsync_Overwrites_BecauseTheSaveDialogAlreadyAsked()
    {
        var dir = NewTempDir();
        try
        {
            var svc = NewService(dir);
            var target = Path.Combine(dir, "answer.html");

            await svc.ExportToPathAsync("# One", target, "Pia answer", ct: TestContext.Current.CancellationToken);
            await svc.ExportToPathAsync("# Two", target, "Pia answer", ct: TestContext.Current.CancellationToken);

            Assert.Single(Directory.GetFiles(dir, "*.html"));
            Assert.Contains("Two", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("# Quarterly: review\n\nbody", "Quarterly_ review")]
    [InlineData("no heading here", "no heading here")]
    [InlineData("", "Pia answer")]
    public void SuggestFileName_DerivesASanitizedStem(string markdown, string expected)
    {
        var svc = NewService(null);

        Assert.Equal(expected, svc.SuggestFileName(markdown, "Pia answer"));
    }
}
