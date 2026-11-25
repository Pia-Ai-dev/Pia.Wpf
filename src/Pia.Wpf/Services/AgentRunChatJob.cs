using Pia.Models;

namespace Pia.Services;

public static class AgentRunChatJob
{
    /// <summary>True for a planned run started from a plan in the user's chat; read off the policy envelope.</summary>
    public static bool IsChatJob(this AgentRun run) => HeadlessRunLauncher.TryRestoreChatJob(run.PolicyJson);
}
