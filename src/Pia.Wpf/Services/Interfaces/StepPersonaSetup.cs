using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Everything one act step of a <see cref="RunShape.Planned"/> run needs in order to run AS a persona
/// (Batch 07 D6): the persona itself, the provider it runs on, and — the member that carries the actual
/// substance — the <see cref="AssistantTurnSetup"/> composed FROM that persona.
/// <para>
/// <b>Why the turn setup is in here and not derived downstream.</b> A per-step persona that changes only
/// the attribution glyph and the provider is <i>inert in the model</i>: the system prompt and the tool
/// list are what make a "reviewer" step behave like a reviewer, and both live in
/// <see cref="AssistantTurnSetup"/>, which <see cref="IAssistantPromptComposer.PrepareTurn"/> builds from
/// the persona. So the three travel together as one value, produced once by
/// <c>StepPersonaResolver</c> and consumed unsplit by both executors.
/// </para>
/// <para>
/// Lives in <c>Pia.Services.Interfaces</c> deliberately: <see cref="AssistantTurnSetup"/> is declared
/// here, <c>Pia.Models</c> may not depend on <c>Pia.Services</c>, and a record in the ROOT
/// <c>Pia.Services</c> namespace fails the architecture rule that keeps records off that shelf. Same
/// reasoning, same shelf, as <c>StepTurnSpec</c>.
/// </para>
/// </summary>
/// <param name="Persona">
/// The persona this step runs as. Both executors read its identity for attribution — Headless stamps
/// <c>SyncMessagePersona</c> onto the persisted message, Live projects a <c>PersonaAttribution</c> — which
/// is why this is the whole <see cref="Models.Persona"/> and not just an id.
/// </param>
/// <param name="Provider">
/// The provider to send this step's exchange to. May be a <see cref="AiProvider.Clone"/> carrying the
/// persona's <see cref="Models.Persona.ReasoningEffort"/>: never the shared instance the provider store
/// handed out, because mutating that would leak one persona's effort into every other run in the process.
/// </param>
/// <param name="TurnSetup">The system prompt + tool list composed from <paramref name="Persona"/>.</param>
public sealed record StepPersonaSetup(Persona Persona, AiProvider Provider, AssistantTurnSetup TurnSetup);
