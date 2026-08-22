using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>Offers the AutomationId-bearing elements of the active window that a guided tour could point at.</summary>
public interface ITourTargetCollector
{
    Task<TourTargetScan> CollectActiveWindowAsync();
}
