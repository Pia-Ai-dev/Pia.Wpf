namespace Pia.Services;

/// <summary>Keeps a sign-in or sign-out from landing while sync writes a pulled page into the local stores.</summary>
public sealed class AccountTransitionCoordinator
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _sync = new();
    private readonly AsyncLocal<object?> _flowHolder = new();
    private object? _holder;
    private int _epoch;
    private int _activeTransitions;

    /// <summary>Null while an account change is under way; otherwise a token that <see cref="IsCurrent"/> checks.</summary>
    public int? TryCaptureEpoch()
    {
        lock (_sync)
            return _activeTransitions == 0 ? _epoch : null;
    }

    public bool IsCurrent(int epoch)
    {
        lock (_sync)
            return _activeTransitions == 0 && _epoch == epoch;
    }

    // The epoch moves before the wait, so a holder stops at its next check instead of finishing its page.
    public async Task<IDisposable> BeginTransitionAsync()
    {
        lock (_sync)
        {
            _activeTransitions++;
            _epoch++;
        }

        // A sign-out raised from inside the holder (a token refresh failing mid-apply) would wait on itself.
        if (_flowHolder.Value is { } flow && ReferenceEquals(flow, Volatile.Read(ref _holder)))
            return new Releaser(EndTransition);

        try
        {
            await _lock.WaitAsync();
        }
        catch
        {
            EndTransition();
            throw;
        }

        return new Releaser(() =>
        {
            _lock.Release();
            EndTransition();
        });
    }

    // Not async, so the holder mark lands in the caller's execution context and flows into its callees.
    public Task<IDisposable> EnterWriteAsync()
    {
        var token = new object();
        _flowHolder.Value = token;
        return EnterWriteCoreAsync(token);
    }

    private async Task<IDisposable> EnterWriteCoreAsync(object token)
    {
        await _lock.WaitAsync();
        Volatile.Write(ref _holder, token);
        return new Releaser(() =>
        {
            Volatile.Write(ref _holder, null);
            _lock.Release();
        });
    }

    private void EndTransition()
    {
        lock (_sync)
            _activeTransitions--;
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release();
        }
    }
}
