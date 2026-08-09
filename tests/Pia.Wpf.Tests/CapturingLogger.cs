using Microsoft.Extensions.Logging;

namespace Pia.Tests;

/// <summary>Entries are handed out as a locked snapshot because an agent run's continuations hop threadpool threads.</summary>
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
