using Microsoft.Extensions.Logging;

namespace Pia.Tests;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that records what was logged, for the fixtures that need to
/// assert on a log LINE rather than on a state change. Generic only: <c>ILogger&lt;T&gt;</c> derives from
/// <c>ILogger</c>, so this also satisfies a non-generic <c>ILogger</c> constructor parameter.
/// <para>
/// <c>IsEnabled</c> is always true so nothing is filtered out by level, and the entries are kept under a
/// lock and handed out as a snapshot - an agent run's continuations hop threadpool threads, so the list
/// must never be published live.
/// </para>
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_entries)
        {
            _entries.Add((logLevel, message, exception));
        }
    }
}
