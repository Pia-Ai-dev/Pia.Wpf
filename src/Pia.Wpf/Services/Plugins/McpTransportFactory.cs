using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Pia.Logging;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.Services.Plugins;

/// <summary>The one place a local MCP server's launch path is chosen, so a probe and the real start cannot differ.</summary>
public interface IMcpTransportFactory
{
    /// <exception cref="SandboxStartException">A sandboxed server was refused; it is never started uncontained instead.</exception>
    IClientTransport Create(McpTransportRequest request);
}

public sealed class McpTransportFactory : IMcpTransportFactory
{
    private readonly IMxcProcessLauncher? _launcher;
    private readonly ISandboxGrantVerifier _verifier;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Func<string, bool>? _fileExists;
    private readonly SandboxGuardEnvironment? _guardEnvironment;
    private readonly IRuntimeDetector? _detector;
    private readonly IRuntimeHostService? _host;
    private readonly IRuntimeStateProvisioner? _provisioner;
    private readonly Func<IReadOnlyList<string>>? _userEnvironmentNames;

    public McpTransportFactory(
        IMxcProcessLauncher? launcher = null,
        ISandboxGrantVerifier? verifier = null,
        ILoggerFactory? loggerFactory = null,
        IRuntimeDetector? detector = null,
        IRuntimeHostService? host = null,
        IRuntimeStateProvisioner? provisioner = null)
        : this(launcher, verifier, loggerFactory, fileExists: null, guardEnvironment: null, detector, host, provisioner)
    {
    }

    internal McpTransportFactory(
        IMxcProcessLauncher? launcher,
        ISandboxGrantVerifier? verifier,
        ILoggerFactory? loggerFactory,
        Func<string, bool>? fileExists,
        SandboxGuardEnvironment? guardEnvironment = null,
        IRuntimeDetector? detector = null,
        IRuntimeHostService? host = null,
        IRuntimeStateProvisioner? provisioner = null,
        Func<IReadOnlyList<string>>? userEnvironmentNames = null)
    {
        _launcher = launcher;
        _verifier = verifier ?? new SandboxGrantVerifier();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<McpTransportFactory>();
        _fileExists = fileExists;
        _guardEnvironment = guardEnvironment;
        _detector = detector;
        _host = host;
        _provisioner = provisioner;
        _userEnvironmentNames = userEnvironmentNames;
    }

    public IClientTransport Create(McpTransportRequest request)
    {
        if (request.Sandbox is null)
            return new StdioClientTransport(BuildStdioOptions(request));

        if (_launcher is null)
            throw new SandboxStartException("Sandboxing is not available on this PC, so this server was not started.");

        var runtime = new SandboxRuntimeContext(_detector, _host, CanProvision: _provisioner is not null, UserEnvironmentNames());
        var plan = SandboxLaunchSpecBuilder.Build(request, runtime, fileExists: _fileExists, logger: _logger);
        if (plan.Spec is not { } spec)
            throw new SandboxStartException(plan.Error!);

        // The detector already judged a runner's folders.
        var command = spec.RunnerKind == RunnerKind.None ? spec.Command : null;
        var issues = SandboxPathGuard.Validate(
            request.Sandbox, command, _guardEnvironment ?? SandboxGuardEnvironment.Current(), _fileExists);
        if (issues.Count > 0)
            throw new SandboxStartException($"Pia can't sandbox this server: {string.Join(" ", issues.Select(i => i.Message))}");

        return new MxcClientTransport(
            spec, _launcher, _verifier, _loggerFactory.CreateLogger<MxcClientTransport>(), _loggerFactory, _provisioner);
    }

    // Blanking never depends on DI, and a start that can't learn the names to blank is refused, never run unblanked.
    private IReadOnlyList<string> UserEnvironmentNames()
    {
        try
        {
            return _host?.UserEnvironmentNames() ?? _userEnvironmentNames?.Invoke() ?? RuntimeHostService.ReadUserEnvironmentNames();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Reading the user environment names failed: {ExceptionType}", ex.GetType().Name);
            _logger.SensitiveDebug("Reading the user environment names failed: {Message}", ex.Message);
            throw new SandboxStartException(SandboxRuntimeMessages.UserEnvironmentUnreadable);
        }
    }

    internal static StdioClientTransportOptions BuildStdioOptions(McpTransportRequest request)
    {
        // A null value removes the variable from the child's environment, which Pia has set for its own loading.
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [SandboxLaunchSpecBuilder.RuntimeDirectoryVariable] = null
        };
        foreach (var (key, value) in request.Env ?? new Dictionary<string, string>())
        {
            if (!string.Equals(key, SandboxLaunchSpecBuilder.RuntimeDirectoryVariable, StringComparison.OrdinalIgnoreCase))
                environment[key] = value;
        }

        var options = new StdioClientTransportOptions
        {
            Name = request.Name,
            Command = request.Command,
            Arguments = [.. request.Args],
            EnvironmentVariables = environment
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory))
            options.WorkingDirectory = request.WorkingDirectory;

        return options;
    }
}
