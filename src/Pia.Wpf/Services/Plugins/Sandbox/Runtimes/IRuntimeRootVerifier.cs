namespace Pia.Services.Plugins.Sandbox.Runtimes;

public interface IRuntimeRootVerifier
{
    /// <returns>Why Pia will not grant this folder per start; null when it may.</returns>
    RuntimeIssue? Check(string root, bool isProgramFolder);
}
