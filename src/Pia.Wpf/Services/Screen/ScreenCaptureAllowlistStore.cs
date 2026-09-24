using Pia.Services.Interfaces;

namespace Pia.Services.Screen;

/// <summary>A sibling file rather than a list on <c>AppSettings</c>: a title fragment is a user-named item, and
/// <c>settings.json</c> is what the policy merge, the recovery-key restore and support requests all handle.</summary>
public sealed class ScreenCaptureAllowlistStore
    : JsonPersistenceService<ScreenCaptureAllowlistState>, IScreenCaptureAllowlistStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _directory;

    public ScreenCaptureAllowlistStore()
    {
    }

    internal ScreenCaptureAllowlistStore(string directory) => _directory = directory;

    public event EventHandler? Changed;

    protected override string FileName => "screen-capture-allowlist.json";

    protected override string DirectoryPath => _directory ?? SettingsDirectory;

    protected override ScreenCaptureAllowlistState CreateDefault() => new();

    /// <summary>A copy: <c>LoadAsync</c> hands back the live cached object, and a caller holding that list would
    /// see — or make — edits behind this store's gate.</summary>
    public async Task<IReadOnlyList<ScreenCaptureAllowlistEntry>> ListAsync()
    {
        var state = await LoadAsync();
        return state.Entries.ToList();
    }

    public async Task<ScreenCaptureAllowlistEntry?> AddAsync(string? processName, string? titleContains)
    {
        var normalized = ScreenCaptureAllowlistMatcher.NormalizeProcessName(processName);
        if (normalized.Length == 0)
            return null;

        var pattern = (titleContains ?? string.Empty).Trim();

        ScreenCaptureAllowlistEntry? added;
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            var duplicate = state.Entries.Any(e =>
                ScreenCaptureAllowlistMatcher.NormalizeProcessName(e.ProcessName)
                    .Equals(normalized, StringComparison.OrdinalIgnoreCase)
                && e.TitleContains.Trim().Equals(pattern, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
                return null;

            added = new ScreenCaptureAllowlistEntry(Guid.NewGuid(), normalized, pattern, DateTimeOffset.UtcNow);
            state.Entries.Add(added);
            await SaveAsync(state);
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate: a handler that reads the list back must not wait on the write that provoked it.
        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            if (state.Entries.RemoveAll(e => e.Id == id) == 0)
                return false;

            await SaveAsync(state);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
