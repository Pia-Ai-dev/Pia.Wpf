using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// A process token cannot gain or shed elevation while it runs, so the answer is probed once and cached.
/// </summary>
public sealed class ElevationService : IElevationService
{
    private readonly Lazy<bool> _isElevated;

    public ElevationService(ILogger<ElevationService> logger)
    {
        _isElevated = new Lazy<bool>(() =>
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                logger.LogInformation("Process elevation probed: {IsElevated}", elevated);
                return elevated;
            }
            catch (Exception ex)
            {
                // Unreadable token: claim the safer answer rather than warning about rights we cannot confirm.
                logger.LogWarning(ex, "Could not determine process elevation");
                return false;
            }
        });
    }

    public bool IsElevated => _isElevated.Value;
}
