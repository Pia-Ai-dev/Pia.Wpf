using Pia.Models;

namespace Pia.ViewModels.Models;

/// <summary>A thumbs-up/down click on one answer, carried from the toolbar to the view model.</summary>
public sealed record AnswerRatingRequest(AssistantMessage Message, bool Positive);
