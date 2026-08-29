using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
        var dir = Path.Combine(Path.GetTempPath(), "pia-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "pia-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "pia-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "pia-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "pia-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
}
