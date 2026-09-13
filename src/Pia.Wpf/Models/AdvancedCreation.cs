namespace Pia.Models;

/// <summary>What an Advanced Creation interview is designing.</summary>
public enum AdvancedCreationSubject
{
    Template,
    Persona,
    Routine
}

/// <summary>
/// The control an answer is collected with. One renderer per member is what makes the three subjects
/// look alike; an unrecognised kind from the model degrades to <see cref="Text"/>.
/// </summary>
public enum AdvancedCreationAnswerKind
{
    Text,
    LongText,
    Choice,
    MultiChoice,
    Sample
}

/// <param name="Id">The key the answer is sent back under. Opaque to the user.</param>
/// <param name="Options">Empty unless the kind is Choice or MultiChoice.</param>
public sealed record AdvancedCreationQuestion(
    string Id,
    AdvancedCreationAnswerKind Kind,
    string Label,
    string? Help,
    IReadOnlyList<string> Options,
    bool Optional);

/// <summary>
/// One reply from the model: either questions to put to the user, or the finished draft.
/// <see cref="DraftJson"/> is the raw object so the caller can hand it to the same parser the one-shot
/// generators use.
/// </summary>
public sealed record AdvancedCreationTurn(
    bool IsComplete,
    string? Summary,
    IReadOnlyList<AdvancedCreationQuestion> Questions,
    string? DraftJson);

/// <summary>
/// What the interview may ask about, per subject. <paramref name="DraftKeysBlock"/> is lifted from the
/// matching one-shot prompt so the closing turn produces exactly what that generator produces.
/// </summary>
/// <param name="ExtraContext">Routines pass the tool list this device offers, so the model picks a real
/// name rather than one it remembers.</param>
public sealed record AdvancedCreationMode(
    AdvancedCreationSubject Subject,
    string SubjectPreamble,
    string DraftKeysBlock,
    string? ExtraContext,
    WindowMode ProviderMode);
