namespace Pia.Shared;

/// <summary>
/// The one identity-block shape, shared so the client composer and the server's admin preview cannot
/// drift; <paramref name="formatProvider"/> is caller-supplied because the client wants the end user's
/// weekday language and the preview wants Invariant.
/// </summary>
public static class PersonaPromptShape
{
    public static string BuildIdentityBlock(string systemPrompt, string? guardrails, DateTime now, IFormatProvider formatProvider)
    {
        var guardrailBlock = string.IsNullOrWhiteSpace(guardrails)
            ? string.Empty
            : $"\n\n{guardrails.Trim()}";

        var dateLine = string.Create(
            formatProvider,
            $"The current date and time is {now:yyyy-MM-dd HH:mm} ({now:dddd}).");

        // Explicit \n, not a raw string: the bytes must not depend on the checkout's line endings.
        return $"{systemPrompt.Trim()}{guardrailBlock}\n{dateLine}";
    }
}
