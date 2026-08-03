using System.Globalization;

namespace Pia.Shared;

/// <summary>
/// The single source of the persona identity-block shape: the persona's system prompt, optional
/// guardrails as their own paragraph, then the substrate date line.
///
/// <para>Two composers render this shape and must stay byte-identical: the WPF client's
/// <c>AssistantPromptComposer.BuildIdentityBlock</c> (the runtime source of truth — the server only
/// ever sees the finished system message) and the server's <c>ManagedPersonaPreviewPrompt</c>, which
/// reproduces the client's prompt for the admin preview because it has no client to ask. Before this
/// class existed the server carried a hand-written mirror, and a client-side shape change drifted
/// silently. Now both delegate here, so a shape change lands in one place and the pinned-string tests
/// on BOTH sides (<c>PersonaPromptCompositionTests</c> client-side,
/// <c>ManagedPersonaPreviewPromptTests</c> server-side) fail together instead of one side quietly
/// falling behind.</para>
///
/// <para><paramref name="formatProvider"/> is required, not defaulted, because the two callers
/// deliberately differ: the client passes <see cref="CultureInfo.CurrentCulture"/> so the
/// <c>dddd</c> weekday renders in the END USER's language, while the server preview passes
/// <see cref="CultureInfo.InvariantCulture"/> — the host box's culture belongs to nobody the persona
/// will ever serve, and "Samstag" injected into an English preview is noise the real turn would
/// never carry.</para>
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

        return $"""
            {systemPrompt.Trim()}{guardrailBlock}
            {dateLine}
            """;
    }
}
