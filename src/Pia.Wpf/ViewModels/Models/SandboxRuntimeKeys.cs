using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.ViewModels.Models;

public static class SandboxRuntimeKeys
{
    /// <summary>The ViewStrings key for a parse refusal; null for <see cref="RunnerRefusal.None"/>.</summary>
    public static string? ForRefusal(RunnerRefusal refusal, string? subject) => refusal switch
    {
        RunnerRefusal.None => null,
        RunnerRefusal.NonGoalLauncher or RunnerRefusal.ScriptFile => "McpServers_Sandbox_Reason_Shim",
        RunnerRefusal.UnsupportedForm => subject switch
        {
            "npm" => "McpServers_Sandbox_Reason_UnsupportedForm_Npm",
            "uv" => "McpServers_Sandbox_Reason_UnsupportedForm_Uv",
            "dotnet" => "McpServers_Sandbox_Reason_UnsupportedForm_Dotnet",
            "node" => "McpServers_Sandbox_Reason_UnsupportedForm_Node",
            _ => "McpServers_Sandbox_Reason_Shim",
        },
        _ => $"McpServers_Sandbox_Reason_{refusal}",
    };

    /// <summary>True when the refusal's text formats the parse's subject into {0}.</summary>
    public static bool TakesSubject(RunnerRefusal refusal) => refusal == RunnerRefusal.UvxOption;

    public static string? ForIssue(RuntimeIssue issue) =>
        issue == RuntimeIssue.None ? null : $"McpServers_Sandbox_Runtime_Issue_{issue}";

    public static string? ForRunner(RunnerKind kind) =>
        kind == RunnerKind.None ? null : $"McpServers_Sandbox_Runner_{kind}";

    public static string? ForGrantFailure(MachineWideGrantFailure failure) =>
        failure == MachineWideGrantFailure.None ? null : $"McpServers_Sandbox_Runtime_FixFailed_{failure}";
}
