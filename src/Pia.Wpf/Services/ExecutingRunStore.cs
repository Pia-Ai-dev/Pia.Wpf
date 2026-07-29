using System.Collections.Concurrent;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IExecutingRunStore"/>. ONE reverse map (run → chat) rather than a chat → set index,
/// because the run id is the only key both brackets and the <c>RunChanged</c> handler share: keying on it
/// makes <see cref="Release"/> a single idempotent operation, gives <see cref="GetChatId"/> for free, and
/// makes "a chat stays gated while a SECOND run on it is still bracketed" fall out of the data shape instead
/// of needing an empty-inner-set removal dance.
/// <para>
/// Every operation is a lock-free <see cref="ConcurrentDictionary{TKey,TValue}"/> call, so the UI thread
/// never waits on the run pool (A2's whole point). <see cref="IsExecuting"/> walks the live enumerator — not
/// <c>Values</c>, which snapshots under every bucket lock — and is linear in the number of OPEN brackets,
/// which the launcher's concurrency cap keeps to single digits. Nothing here can throw.
/// </para>
/// </summary>
public sealed class ExecutingRunStore : IExecutingRunStore
{
    private readonly ConcurrentDictionary<Guid, Guid> _chatByRun = new();

    public void Register(Guid chatId, Guid runId) => _chatByRun[runId] = chatId;

    public void Release(Guid runId) => _chatByRun.TryRemove(runId, out _);

    public Guid? GetChatId(Guid runId) => _chatByRun.TryGetValue(runId, out var chatId) ? chatId : null;

    public bool IsExecuting(Guid chatId)
    {
        foreach (var entry in _chatByRun)
        {
            if (entry.Value == chatId)
                return true;
        }

        return false;
    }
}
