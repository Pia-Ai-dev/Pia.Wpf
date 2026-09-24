using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pia.Native;
using Pia.Services.Imaging;
using Pia.Services.Screen;
using Pia.Tests.TestInfrastructure;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>Measures whether GDI returns usable frames on a real desktop, and whether Pia's own chrome really
/// disappears from a display capture. Needs a human: an unlocked, interactive session with real apps open.</summary>
[Collection("WpfApplicationStatic")]
public class ScreenCaptureDesktopProbe
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [DesktopProbeFact]
    public async Task CaptureEveryEligibleTargetAndWriteTheResultsTable()
    {
        var logger = new CapturingLogger<GdiScreenCaptureService>();
        var service = new GdiScreenCaptureService(logger);
        var imageDirectory = Path.Combine(
            Path.GetTempPath(), $"pia-screen-probe-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(imageDirectory);

        using var fixtures = ProbeFixtures.Show();

        var targets = await service.EnumerateTargetsAsync();
        var monitors = targets.Where(t => t.Kind == CaptureTargetKind.Monitor).ToList();
        var windows = targets.Where(t => t.Kind == CaptureTargetKind.Window).ToList();

        Assert.NotEmpty(monitors);
        Assert.DoesNotContain(windows, t => t.Hwnd == fixtures.WindowHandle || t.Hwnd == fixtures.PopupHandle);

        var report = new StringBuilder();
        WriteHeader(report, fixtures, monitors);

        report.AppendLine("## Windows");
        report.AppendLine();
        report.AppendLine("| # | process | title chars | physical | ms | result | dominant share | jpeg | legibility (human) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");

        var index = 0;
        foreach (var target in windows)
        {
            report.AppendLine(await CaptureRow(service, logger, target, ++index, imageDirectory));
        }

        report.AppendLine();
        report.AppendLine("## Monitors");
        report.AppendLine();
        report.AppendLine("| # | device | physical | ms | result | dominant share | jpeg | Pia window | Pia popup |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");

        var windowVerdicts = new List<string>();
        var popupVerdicts = new List<string>();

        foreach (var target in monitors)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await service.CaptureAsync(target);
            stopwatch.Stop();

            var windowVerdict = RegionVerdict(result, target, fixtures.WindowBounds, IsMagenta);
            var popupVerdict = RegionVerdict(result, target, fixtures.PopupBounds, IsCyan);
            windowVerdicts.Add(windowVerdict);
            popupVerdicts.Add(popupVerdict);

            index++;
            var jpeg = WriteJpeg(result, logger, imageDirectory, index, target.MonitorDeviceId);
            report.AppendLine(
                $"| {index} | {target.MonitorDeviceId} | {target.Bounds.Width}x{target.Bounds.Height} "
                + $"| {stopwatch.ElapsedMilliseconds} | {Outcome(result)} | {Share(result)} | {jpeg} "
                + $"| {windowVerdict} | {popupVerdict} |");
        }

        report.AppendLine();
        report.AppendLine("## Affinity after the captures");
        report.AppendLine();
        report.AppendLine($"- Pia-shaped window: `{ReadAffinity(fixtures.WindowHandle)}` (must be `0`)");
        report.AppendLine($"- layered popup: `{ReadAffinity(fixtures.PopupHandle)}` (must be `0`)");
        report.AppendLine();
        report.AppendLine("## Capture log");
        report.AppendLine();
        report.AppendLine("```");
        foreach (var entry in logger.Entries)
        {
            report.AppendLine($"{entry.Level}: {entry.Message}");
        }

        report.AppendLine("```");
        WriteFooter(report, imageDirectory);

        var document = ReportPath();
        File.WriteAllText(document, report.ToString());

        Assert.True(File.Exists(document));
        Assert.DoesNotContain("LEAK", windowVerdicts);
        Assert.DoesNotContain("LEAK", popupVerdicts);
    }

    private static async Task<string> CaptureRow(
        GdiScreenCaptureService service,
        CapturingLogger<GdiScreenCaptureService> logger,
        CaptureTarget target,
        int index,
        string imageDirectory)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await service.CaptureAsync(target);
        stopwatch.Stop();

        var jpeg = WriteJpeg(result, logger, imageDirectory, index, target.ProcessName);

        return $"| {index} | {target.ProcessName} | {target.Title.Length} "
            + $"| {target.Bounds.Width}x{target.Bounds.Height} | {stopwatch.ElapsedMilliseconds} "
            + $"| {Outcome(result)} | {Share(result)} | {jpeg} | |";
    }

    private static string Outcome(CaptureResult result) =>
        result.IsSuccess ? "ok" : result.Reason.ToString();

    private static string Share(CaptureResult result) =>
        result.Bitmap is null
            ? "-"
            : FrameUniformityDetector.DominantShare(result.Bitmap).ToString("F3", CultureInfo.InvariantCulture);

    private static string WriteJpeg(
        CaptureResult result,
        CapturingLogger<GdiScreenCaptureService> logger,
        string imageDirectory,
        int index,
        string label)
    {
        if (result.Bitmap is null)
        {
            return "-";
        }

        var attachment = ImageAttachmentProcessor.TryPrepare(result.Bitmap, logger);
        if (attachment is null)
        {
            return "encode failed";
        }

        var safeLabel = string.Concat(label.Where(char.IsLetterOrDigit));
        var file = Path.Combine(imageDirectory, $"{index:D2}-{(safeLabel.Length == 0 ? "target" : safeLabel)}.jpg");
        File.WriteAllBytes(file, attachment.JpegBytes);

        return $"{attachment.Width}x{attachment.Height}, {attachment.JpegBytes.Length / 1024} KB";
    }

    /// <summary>Reads the fixture's rectangle out of a display grab: a coloured region there means the exclusion
    /// did not hold and Pia's own window reached the model.</summary>
    private static string RegionVerdict(
        CaptureResult result, CaptureTarget monitor, PixelRect region, Func<byte, byte, byte, bool> isFixtureColour)
    {
        if (result.Bitmap is null || region.IsEmpty)
        {
            return "-";
        }

        var local = new PixelRect(region.X - monitor.Bounds.X, region.Y - monitor.Bounds.Y, region.Width, region.Height);
        if (local.X < 0 || local.Y < 0 || local.Right > result.Width || local.Bottom > result.Height)
        {
            return "off this display";
        }

        var pixels = new byte[local.Width * local.Height * 4];
        result.Bitmap.CopyPixels(
            new Int32Rect(local.X, local.Y, local.Width, local.Height), pixels, local.Width * 4, 0);

        var total = local.Width * local.Height;
        var coloured = 0;
        var black = 0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var blue = pixels[i];
            var green = pixels[i + 1];
            var red = pixels[i + 2];

            if (isFixtureColour(blue, green, red))
            {
                coloured++;
            }
            else if (blue < 24 && green < 24 && red < 24)
            {
                black++;
            }
        }

        if (coloured >= total * 0.10)
        {
            return "LEAK";
        }

        return black >= total * 0.90 ? "excluded-black" : "excluded-see-through";
    }

    private static bool IsMagenta(byte blue, byte green, byte red) => red > 200 && blue > 200 && green < 80;

    private static bool IsCyan(byte blue, byte green, byte red) => red < 80 && green > 200 && blue > 200;

    private static string ReadAffinity(nint hwnd) =>
        ScreenCaptureInterop.GetWindowDisplayAffinity(hwnd, out var affinity)
            ? "0x" + affinity.ToString("X", CultureInfo.InvariantCulture)
            : "unreadable";

    private static void WriteHeader(StringBuilder report, ProbeFixtures fixtures, IReadOnlyList<CaptureTarget> monitors)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        report.AppendLine("# Screen capture — desktop probe results");
        report.AppendLine();
        report.AppendLine($"**Status.** Measured {today}; verdict pending (human).");
        report.AppendLine("**Owner.** Marco Altmann");
        report.AppendLine($"**Written.** {today}");
        report.AppendLine("**Origin.** The desktop-probe step of "
            + "[2026-09-07-screen-vision-checklist.md](2026-09-07-screen-vision-checklist.md).");
        report.AppendLine();
        report.AppendLine("Before running this, open on the probed desktop, un-minimized: Word or Excel with a "
            + "paragraph of 10 pt text, Teams, Edge or Chrome on a text-heavy page, VS Code, a PDF, an mstsc "
            + "session, Task Manager as administrator, Settings and Calculator, and one Explorer window "
            + "minimized. Run from an interactive terminal on that desktop — a disconnected or background "
            + "session gets no DWM composition.");
        report.AppendLine();
        report.AppendLine("## Environment");
        report.AppendLine();
        report.AppendLine($"- OS: `{Environment.OSVersion}`");
        report.AppendLine($"- WDA_EXCLUDEFROMCAPTURE available: `{OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)}`");
        report.AppendLine($"- preferred affinity: `0x{DisplayAffinityLease.PreferredAffinity:X}`");
        report.AppendLine($"- test-host thread DPI awareness: `{DpiAwarenessScope.CurrentAwareness}` "
            + "(0 unaware, 1 system, 2 per-monitor)");
        report.AppendLine($"- fixture window rect: `{fixtures.WindowBounds}`");
        report.AppendLine($"- fixture popup rect: `{fixtures.PopupBounds}`");

        foreach (var monitor in monitors)
        {
            report.AppendLine($"- monitor `{monitor.MonitorDeviceId}`: `{monitor.Bounds}`"
                + (monitor.IsPrimary ? " (primary)" : string.Empty));
        }

        report.AppendLine();
    }

    private static void WriteFooter(StringBuilder report, string imageDirectory)
    {
        report.AppendLine();
        report.AppendLine("## Legibility and verdict — fill in by hand");
        report.AppendLine();
        report.AppendLine($"The JPEGs are in `{imageDirectory}`. Open them, fill the legibility column above "
            + "(can you read the 10 pt paragraph at the 1568 px ceiling? yes / barely / no), then answer:");
        report.AppendLine();
        report.AppendLine("- usable frames: __ of __ windows, __ of __ displays");
        report.AppendLine("- black-frame rate: __");
        report.AppendLine("- Pia region in a display grab: black | see-through | LEAK");
        report.AppendLine("- verdict: GDI holds / GDI does not hold, move to Windows.Graphics.Capture");
        report.AppendLine();
        report.AppendLine("Delete the image folder when done — it is a picture of the real screen.");
    }

    private static string ReportPath()
    {
        var folder = Path.Combine(RepositoryRoot, "docs", "screen_capture");
        Directory.CreateDirectory(folder);

        var stem = $"{DateTime.Now:yyyy-MM-dd}-capture-probe";
        var path = Path.Combine(folder, stem + ".md");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(folder, $"{stem}-{suffix}.md");
        }

        return path;
    }

    /// <summary>Two top-level HWNDs shaped like the ones Pia actually owns: a Mica FluentWindow and a layered
    /// popup. Whether display affinity takes on the layered one is the open question.</summary>
    private sealed class ProbeFixtures : IDisposable
    {
        private Dispatcher? _dispatcher;
        private Thread? _thread;

        public nint WindowHandle { get; private set; }

        public nint PopupHandle { get; private set; }

        public PixelRect WindowBounds { get; private set; }

        public PixelRect PopupBounds { get; private set; }

        public static ProbeFixtures Show()
        {
            var fixtures = new ProbeFixtures();
            var ready = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                var window = new FluentWindow
                {
                    Title = "Pia screen probe",
                    WindowBackdropType = WindowBackdropType.Mica,
                    ExtendsContentIntoTitleBar = true,
                    Width = 400,
                    Height = 300,
                    Left = 80,
                    Top = 80,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    Content = new System.Windows.Controls.Border { Background = Brushes.Magenta },
                };

                var popup = new Popup
                {
                    AllowsTransparency = true,
                    Placement = PlacementMode.Absolute,
                    HorizontalOffset = 520,
                    VerticalOffset = 80,
                    Child = new System.Windows.Controls.Border
                    {
                        Width = 200,
                        Height = 150,
                        Background = Brushes.Cyan,
                    },
                };

                window.ContentRendered += (_, _) =>
                {
                    popup.IsOpen = true;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        fixtures.WindowHandle = new WindowInteropHelper(window).Handle;
                        fixtures.PopupHandle = PresentationSource.FromVisual(popup.Child) is HwndSource source
                            ? source.Handle
                            : 0;
                        fixtures._dispatcher = window.Dispatcher;
                        ready.Set();
                    }));
                };

                window.Show();

                // A window with no message pump never paints, and DWM never composes it.
                Dispatcher.Run();
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            fixtures._thread = thread;

            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("The probe fixture windows never rendered.");
            }

            Thread.Sleep(300);
            fixtures.ReadBounds();
            return fixtures;
        }

        public void Dispose()
        {
            _dispatcher?.InvokeShutdown();
            _thread?.Join(TimeSpan.FromSeconds(5));
        }

        private void ReadBounds()
        {
            using var dpi = DpiAwarenessScope.PerMonitorV2();
            WindowBounds = Rect(WindowHandle);
            PopupBounds = Rect(PopupHandle);
        }

        private static PixelRect Rect(nint hwnd)
        {
            if (hwnd == 0 || !ScreenCaptureInterop.GetWindowRect(hwnd, out var rect))
            {
                return default;
            }

            return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }
}
