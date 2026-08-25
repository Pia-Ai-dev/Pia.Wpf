using System.IO;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>The tour dump serialises every UIA Name onto the clipboard, so a Release build must not contain it.</summary>
public class TourDumpDebugOnlyRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    [Fact]
    public void TheTourDumpIsCompiledOutOfRelease()
    {
        var path = Path.Combine(SourceDirectory, "ViewModels", "MainWindowViewModel.cs");
        Assert.True(File.Exists(path), $"view model not found: {path}");
        var lines = File.ReadAllLines(path);

        var method = OnlyLineContaining(lines, "DumpTourTargetsAsync");
        var clipboard = OnlyLineContaining(lines, "_clipboardService.SetText(");
        Assert.True(method < clipboard, "the clipboard write must sit inside the dump method");

        var open = LastDirectiveBefore(lines, method);
        Assert.True(open >= 0, "the dump is not preceded by any preprocessor directive, so it ships in Release");
        Assert.Equal("#if DEBUG", lines[open].Trim());

        var close = FirstDirectiveAfter(lines, clipboard);
        Assert.True(close >= 0, "the guard around the dump is never closed");
        Assert.Equal("#endif", lines[close].Trim());

        for (var i = open + 1; i < close; i++)
        {
            Assert.False(
                IsDirective(lines[i]),
                $"line {i + 1} splits the guard, so part of the dump can still reach Release: {lines[i].Trim()}");
        }
    }

    private static bool IsDirective(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("#if", StringComparison.Ordinal)
            || trimmed.StartsWith("#elif", StringComparison.Ordinal)
            || trimmed.StartsWith("#else", StringComparison.Ordinal)
            || trimmed.StartsWith("#endif", StringComparison.Ordinal);
    }

    private static int OnlyLineContaining(string[] lines, string needle)
    {
        var found = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(needle, StringComparison.Ordinal)) continue;
            Assert.True(found < 0, $"expected one line with '{needle}', found lines {found + 1} and {i + 1}");
            found = i;
        }

        Assert.True(found >= 0, $"no line contains '{needle}' — the rule is asserting nothing");
        return found;
    }

    private static int LastDirectiveBefore(string[] lines, int index)
    {
        for (var i = index - 1; i >= 0; i--)
            if (IsDirective(lines[i])) return i;
        return -1;
    }

    private static int FirstDirectiveAfter(string[] lines, int index)
    {
        for (var i = index + 1; i < lines.Length; i++)
            if (IsDirective(lines[i])) return i;
        return -1;
    }
}
