namespace Pia.Services.E2EE;

/// <summary>The server no longer accepts this onboarding session; registering again opens a new one.</summary>
public sealed class OnboardingSessionExpiredException(string message) : InvalidOperationException(message);
