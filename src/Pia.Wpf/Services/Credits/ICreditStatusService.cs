namespace Pia.Services.Credits;

public interface ICreditStatusService
{
    Task<CreditStatusResponse?> GetAsync(CancellationToken ct = default);
}
