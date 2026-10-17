using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pia.Logging;
using Pia.Paths;
using Pia.Services.Exceptions;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>The last lines a server wrote to stderr, which usually name why it would not start.</summary>
public interface IStderrTailSource
{
    string StderrTail { get; }
}

/// <summary>Runs an MCP server inside an MXC container and speaks to it over the container's pipes.</summary>
public sealed class MxcClientTransport : IClientTransport, IStderrTailSource
{
    private const int StderrTailLines = 20;

    private readonly SandboxLaunchSpec _spec;
    private readonly IMxcProcessLauncher _launcher;
    private readonly ISandboxGrantVerifier _verifier;
    private readonly ILogger _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Queue<string> _stderrTail = new();
    private int _connected;

    public MxcClientTransport(
        SandboxLaunchSpec spec,
        IMxcProcessLauncher launcher,
        ISandboxGrantVerifier verifier,
        ILogger logger,
        ILoggerFactory? loggerFactory)
    {
        _spec = spec;
        _launcher = launcher;
        _verifier = verifier;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "mxc:" + _spec.ContainerName;

    public string StderrTail
    {
        get
        {
            lock (_stderrTail)
                return string.Join("\n", _stderrTail);
        }
    }

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _connected, 1) != 0)
            throw new InvalidOperationException("The transport is already connected.");

        CreateOwnedFolders();
        var grants = _verifier.Verify(_spec);
        if (!grants.Ok)
        {
            _logger.LogInformation("{Container} not started: {Blocked} item(s) the sandbox cannot be given access to",
                _spec.ContainerName, grants.BlockedCount);
            throw new SandboxStartException(grants.Reason ?? "Pia can't sandbox this server.");
        }

        var launched = await _launcher.LaunchAsync(_spec, cancellationToken).ConfigureAwait(false);
        try
        {
            var pump = Task.Run(() => PumpStderrAsync(launched.StandardError), CancellationToken.None);
            var inner = await new StreamClientTransport(launched.StandardInput, launched.StandardOutput, _loggerFactory)
                .ConnectAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Container} started, pid {Pid}", _spec.ContainerName, launched.ProcessId);
            return new MxcSessionTransport(inner, launched, pump, this);
        }
        catch
        {
            await launched.ShutdownAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The editor proposes a folder under Pia's own sandbox root without creating it; nothing else is created.
    private void CreateOwnedFolders()
    {
        var root = Path.TrimEndingDirectorySeparator(PiaPaths.McpSandboxRoot) + Path.DirectorySeparatorChar;
        foreach (var folder in _spec.ReadwritePaths.Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            Directory.CreateDirectory(folder);
    }

    private async Task PumpStderrAsync(Stream stderr)
    {
        try
        {
            using var reader = new StreamReader(stderr);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _logger.SensitiveDebug("[{Container}] stderr: {Line}", _spec.ContainerName, line);
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > StderrTailLines)
                        _stderrTail.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Closes stdin first so a well-behaved server exits at once, instead of the SDK waiting out its
    /// own shutdown timeout before it ever closes the pipe.</summary>
    private sealed class MxcSessionTransport(
        ITransport inner,
        LaunchedMxcProcess launched,
        Task stderrPump,
        MxcClientTransport owner) : ITransport
    {
        private static readonly TimeSpan PumpWait = TimeSpan.FromSeconds(2);

        private int _disposed;

        public string? SessionId => inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested && owner.StderrTail.Length > 0)
            {
                throw new IOException(
                    $"{ex.Message}{Environment.NewLine}Server's stderr tail:{Environment.NewLine}{owner.StderrTail}", ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await launched.ShutdownAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
            try
            {
                await stderrPump.WaitAsync(PumpWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }
}
