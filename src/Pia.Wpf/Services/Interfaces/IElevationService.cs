namespace Pia.Services.Interfaces;

/// <summary>Whether this process holds local-administrator rights.</summary>
public interface IElevationService
{
    bool IsElevated { get; }
}
