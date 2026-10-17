using System.IO;
using Microsoft.Win32;
using Pia.Native;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>The SIDs of the containers Pia itself creates.</summary>
public interface IPiaContainerSidResolver
{
    IReadOnlySet<string> Resolve(IEnumerable<string> containerNames);
}

/// <summary>Our containers are the ones named <c>pia-mcp-*</c>. The profile mappings give a SID its name, and the
/// names in use give theirs directly, so a leak is found even when the mapping is gone.</summary>
public sealed class PiaContainerSidResolver : IPiaContainerSidResolver
{
    private const string NamePrefix = "pia-mcp-";
    private const string MappingsKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings";

    // ALL APPLICATION PACKAGES and ALL RESTRICTED APPLICATION PACKAGES are legitimate OS ACEs, never a leak.
    private static readonly HashSet<string> WellKnown = ["S-1-15-2-1", "S-1-15-2-2"];

    private readonly Func<IEnumerable<(string Sid, string Moniker)>> _mappings;
    private readonly Func<string, string?> _deriveSid;

    public PiaContainerSidResolver() : this(RegistryMappings, AppContainerNative.DeriveSid)
    {
    }

    internal PiaContainerSidResolver(
        Func<IEnumerable<(string Sid, string Moniker)>> mappings,
        Func<string, string?> deriveSid)
    {
        _mappings = mappings;
        _deriveSid = deriveSid;
    }

    public IReadOnlySet<string> Resolve(IEnumerable<string> containerNames)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (sid, moniker) in _mappings())
            {
                if (moniker.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
                    sids.Add(sid);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // The names in use below still identify the current servers' containers.
        }

        foreach (var name in containerNames)
        {
            if (_deriveSid(name) is { } derived)
                sids.Add(derived);
        }

        sids.ExceptWith(WellKnown);
        return sids;
    }

    private static List<(string Sid, string Moniker)> RegistryMappings()
    {
        var result = new List<(string, string)>();
        using var key = Registry.CurrentUser.OpenSubKey(MappingsKey);
        if (key is null)
            return result;

        foreach (var name in key.GetSubKeyNames())
        {
            using var mapping = key.OpenSubKey(name);
            if (mapping?.GetValue("Moniker") is string moniker)
                result.Add((name, moniker));
        }

        return result;
    }
}
