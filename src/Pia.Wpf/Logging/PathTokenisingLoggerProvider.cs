using System.Text;
using Microsoft.Extensions.Logging;

namespace Pia.Logging;

/// <summary>A profile root and the environment variable that names it.</summary>
public sealed record ProfileRootToken(string Root, string Token);

/// <summary>
/// Rewrites the profile roots in a log message to <c>%APPDATA%</c>-style tokens, so the <c>pia-*.log</c> a user
/// attaches to a support request carries no account name. Unlike <see cref="SafeUrl"/> this runs in DEBUG too — a
/// build split would mean no developer ever sees the shape a user's log actually has.
/// </summary>
public sealed class PathTokenisingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;
    private readonly IReadOnlyList<ProfileRootToken> _roots;

    /// <param name="roots">Injected rather than read here, so a test can scrub a profile it is not running on.</param>
    public PathTokenisingLoggerProvider(ILoggerProvider inner, IReadOnlyList<ProfileRootToken> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        _inner = inner;
        _roots = Ordered(roots);
    }

    /// <summary>Longest first, so AppData\Local wins over the user profile that contains it.</summary>
    internal static IReadOnlyList<ProfileRootToken> Ordered(IReadOnlyList<ProfileRootToken> roots) =>
        [.. roots.Where(r => !string.IsNullOrWhiteSpace(r.Root)).OrderByDescending(r => r.Root.Length)];

    public ILogger CreateLogger(string categoryName) =>
        new TokenisingLogger(_inner.CreateLogger(categoryName), _roots);

    public void Dispose() => _inner.Dispose();

    /// <summary>Internal so the rule can be asserted directly rather than only through a logger.</summary>
    internal static string Tokenise(string message, IReadOnlyList<ProfileRootToken> roots)
    {
        foreach (var root in roots)
            foreach (var form in SeparatorForms(root.Root))
                message = ReplaceRoot(message, form, root.Token);

        return message;
    }

    // Bounded like the export's own keys: a root must not also eat C:\Users\adalovelace.
    private static string ReplaceRoot(string message, string form, string token)
    {
        var builder = new StringBuilder(message.Length);
        var cursor = 0;

        while (message.IndexOf(form, cursor, StringComparison.OrdinalIgnoreCase) is var hit and >= 0)
        {
            var after = hit + form.Length;
            var bounded = (hit == 0 || !char.IsLetterOrDigit(message[hit - 1]))
                && (after == message.Length || !char.IsLetterOrDigit(message[after]));

            builder.Append(message, cursor, hit - cursor)
                .Append(bounded ? token : message.Substring(hit, form.Length));
            cursor = after;
        }

        return cursor == 0 ? message : builder.Append(message, cursor, message.Length - cursor).ToString();
    }

    /// <summary>The escaped form first: a JSON-serialised tool argument carries the root with doubled separators.</summary>
    private static IEnumerable<string> SeparatorForms(string root)
    {
        yield return root.Replace("\\", "\\\\");
        yield return root;

        var flipped = root.Replace('\\', '/');
        if (!string.Equals(flipped, root, StringComparison.Ordinal))
            yield return flipped;
    }

    private sealed class TokenisingLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly IReadOnlyList<ProfileRootToken> _roots;

        internal TokenisingLogger(ILogger inner, IReadOnlyList<ProfileRootToken> roots)
        {
            _inner = inner;
            _roots = roots;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (_roots.Count == 0)
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            // The exception is handed on untouched: NReco renders it itself, so a stack frame's build-time source
            // path is out of reach from here and stays a csproj concern.
            _inner.Log(logLevel, eventId, state, exception, (s, e) => Tokenise(formatter(s, e), _roots));
        }
    }
}
