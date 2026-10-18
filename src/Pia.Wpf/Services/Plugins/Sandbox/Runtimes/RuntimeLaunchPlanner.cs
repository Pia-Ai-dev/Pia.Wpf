using System.Collections;
using System.IO;
using System.Security;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

public static class RuntimeLaunchPlanner
{
    private const string NodeOptionsPrefix = "--preserve-symlinks --preserve-symlinks-main --require ";

    public static RuntimeLaunchPlan Plan(RuntimePlanInput input) => Plan(input, ReadProcessEnvironment);

    internal static RuntimeLaunchPlan Plan(RuntimePlanInput input, Func<IDictionary<string, string>> processEnvironment)
    {
        ArgumentNullException.ThrowIfNull(input);
        var install = input.Resolution.Install
            ?? throw new ArgumentException("The plan needs a resolved install.", nameof(input));

        return input.Runner.Kind switch
        {
            RunnerKind.Npx or RunnerKind.NpmExec or RunnerKind.NodeScript => PlanNode(input, install),
            RunnerKind.Dnx => PlanDnx(input, install),
            RunnerKind.DotnetDll => PlanDotnetDll(input, install),
            RunnerKind.Uvx => PlanUvx(input, install, processEnvironment()),
            _ => throw new ArgumentException("The plan needs a runner kind.", nameof(input)),
        };
    }

    private static RuntimeLaunchPlan PlanNode(RuntimePlanInput input, RuntimeInstall install)
    {
        var runner = input.Runner;
        var state = input.StateDirectory;
        var support = input.SupportDirectory;
        var preload = Path.Combine(support, NodeSandboxPreload.FileName);

        List<string> args = runner.Kind switch
        {
            RunnerKind.Npx => [EntryScript(install), .. runner.Arguments],
            RunnerKind.NpmExec => [EntryScript(install), "exec", .. runner.Arguments],
            _ => [.. runner.RuntimeOptions, ScriptOrDll(runner), .. runner.Arguments],
        };

        var environment = HomeEnvironment(state);
        environment["NODE_OPTIONS"] = NodeOptions(preload, UserValue(input.UserEnvironment, "NODE_OPTIONS"));
        environment["PIA_SANDBOX_ROOTS"] = string.Join('|',
            input.SandboxRoots.Append(install.Root).Append(state).Append(support).Distinct(StringComparer.OrdinalIgnoreCase));
        environment["PATH"] = PathVariable(input, install.Root);

        var directories = HomeDirectories(state, input.ContainerName);
        if (runner.Kind != RunnerKind.NodeScript)
        {
            var cache = Path.Combine(state, "npm-cache");
            var prefix = Path.Combine(state, "npm-prefix");
            environment["npm_config_cache"] = cache;
            environment["npm_config_prefix"] = prefix;
            environment["npm_config_userconfig"] = Path.Combine(state, "home", ".npmrc");
            environment["npm_config_update_notifier"] = "false";
            environment["npm_config_audit"] = "false";
            if (!input.AllowNetwork)
            {
                environment["npm_config_prefer_offline"] = "true";
                environment["npm_config_fetch_retries"] = "0";
            }

            directories.Add(cache);
            directories.Add(prefix);
        }

        return new RuntimeLaunchPlan(
            install.Executable,
            args,
            environment,
            input.Resolution.ToolchainRoots,
            state,
            StateReadOnly: false,
            support,
            new RuntimePreparation(directories, [new PreparedFile(preload, NodeSandboxPreload.Content)], Uv: null));
    }

    private static RuntimeLaunchPlan PlanDnx(RuntimePlanInput input, RuntimeInstall install)
    {
        var state = input.StateDirectory;
        var nuget = Path.Combine(state, "nuget");
        var packages = Path.Combine(nuget, "packages");

        var environment = DotnetEnvironment(input, install.Root);
        environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "0";
        environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        environment["NUGET_PACKAGES"] = packages;
        environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(nuget, "http-cache");
        environment["NUGET_SCRATCH"] = Path.Combine(nuget, "scratch");
        environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(nuget, "plugins-cache");

        var directories = HomeDirectories(state, input.ContainerName);
        directories.Add(packages);
        var config = new PreparedFile(
            Path.Combine(state, "home", "AppData", "Roaming", "NuGet", "NuGet.Config"),
            NuGetConfig(input.AllowNetwork, packages));

        return new RuntimeLaunchPlan(
            install.Executable,
            ["exec", EntryScript(install), "dnx", .. WithYes(input.Runner.Arguments)],
            environment,
            input.Resolution.ToolchainRoots,
            state,
            StateReadOnly: false,
            SupportDirectory: null,
            new RuntimePreparation(directories, [config], Uv: null));
    }

    private static RuntimeLaunchPlan PlanDotnetDll(RuntimePlanInput input, RuntimeInstall install)
    {
        var state = input.StateDirectory;
        return new RuntimeLaunchPlan(
            install.Executable,
            [ScriptOrDll(input.Runner), .. input.Runner.Arguments],
            DotnetEnvironment(input, install.Root),
            input.Resolution.ToolchainRoots,
            state,
            StateReadOnly: false,
            SupportDirectory: null,
            new RuntimePreparation(HomeDirectories(state, input.ContainerName), [], Uv: null));
    }

    private static RuntimeLaunchPlan PlanUvx(
        RuntimePlanInput input, RuntimeInstall install, IDictionary<string, string> processEnvironment)
    {
        var spec = input.Runner.Uvx ?? throw new ArgumentException("A uvx plan needs its package spec.", nameof(input));
        var state = input.StateDirectory;
        var tmp = Path.Combine(state, "tmp");
        var uvCache = Path.Combine(state, "uv-cache");
        var pythonCache = Path.Combine(state, "python-cache");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in processEnvironment)
        {
            if (!IsDroppedForUv(name))
                environment[name] = value;
        }

        environment["UV_CACHE_DIR"] = uvCache;
        environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(state, "python");
        environment["UV_PYTHON_CACHE_DIR"] = pythonCache;
        environment["UV_TOOL_DIR"] = Path.Combine(state, "tools");
        environment["UV_TOOL_BIN_DIR"] = Path.Combine(state, "tool-bin");
        environment["UV_PYTHON_BIN_DIR"] = Path.Combine(state, "python-bin");
        // Wheels only, so no package code runs on the host during the install.
        environment["UV_NO_BUILD"] = "1";
        environment["UV_NO_CONFIG"] = "1";
        environment["UV_PYTHON_PREFERENCE"] = "only-managed";
        environment["UV_PYTHON_INSTALL_REGISTRY"] = "0";
        environment["UV_PYTHON_INSTALL_BIN"] = "0";
        environment["TEMP"] = tmp;
        environment["TMP"] = tmp;

        var uv = new UvPrepareRequest(
            input.ContainerName,
            install.Executable,
            spec,
            environment,
            Offline: !input.AllowNetwork,
            state,
            input.Runner.Arguments,
            [uvCache, pythonCache]);

        // The provisioner fills in the command once the tool is installed.
        return new RuntimeLaunchPlan(
            string.Empty,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [],
            state,
            StateReadOnly: true,
            SupportDirectory: null,
            new RuntimePreparation([state, tmp], [], uv));
    }

    private static Dictionary<string, string> HomeEnvironment(string state)
    {
        var home = Path.Combine(state, "home");
        var drive = (Path.GetPathRoot(state) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = home,
            ["HOME"] = home,
            ["HOMEDRIVE"] = drive,
            ["HOMEPATH"] = home[drive.Length..],
            ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local"),
        };
    }

    // MXC points TEMP at LOCALAPPDATA\Packages\<container>\AC\Temp and needs it to exist before the spawn.
    private static List<string> HomeDirectories(string state, string containerName)
    {
        var home = Path.Combine(state, "home");
        return
        [
            state,
            home,
            Path.Combine(home, "AppData", "Roaming"),
            Path.Combine(home, "AppData", "Local", "Packages", containerName, "AC", "Temp"),
        ];
    }

    private static Dictionary<string, string> DotnetEnvironment(RuntimePlanInput input, string root)
    {
        var environment = HomeEnvironment(input.StateDirectory);
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_CLI_HOME"] = Path.Combine(input.StateDirectory, "home");
        environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        environment["DOTNET_NOLOGO"] = "1";
        environment["PATH"] = PathVariable(input, root);
        return environment;
    }

    private static string PathVariable(RuntimePlanInput input, string root)
    {
        var userPath = UserValue(input.UserEnvironment, "PATH");
        var rest = string.IsNullOrEmpty(userPath) ? input.RegistryPath : userPath;
        return string.IsNullOrEmpty(rest) ? root : root + ";" + rest;
    }

    // A backslash inside the quotes would be read as an escape.
    private static string NodeOptions(string preloadPath, string? userOptions)
    {
        var options = NodeOptionsPrefix + "\"" + preloadPath.Replace('\\', '/') + "\"";
        return string.IsNullOrWhiteSpace(userOptions) ? options : options + " " + userOptions;
    }

    private static string? UserValue(IReadOnlyDictionary<string, string>? environment, string name)
    {
        if (environment is null)
            return null;

        foreach (var (key, value) in environment)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    // A sandbox has no console to answer dnx's prompt.
    private static IReadOnlyList<string> WithYes(IReadOnlyList<string> arguments)
    {
        var package = -1;
        for (var i = 0; i < arguments.Count && arguments[i] != "--"; i++)
        {
            if (arguments[i] is "--yes" or "-y")
                return arguments;
            if (package < 0 && !arguments[i].StartsWith('-'))
                package = i;
        }

        if (package < 0)
            return arguments;

        return [.. arguments.Take(package + 1), "--yes", .. arguments.Skip(package + 1)];
    }

    private static string NuGetConfig(bool allowNetwork, string localPackages)
    {
        var source = allowNetwork
            ? "<add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\" />"
            : "<add key=\"pia-local\" value=\"" + SecurityElement.Escape(localPackages) + "\" />";
        return string.Join("\r\n",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<configuration>",
            "  <packageSources>",
            "    <clear />",
            "    " + source,
            "  </packageSources>",
            "</configuration>",
            string.Empty);
    }

    private static bool IsDroppedForUv(string name) =>
        (name.StartsWith("UV_", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "UV_NATIVE_TLS", StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("PIA_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "MXC_FFI_DIR", StringComparison.OrdinalIgnoreCase);

    private static string EntryScript(RuntimeInstall install) =>
        install.EntryScript ?? throw new ArgumentException("The install has no entry script.", nameof(install));

    private static string ScriptOrDll(RunnerRequest runner) =>
        runner.ScriptOrDll ?? throw new ArgumentException("The runner names no script or dll.", nameof(runner));

    private static IDictionary<string, string> ReadProcessEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                environment[name] = value;
        }

        return environment;
    }
}
