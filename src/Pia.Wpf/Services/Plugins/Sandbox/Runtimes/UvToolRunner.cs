using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Runs the host-side, uncontained <c>uv tool install</c> into a server's runtime state folder.</summary>
public sealed class UvToolRunner
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<UvToolRunner> _logger;
    private readonly Func<ProcessStartInfo, Action<string>, CancellationToken, Task<int>> _run;

    public UvToolRunner(ILogger<UvToolRunner> logger)
        : this(logger, RunProcessAsync)
    {
    }

    internal UvToolRunner(ILogger<UvToolRunner> logger, Func<ProcessStartInfo, Action<string>, CancellationToken, Task<int>> run)
    {
        _logger = logger;
        _run = run;
    }

    /// <returns>uv's exit code.</returns>
    public async Task<int> InstallAsync(UvPrepareRequest request, Action<string> outputLine, CancellationToken cancellationToken)
    {
        var info = StartInfo(request);
        _logger.SensitiveDebug("uv install for {Container}: {Package}", request.ContainerName, request.Spec.Package);

        var clock = Stopwatch.StartNew();
        var code = await _run(info, line =>
        {
            _logger.SensitiveDebug("[{Container}] uv: {Line}", request.ContainerName, line);
            outputLine(line);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("uv install for {Container}: exit {Code} in {Ms} ms",
            request.ContainerName, code, clock.ElapsedMilliseconds);
        return code;
    }

    internal static ProcessStartInfo StartInfo(UvPrepareRequest request)
    {
        var info = new ProcessStartInfo(request.UvExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.StateDirectory,
        };

        info.ArgumentList.Add("tool");
        info.ArgumentList.Add("install");
        info.ArgumentList.Add(request.Spec.Package);
        foreach (var requirement in request.Spec.With)
        {
            info.ArgumentList.Add("--with");
            info.ArgumentList.Add(requirement);
        }

        if (request.Spec.Python is { } python)
        {
            info.ArgumentList.Add("--python");
            info.ArgumentList.Add(python);
        }

        info.Environment.Clear();
        foreach (var (name, value) in request.Environment)
            info.Environment[name] = value;

        return info;
    }

    internal static async Task<int> RunProcessAsync(ProcessStartInfo info, Action<string> outputLine, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = info };
        process.Start();
        if (info.RedirectStandardInput)
            process.StandardInput.Close();

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, outputLine),
            PumpAsync(process.StandardError, outputLine));

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        try
        {
            await pumps.WaitAsync(DrainTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> outputLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                outputLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }
}
