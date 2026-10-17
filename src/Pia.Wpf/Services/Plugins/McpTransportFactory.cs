using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;

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
    private readonly Func<string, bool>? _fileExists;
    private readonly SandboxGuardEnvironment? _guardEnvironment;

    public McpTransportFactory(
        IMxcProcessLauncher? launcher = null,
        ISandboxGrantVerifier? verifier = null,
        ILoggerFactory? loggerFactory = null)
        : this(launcher, verifier, loggerFactory, fileExists: null, guardEnvironment: null)
    {
    }

    internal McpTransportFactory(
        IMxcProcessLauncher? launcher,
        ISandboxGrantVerifier? verifier,
        ILoggerFactory? loggerFactory,
        Func<string, bool>? fileExists,
        SandboxGuardEnvironment? guardEnvironment = null)
    {
        _launcher = launcher;
        _verifier = verifier ?? new SandboxGrantVerifier();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _fileExists = fileExists;
        _guardEnvironment = guardEnvironment;
    }

    public IClientTransport Create(McpTransportRequest request)
    {
        if (request.Sandbox is null)
            return new StdioClientTransport(BuildStdioOptions(request));

        if (_launcher is null)
            throw new SandboxStartException("Sandboxing is not available on this PC, so this server was not started.");

        var plan = SandboxLaunchSpecBuilder.Build(request, fileExists: _fileExists);
        if (plan.Spec is null)
            throw new SandboxStartException(plan.Error!);

        var issues = SandboxPathGuard.Validate(
            request.Sandbox, request.Command, _guardEnvironment ?? SandboxGuardEnvironment.Current(), _fileExists);
        if (issues.Count > 0)
            throw new SandboxStartException($"Pia can't sandbox this server: {string.Join(" ", issues.Select(i => i.Message))}");

        return new MxcClientTransport(
            plan.Spec, _launcher, _verifier, _loggerFactory.CreateLogger<MxcClientTransport>(), _loggerFactory);
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
