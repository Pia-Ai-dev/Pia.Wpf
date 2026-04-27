namespace Pia.Services.Consent;

public interface IConsentAuditLog : IAsyncDisposable
{
    void Append(AuditEvent evt);
}
