namespace Pia.Services.Plugins.Sandbox.Runtimes;

public interface IRuntimeDetector
{
    RuntimeResolution Detect(RunnerRequest runner, CancellationToken cancellationToken);
}
