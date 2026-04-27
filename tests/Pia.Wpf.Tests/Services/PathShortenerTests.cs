using System;
using System.IO;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class PathShortenerTests
{
    [Fact]
    public void Shorten_UsesAppData_WhenPathIsUnderAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var input = Path.Combine(appData, "Pia", "assistant", "meetings", "transcript-x.md");

        var shortened = PathShortener.Shorten(input);

        Assert.StartsWith("%APPDATA%", shortened);
        Assert.EndsWith("transcript-x.md", shortened);
    }

    [Fact]
    public void Shorten_PrefersLongestMatch_WhenMultipleVarsApply()
    {
        // %APPDATA% lives under %USERPROFILE%; longer match wins.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var input = Path.Combine(appData, "Pia", "x.md");

        var shortened = PathShortener.Shorten(input);

        Assert.StartsWith("%APPDATA%", shortened);
        Assert.DoesNotContain("%USERPROFILE%", shortened);
    }

    [Fact]
    public void Shorten_ReturnsUnchanged_WhenNoEnvVarMatches()
    {
        var input = @"X:\unrelated\transcript.md";

        Assert.Equal(input, PathShortener.Shorten(input));
    }

    [Fact]
    public void Expand_RoundTripsAShortenedPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var input = Path.Combine(appData, "Pia", "x.md");

        var roundTripped = PathShortener.Expand(PathShortener.Shorten(input));

        Assert.Equal(input, roundTripped, ignoreCase: true);
    }

    [Fact]
    public void Expand_ReturnsUnchanged_WhenNoVarPresent()
    {
        var input = @"C:\absolute\path\file.md";

        Assert.Equal(input, PathShortener.Expand(input));
    }

    [Fact]
    public void Shorten_IsCaseInsensitive_OnWindowsPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var mixedCase = appData.ToUpperInvariant();
        var input = Path.Combine(mixedCase, "Pia", "x.md");

        var shortened = PathShortener.Shorten(input);

        Assert.StartsWith("%APPDATA%", shortened);
    }
}
