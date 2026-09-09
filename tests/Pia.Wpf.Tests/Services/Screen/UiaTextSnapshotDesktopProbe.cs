using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Screen;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>Measures whether a UI Automation text snapshot is usable in Chromium and Electron windows with no
/// accessibility setting turned on. Needs a human: an unlocked, interactive session with real apps open.</summary>
[Collection("WpfApplicationStatic")]
public class UiaTextSnapshotDesktopProbe
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Long enough that a slow tree reports its real cost instead of clipping at the shipped watchdog —
    /// a 5 s clip would read as "no accessibility tree" for exactly the windows this exists to measure.</summary>
    private static readonly TimeSpan ProbeWatchdog = TimeSpan.FromSeconds(30);

    [DesktopProbeFact]
    public async Task SnapshotEveryEligibleWindowAndWriteTheResultsTable()
    {
        var service = new UiaTextSnapshotService(NullLogger<UiaTextSnapshotService>.Instance, ProbeWatchdog);
        var textDirectory = Path.Combine(
            Path.GetTempPath(), $"pia-uia-probe-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(textDirectory);

        var report = new StringBuilder();
        WriteHeader(report);

        // POSITIVE CONTROL. The only hard functional assertion here: it proves the service, not the gate.
        using (var fixture = ProbeWindow.Show())
        {
            var control = await service.SnapshotAsync(fixture.Handle);
            report.AppendLine("## Positive control");
            report.AppendLine();
            report.AppendLine($"- outcome: `{control.Outcome}`");
            report.AppendLine($"- elements: {control.ElementsVisited}, text nodes: {control.TextNodes}, "
                + $"chars: {control.Text.Length}, ms: {control.Elapsed.TotalMilliseconds:F0}");
            report.AppendLine();

            var alpha = control.Text.IndexOf("alpha bravo", StringComparison.Ordinal);
            var charlie = control.Text.IndexOf("charlie", StringComparison.Ordinal);
            var delta = control.Text.IndexOf("delta echo", StringComparison.Ordinal);
            Assert.True(alpha >= 0 && charlie > alpha && delta > charlie,
                $"the control window's three lines must come back in order, got: {control.Text.Length} chars");
        }

        var capture = new GdiScreenCaptureService(NullLogger<GdiScreenCaptureService>.Instance);
        var windows = (await capture.EnumerateTargetsAsync())
            .Where(t => t.Kind == CaptureTargetKind.Window && !t.IsMinimized)
            .ToList();

        report.AppendLine("## Windows");
        report.AppendLine();
        report.AppendLine("| # | process | title chars | cold | warm | stable | coverage (human) | fresh start? (human) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");

        var rows = 0;
        var index = 0;
        foreach (var target in windows)
        {
            index++;
            var cold = await service.SnapshotAsync(target.Hwnd);
            await Task.Delay(1500);
            var warm = await service.SnapshotAsync(target.Hwnd);
            await Task.Delay(500);
            var again = await service.SnapshotAsync(target.Hwnd);

            var stable = warm.ContentHash.Length > 0 && warm.ContentHash == again.ContentHash;
            report.AppendLine(
                $"| {index} | {target.ProcessName} | {target.Title.Length} | {Cell(cold)} | {Cell(warm)} "
                + $"| {stable} |  |  |");
            rows++;

            WriteText(textDirectory, index, target.ProcessName, warm);
        }

        report.AppendLine();
        WriteFooter(report, textDirectory);

        var document = ReportPath();
        File.WriteAllText(document, report.ToString());

        Assert.True(File.Exists(document));
        Assert.NotEqual(0, rows);
    }

    /// <summary><c>fetch</c> when the timeout hit before a single element was visited, i.e. the one unbounded
    /// cross-process call is what was slow — not a missing tree.</summary>
    private static string Cell(ScreenTextSnapshot snapshot)
    {
        var stage = snapshot.Outcome == ScreenTextSnapshotOutcome.Timeout && snapshot.ElementsVisited == 0
            ? "fetch"
            : "walk";
        return $"{snapshot.Outcome}/{stage}/{snapshot.ElementsVisited}/{snapshot.TextNodes}/"
            + $"{snapshot.Text.Length}/{snapshot.Elapsed.TotalMilliseconds:F0}ms";
    }

    /// <summary>Under %TEMP%, never under the repo: this is the user's own screen.</summary>
    private static void WriteText(string directory, int index, string process, ScreenTextSnapshot snapshot)
    {
        if (snapshot.Text.Length == 0) return;

        var safe = string.Concat(process.Where(char.IsLetterOrDigit));
        var file = Path.Combine(directory, $"{index:D2}-{(safe.Length == 0 ? "window" : safe)}.txt");
        File.WriteAllText(file, snapshot.Text);
    }

    private static string ReportPath()
    {
        var folder = Path.Combine(RepositoryRoot, "docs", "screen_capture");
        Directory.CreateDirectory(folder);

        var stem = $"{DateTime.Now:yyyy-MM-dd}-uia-text-probe";
        var path = Path.Combine(folder, stem + ".md");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(folder, $"{stem}-{suffix}.md");
        }

        return path;
    }

    private static void WriteHeader(StringBuilder report)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        report.AppendLine("# Screen text — UI Automation probe results");
        report.AppendLine();
        report.AppendLine($"**Status.** Measured {today}; verdict pending (human).");
        report.AppendLine("**Owner.** Marco Altmann");
        report.AppendLine($"**Written.** {today}");
        report.AppendLine("**Origin.** The UIA text-snapshot step of "
            + "[2026-09-07-screen-vision-checklist.md](2026-09-07-screen-vision-checklist.md).");
        report.AppendLine();
        report.AppendLine("## Procedure");
        report.AppendLine();
        report.AppendLine("With NO screen reader and no accessibility setting enabled, and each app STARTED "
            + "FRESH for the run — Chromium builds its accessibility tree the first time a UIA client asks, so "
            + "a warm hit after a cold miss is a finding, not a failure — open: Edge or Chrome on a text-heavy "
            + "page; VS Code with a source file; Teams (a chat with text; it is WebView2/Chromium); Slack or "
            + "another Electron app if present; Word with a paragraph; Notepad; an mstsc window (expect "
            + "`NoText`); a PDF in Edge. Unlock the desktop and run from an interactive terminal:");
        report.AppendLine();
        report.AppendLine("```");
        report.AppendLine("dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --explicit only "
            + "--filter-class Pia.Tests.Services.Screen.UiaTextSnapshotDesktopProbe");
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("Then pick one visible paragraph per app, count how many of its words appear in that "
            + "window's text file, fill in **coverage**, delete the temp folder named at the end, commit this "
            + "document, and write the verdict below into the checklist.");
        report.AppendLine();
        report.AppendLine("Cell format: `outcome/stage/elements/textNodes/chars/ms`. `stage` is `fetch` when "
            + "the one unbounded subtree fetch timed out before any element was visited, `walk` otherwise.");
        report.AppendLine();
        report.AppendLine("## Environment");
        report.AppendLine();
        report.AppendLine($"- OS: `{Environment.OSVersion}`");
        report.AppendLine($"- MaxElements: `{UiaTextSnapshotService.MaxElements}`, "
            + $"MaxChars: `{UiaTextSnapshotService.MaxChars}`, "
            + $"shipped watchdog: `{UiaTextSnapshotService.Watchdog.TotalSeconds:F0}s`, "
            + $"probe watchdog: `{ProbeWatchdog.TotalSeconds:F0}s`");
        report.AppendLine();
    }

    private static void WriteFooter(StringBuilder report, string textDirectory)
    {
        report.AppendLine("## Coverage");
        report.AppendLine();
        report.AppendLine($"Extracted text per window: `{textDirectory}` — the user's own screen. Read it, fill "
            + "the table's coverage column, then delete the folder. Never commit it.");
        report.AppendLine();
        report.AppendLine("## Verdict");
        report.AppendLine();
        report.AppendLine("**Yes** requires, for the Chromium window AND every Electron window probed: warm "
            + "`Ok`; warm elapsed under 2000 ms; coverage at least 80 % of the chosen paragraph's words; "
            + "`stable` true for a window nobody touched between the two snapshots; and no accessibility "
            + "setting was turned on to get there.");
        report.AppendLine();
        report.AppendLine("**Mixed** — Chromium passes and an Electron app fails: the OCR fallback becomes "
            + "required rather than conditional.");
        report.AppendLine();
        report.AppendLine("**No** — Chromium fails too: the watch feature is OCR-only or does not ship.");
        report.AppendLine();
        report.AppendLine("Verdict: _(fill in)_");
    }

    /// <summary>Three lines of known text in a window that really pumps messages, so a failure here is the
    /// service's and not the desktop's.</summary>
    private sealed class ProbeWindow : IDisposable
    {
        private Dispatcher? _dispatcher;
        private Thread? _thread;

        public nint Handle { get; private set; }

        public static ProbeWindow Show()
        {
            var fixture = new ProbeWindow();
            var ready = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "alpha bravo" });
                panel.Children.Add(new TextBlock { Text = "charlie" });
                panel.Children.Add(new TextBlock { Text = "delta echo" });

                var window = new Window
                {
                    Title = "Pia UIA probe",
                    Width = 320,
                    Height = 240,
                    Left = 60,
                    Top = 60,
                    ShowInTaskbar = false,
                    Content = panel,
                };

                window.ContentRendered += (_, _) =>
                {
                    fixture.Handle = new WindowInteropHelper(window).Handle;
                    fixture._dispatcher = window.Dispatcher;
                    ready.Set();
                };

                window.Show();

                // A window with no message pump never builds an automation tree for a client to read.
                Dispatcher.Run();
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            fixture._thread = thread;

            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("The probe fixture window never rendered.");
            }

            Thread.Sleep(300);
            return fixture;
        }

        public void Dispose()
        {
            _dispatcher?.InvokeShutdown();
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
    }
}
