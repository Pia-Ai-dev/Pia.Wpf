namespace Pia.Services.Interfaces;

/// <summary>Announces a server policy change to the user. Called by the policy-change coordinator only.</summary>
public interface IPolicyNotificationSurface
{
    /// <param name="restartRequired">True when some of the moved values only take effect after a restart, so
    /// the notice must not claim they are already in force.</param>
    void NotifyValuesChanged(bool restartRequired);
}
