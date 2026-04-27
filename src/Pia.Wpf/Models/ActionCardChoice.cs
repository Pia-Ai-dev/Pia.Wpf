namespace Pia.Models;

/// <summary>
/// A single named option on a multi-choice <see cref="ActionCardInfo"/>.
/// <see cref="Key"/> is the stable identifier returned to the caller; <see cref="Label"/>
/// is the localized button text.
/// </summary>
public sealed record ActionCardChoice(string Key, string Label);
