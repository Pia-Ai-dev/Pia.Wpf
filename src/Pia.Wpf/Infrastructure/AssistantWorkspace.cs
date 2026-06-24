using System.IO;

namespace Pia.Infrastructure;

/// <summary>
/// Single source of truth for the default assistant files folder — the agent's scratch
/// "workdir". It lives <b>inside</b> <c>%LOCALAPPDATA%\Pia</c>, which
/// <see cref="SensitivePathGuard"/> otherwise blocks wholesale (Pia's own DB/config/logs sit
/// there too). The guard carves this exact subtree back out, so the seeding in
/// <c>App.OnStartup</c> and the guard's allow-exception MUST derive from the same value — compute
/// it here once and reference it from both, or a divergence silently re-blocks the whole workdir.
/// </summary>
public static class AssistantWorkspace
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\Pia\workdir</c> — created on first run and seeded as the default sandbox.
    /// </summary>
    public static string DefaultWorkdir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "workdir");
}
