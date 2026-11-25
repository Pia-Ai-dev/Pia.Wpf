namespace Pia.Shared.Models;

/// <summary>The answer to <c>POST /api/ai-feedback</c>.</summary>
public class AiFeedbackResponse
{
    public const string DeliveryNotified = "notified";
    public const string DeliveryStoredOnly = "stored_only";

    public Guid Id { get; set; }

    /// <summary><see cref="DeliveryNotified"/> or <see cref="DeliveryStoredOnly"/>; null from a server that does not say.</summary>
    public string? Delivery { get; set; }
}
