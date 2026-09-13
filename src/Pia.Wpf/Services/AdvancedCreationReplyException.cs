namespace Pia.Services.Exceptions;

/// <summary>
/// The model's reply was neither a question set nor a draft. Retryable: the transcript is intact, so the
/// panel offers another attempt rather than ending the interview on whatever the model happened to say.
/// </summary>
public sealed class AdvancedCreationReplyException(string message) : Exception(message);
