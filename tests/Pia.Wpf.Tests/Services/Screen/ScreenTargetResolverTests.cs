using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class ScreenTargetResolverTests
{
    private static CaptureTarget Window(string process, string title, bool minimized = false) =>
        new(CaptureTargetKind.Window, 1, string.Empty, new PixelRect(0, 0, 800, 600),
            process, title, false, minimized);

    private static CaptureTarget Monitor(string deviceId, bool primary) =>
        new(CaptureTargetKind.Monitor, 0, deviceId, new PixelRect(0, 0, 1920, 1080),
            string.Empty, string.Empty, primary, false);

    [Fact]
    public void Monitor_NoMatch_SingleMonitor_Resolves()
    {
        var only = Monitor(@"\\.\DISPLAY1", primary: false);

        var result = ScreenTargetResolver.Resolve([only], "monitor", null);

        Assert.Equal(ScreenTargetResolution.Resolved, result.Outcome);
        Assert.Same(only, result.Target);
    }

    [Fact]
    public void Monitor_NoMatch_SeveralMonitors_ResolvesThePrimary()
    {
        var second = Monitor(@"\\.\DISPLAY2", primary: true);

        var result = ScreenTargetResolver.Resolve(
            [Monitor(@"\\.\DISPLAY1", primary: false), second], "monitor", "  ");

        Assert.Same(second, result.Target);
    }

    [Theory]
    [InlineData("DISPLAY2")]
    [InlineData("display2")]
    [InlineData(@"\\.\DISPLAY2")]
    [InlineData("2")]
    [InlineData("primary")]
    public void Monitor_ByDeviceTail_ByIndex_ByPrimary_Resolve(string match)
    {
        var second = Monitor(@"\\.\DISPLAY2", primary: true);

        var result = ScreenTargetResolver.Resolve(
            [Monitor(@"\\.\DISPLAY1", primary: false), second], "monitor", match);

        Assert.Equal(ScreenTargetResolution.Resolved, result.Outcome);
        Assert.Same(second, result.Target);
    }

    [Fact]
    public void Monitor_UnknownName_IsNoMatch()
    {
        var result = ScreenTargetResolver.Resolve(
            [Monitor(@"\\.\DISPLAY1", primary: true)], "monitor", "DISPLAY9");

        Assert.Equal(ScreenTargetResolution.NoMatch, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public void Window_NoMatch_IsMissingMatch()
    {
        var result = ScreenTargetResolver.Resolve([Window("outlook", "Inbox")], "window", null);

        Assert.Equal(ScreenTargetResolution.MissingMatch, result.Outcome);
    }

    [Theory]
    [InlineData("OUTLOOK.exe")]
    [InlineData("Outlook")]
    [InlineData("outlook")]
    public void Window_ProcessMatch_IsCaseAndExeInsensitive(string match)
    {
        var window = Window("outlook", "Inbox - Outlook");

        var result = ScreenTargetResolver.Resolve([window], "window", match);

        Assert.Equal(ScreenTargetResolution.Resolved, result.Outcome);
        Assert.Same(window, result.Target);
    }

    [Fact]
    public void Window_TwoWindowsOfTheProcess_IsAmbiguous_WithBothCandidates()
    {
        var a = Window("outlook", "Inbox - Outlook");
        var b = Window("outlook", "Calendar - Outlook");

        var result = ScreenTargetResolver.Resolve([a, b], "window", "outlook");

        Assert.Equal(ScreenTargetResolution.Ambiguous, result.Outcome);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(a, result.Candidates);
        Assert.Contains(b, result.Candidates);
    }

    [Fact]
    public void Window_TitleFragment_ResolvesWhenProcessMisses()
    {
        var notes = Window("notepad", "q3 payroll notes");

        var result = ScreenTargetResolver.Resolve([notes, Window("excel", "Book1")], "window", "payroll");

        Assert.Equal(ScreenTargetResolution.Resolved, result.Outcome);
        Assert.Same(notes, result.Target);
    }

    [Fact]
    public void Window_TitleFragment_TwoHits_IsAmbiguous()
    {
        var result = ScreenTargetResolver.Resolve(
            [Window("notepad", "payroll draft"), Window("excel", "payroll final")], "window", "payroll");

        Assert.Equal(ScreenTargetResolution.Ambiguous, result.Outcome);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Window_ProcessMatchWinsOverTitleMatch()
    {
        var real = Window("outlook", "Inbox");

        var result = ScreenTargetResolver.Resolve(
            [Window("notepad", "outlook notes"), real], "window", "outlook");

        Assert.Same(real, result.Target);
    }

    [Fact]
    public void Window_MinimizedWindow_IsStillACandidate()
    {
        var minimized = Window("outlook", "Inbox", minimized: true);

        var result = ScreenTargetResolver.Resolve([minimized], "window", "outlook");

        Assert.Equal(ScreenTargetResolution.Resolved, result.Outcome);
        Assert.Same(minimized, result.Target);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownKind_IsUnknownKind(string? kind)
    {
        var result = ScreenTargetResolver.Resolve([Window("outlook", "Inbox")], kind, "outlook");

        Assert.Equal(ScreenTargetResolution.UnknownKind, result.Outcome);
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", "DISPLAY1")]
    [InlineData("DISPLAY1", "DISPLAY1")]
    [InlineData("", "")]
    public void DeviceTail_StripsTheWindowsPrefix(string deviceId, string expected)
    {
        Assert.Equal(expected, ScreenTargetResolver.DeviceTail(deviceId));
    }
}
