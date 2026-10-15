using Pia.Models;

namespace Pia.Helpers;

/// <summary>How many tokens dropped files may take in one chat — re-sent every turn, since a live chat is never compacted.</summary>
public static class AttachmentBudget
{
    public const double WindowShare = 0.25;
    public const int MinTokens = 2_000;
    public const int MaxTokens = 50_000;

    public static int ForChat(AiProvider? provider)
    {
        var input = AgentContextBudget.From(provider) is { } budget
            ? budget.WindowTokens - budget.MaxOutputTokens
            : ContextWindowDefaults.For(provider?.ModelName);
        return Math.Clamp((int)(input * WindowShare), MinTokens, MaxTokens);
    }

    /// <summary>Smallest first: a file under its fair share gets all it needs, the rest split what is left.</summary>
    public static int[] Distribute(IReadOnlyList<int> needs, int available)
    {
        var grants = new int[needs.Count];
        if (available <= 0) return grants;

        var order = Enumerable.Range(0, needs.Count).OrderBy(i => needs[i]).ToArray();
        var remaining = available;
        for (var k = 0; k < order.Length; k++)
        {
            var grant = Math.Min(needs[order[k]], remaining / (order.Length - k));
            grants[order[k]] = grant;
            remaining -= grant;
        }
        return grants;
    }
}
