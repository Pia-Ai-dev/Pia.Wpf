using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Screen;

/// <summary>
/// Reads a window's accessibility tree through the in-box UI Automation client. The whole walk runs on a pool
/// thread: a cross-process UIA call from Pia's dispatcher blocks it while the target app pumps.
/// </summary>
public sealed class UiaTextSnapshotService : IScreenTextSnapshotService
{
    public const int MaxElements = 4000;
    public const int MaxChars = 8000;

    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    private readonly ILogger<UiaTextSnapshotService> _logger;
    private readonly TimeSpan _watchdog;

    public UiaTextSnapshotService(ILogger<UiaTextSnapshotService> logger)
        : this(logger, Watchdog)
    {
    }

    internal UiaTextSnapshotService(ILogger<UiaTextSnapshotService> logger, TimeSpan watchdog)
    {
        _logger = logger;
        _watchdog = watchdog;
    }

    public async Task<ScreenTextSnapshot> SnapshotAsync(nint hwnd, CancellationToken cancellationToken = default)
    {
        // Shared with the walk so a timeout can still tell a slow FETCH (nothing visited) from a slow WALK.
        var visited = new StrongBox<int>(0);
        var started = Stopwatch.GetTimestamp();
        var work = Task.Run(() => Walk(hwnd, visited), cancellationToken);

        try
        {
            return await work.WaitAsync(_watchdog, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var seen = Volatile.Read(ref visited.Value);
            _logger.LogWarning(
                "UIA snapshot timed out after {Timeout} ms with {Elements} element(s) visited",
                _watchdog.TotalMilliseconds, seen);
            return ScreenTextSnapshot.Failed(
                hwnd, ScreenTextSnapshotOutcome.Timeout, Stopwatch.GetElapsedTime(started), seen);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("UIA snapshot failed ({Type})", ex.GetType().Name);
            return ScreenTextSnapshot.Failed(
                hwnd, ScreenTextSnapshotOutcome.Error, Stopwatch.GetElapsedTime(started));
        }
    }

    private ScreenTextSnapshot Walk(nint hwnd, StrongBox<int> visited)
    {
        var started = Stopwatch.GetTimestamp();

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception ex) when (ex is ArgumentException or ElementNotAvailableException)
        {
            return ScreenTextSnapshot.Failed(
                hwnd, ScreenTextSnapshotOutcome.WindowGone, Stopwatch.GetElapsedTime(started));
        }

        if (root is null)
        {
            return ScreenTextSnapshot.Failed(
                hwnd, ScreenTextSnapshotOutcome.WindowGone, Stopwatch.GetElapsedTime(started));
        }

        try
        {
            var cache = new CacheRequest
            {
                AutomationElementMode = AutomationElementMode.None,
                TreeFilter = Automation.ControlViewCondition,
                TreeScope = TreeScope.Subtree,
            };
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(ValuePattern.ValueProperty);

            // ONE cross-process call, and it is unbounded: MaxElements cannot engage until it returns, so a
            // text-heavy Chromium tree is paid for in full before the walk below starts.
            var tree = root.GetUpdatedCache(cache);

            var nodes = new List<UiaTextNode>();
            Collect(tree, 0, nodes, visited);

            if (nodes.Count <= 1)
            {
                return ScreenTextSnapshot.Failed(
                    hwnd, ScreenTextSnapshotOutcome.NoAutomationTree,
                    Stopwatch.GetElapsedTime(started), Volatile.Read(ref visited.Value));
            }

            var (text, textNodes, truncated) = ScreenTextCompactor.Compact(nodes, MaxChars);
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (text.Length == 0)
            {
                return ScreenTextSnapshot.Failed(
                    hwnd, ScreenTextSnapshotOutcome.NoText, elapsed, Volatile.Read(ref visited.Value));
            }

            _logger.LogInformation(
                "UIA snapshot read {Elements} element(s), {TextNodes} text node(s), {Chars} chars in {Ms} ms",
                Volatile.Read(ref visited.Value), textNodes, text.Length, (long)elapsed.TotalMilliseconds);
            _logger.SensitiveDebug("UIA snapshot text prefix: {Prefix}", Prefix(text));

            return new ScreenTextSnapshot(
                hwnd, ScreenTextSnapshotOutcome.Ok, text, Volatile.Read(ref visited.Value),
                textNodes, truncated, elapsed, ScreenTextCompactor.Hash(text));
        }
        catch (ElementNotAvailableException)
        {
            return ScreenTextSnapshot.Failed(
                hwnd, ScreenTextSnapshotOutcome.WindowGone, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("UIA snapshot failed ({Type})", ex.GetType().Name);
            return ScreenTextSnapshot.Failed(
                hwnd, ScreenTextSnapshotOutcome.Error, Stopwatch.GetElapsedTime(started),
                Volatile.Read(ref visited.Value));
        }
    }

    private static void Collect(
        AutomationElement element, int depth, List<UiaTextNode> nodes, StrongBox<int> visited)
    {
        if (nodes.Count >= MaxElements) return;

        nodes.Add(new UiaTextNode(
            depth,
            ControlTypeName(element),
            element.Cached.Name,
            CachedValue(element),
            element.Cached.IsOffscreen));
        Volatile.Write(ref visited.Value, nodes.Count);

        foreach (AutomationElement child in element.CachedChildren)
        {
            if (nodes.Count >= MaxElements) return;
            Collect(child, depth + 1, nodes, visited);
        }
    }

    private static string ControlTypeName(AutomationElement element)
    {
        var programmatic = element.Cached.ControlType?.ProgrammaticName ?? string.Empty;
        var cut = programmatic.LastIndexOf('.');
        return cut >= 0 && cut < programmatic.Length - 1 ? programmatic[(cut + 1)..] : programmatic;
    }

    private static string? CachedValue(AutomationElement element)
    {
        var value = element.GetCachedPropertyValue(ValuePattern.ValueProperty, ignoreDefaultValue: true);
        return value == AutomationElement.NotSupported ? null : value as string;
    }

    private static string Prefix(string text) => text.Length <= 200 ? text : text[..200];
}
