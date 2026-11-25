using System.IO;
using System.Text.Json;
using Pia.Infrastructure;
using Pia.Paths;

namespace Pia.Services.E2EE;

public sealed class ConfirmedApproverStore : IConfirmedApproverStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private List<ConfirmedApprover>? _approvers;

    public ConfirmedApproverStore() : this(DefaultPath)
    {
    }

    public ConfirmedApproverStore(string path) => _path = path;

    // Not settings.json: AppSettings syncs to the server and is policy-bindable, and the server is who this guards against.
    public static string DefaultPath => Path.Combine(PiaPaths.LocalDataDirectory, "e2ee-confirmed-approvers.json");

    public bool IsConfirmed(string deviceId, string fingerprint)
    {
        lock (_gate)
        {
            return Load().Any(a => a.DeviceId == deviceId
                                   && string.Equals(a.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        }
    }

    public Task RecordAsync(string deviceId, string fingerprint)
    {
        lock (_gate)
        {
            var approvers = Load();
            approvers.RemoveAll(a => a.DeviceId == deviceId);
            approvers.Add(new ConfirmedApprover(deviceId, fingerprint, DateTime.UtcNow));

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicTextWriter.Write(_path, JsonSerializer.Serialize(approvers, JsonOptions));
        }
        return Task.CompletedTask;
    }

    // An unreadable file confirms nothing, which only costs the person one more comparison.
    private List<ConfirmedApprover> Load()
    {
        if (_approvers is not null)
            return _approvers;

        try
        {
            _approvers = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ConfirmedApprover>>(File.ReadAllText(_path), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _approvers = [];
        }
        return _approvers;
    }

    private sealed record ConfirmedApprover(string DeviceId, string Fingerprint, DateTime ConfirmedAt);
}
