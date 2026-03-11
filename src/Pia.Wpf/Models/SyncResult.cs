namespace Pia.Models;

public record SyncResult(int PushedCount, int PulledCount, int DecryptionErrors);
