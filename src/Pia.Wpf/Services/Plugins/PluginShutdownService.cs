using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Plugins;

/// <summary>Stops every plugin handler when Pia ends, so a sandboxed MCP server gives its folders back before the process goes.</summary>
public sealed class PluginShutdownService
{
    private static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(5);

    private readonly IPluginService _plugins;
    private readonly ILogger<PluginShutdownService> _logger;
    private readonly TimeSpan _bound;
    private int _started;

    public PluginShutdownService(IPluginService plugins, ILogger<PluginShutdownService> logger)
        : this(plugins, logger, DefaultBound)
    {
    }

    internal PluginShutdownService(IPluginService plugins, ILogger<PluginShutdownService> logger, TimeSpan bound)
    {
        _plugins = plugins;
        _logger = logger;
        _bound = bound;
    }

    /// <summary>Waits at most the bound and never throws; a second caller returns at once.</summary>
    public void ShutDownBounded()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        try
        {
            var running = CountRunningMcpServers();
            var watch = Stopwatch.StartNew();

            // Task.Run: a Wait over awaits that captured the dispatcher context would deadlock on the UI thread.
            if (Task.Run(_plugins.ShutdownAllAsync).Wait(_bound))
                _logger.LogInformation("Stopped {Count} MCP server(s) in {Elapsed} ms", running, watch.ElapsedMilliseconds);
            else
                _logger.LogWarning("Stopping MCP servers timed out after {Seconds} s", _bound.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping MCP servers failed");
        }
    }

    private int CountRunningMcpServers()
    {
        try
        {
            return _plugins.GetLocalMcpPlugins().Count(p => _plugins.GetLocalMcpStatus(p.Id).IsRunning);
        }
        catch (Exception)
        {
            // Only a log figure: it must never stand between Pia and the shutdown itself.
            return 0;
        }
    }
}
