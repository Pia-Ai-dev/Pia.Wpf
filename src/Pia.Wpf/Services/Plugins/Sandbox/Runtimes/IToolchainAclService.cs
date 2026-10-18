namespace Pia.Services.Plugins.Sandbox.Runtimes;

public interface IToolchainAclService
{
    bool HasAllAppPackagesAce(string root);
    void GrantAllAppPackagesRead(string root);
}
