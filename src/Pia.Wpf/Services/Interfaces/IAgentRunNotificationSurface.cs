namespace Pia.Services.Interfaces;

/// <summary>
/// Publishes durable Flow items for terminal agent runs (§15.4, R18/G3). Subscribes to
/// <see cref="IAgentRunService.RunChanged"/> in its constructor; eager-resolved at startup so it attaches
/// before any run completes. No members beyond that lifecycle — a marker interface for DI/eager-resolve.
/// </summary>
public interface IAgentRunNotificationSurface
{
}
