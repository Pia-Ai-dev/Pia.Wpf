using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>Writes the grant a tool-approval answer carries, shared by the chat card and a job's block.</summary>
public static class ToolDecisionGrants
{
    public static async Task<ToolGateDecision> ApplyAsync(
        IToolPermissionService permissions, ToolDecision decision, Guid pluginId, string toolName)
    {
        switch (decision)
        {
            case ToolDecision.AllowOnce:
                return ToolGateDecision.ApprovedOnce;
            case ToolDecision.AllowForSession:
                permissions.GrantForSession(pluginId, toolName);
                return ToolGateDecision.ApprovedForSession;
            case ToolDecision.AlwaysAllow:
                await permissions.GrantAsync(pluginId, toolName);
                return ToolGateDecision.ApprovedAlways;
            default:
                return ToolGateDecision.DeclinedByUser;
        }
    }
}
