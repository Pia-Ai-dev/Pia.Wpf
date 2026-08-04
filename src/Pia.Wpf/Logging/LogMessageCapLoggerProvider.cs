using Microsoft.Extensions.Logging;

namespace Pia.Logging;

/// <summary>
/// T2-18 — a RELEASE-ONLY cap on how long one log line may be, as DEFENCE IN DEPTH against a payload reaching
/// the support log. It is a backstop, and it is deliberately the least clever thing that could work.
/// <para>
/// <b>It does not fix "no redaction today", because that claim is false.</b>
/// <c>17-trust-model.md</c> §4 records the mechanism that is actually load-bearing: user content leaves release
/// logs BY COMPILATION, not by level. Tool results, tool arguments, prompts and memory text are logged only
/// through the <c>Sensitive*</c> family, which is <c>[Conditional("DEBUG")]</c> — the call and its argument
/// evaluation are erased from the release IL, so there is no string to redact at runtime. hermes's own
/// <c>RedactingFormatter</c> is the weaker design, and this type must not be mistaken for it.
/// </para>
/// <para>
/// WHAT IT IS FOR, then: the residual case that erasure cannot cover — a line that is NOT
/// <c>Sensitive*</c>-gated and carries a payload anyway. A mistake in our own code, or a third-party library
/// logging a response body. Against that, the only content-AGNOSTIC bound is LENGTH: metadata lines are short,
/// dumped payloads are not. No pattern matching, no entropy heuristics, nothing a caller can defeat by
/// reformatting — and nothing that can silently mangle a legitimate line either, because the cap is far above
/// anything this codebase writes on purpose.
/// </para>
/// <para>
/// THREE things it deliberately does NOT touch:
/// </para>
/// <list type="bullet">
/// <item>The EXCEPTION. It is passed through untouched, so a stack trace is never truncated — the formatter this
/// caps returns the message only (MEL's message formatter ignores the exception argument; the sink appends the
/// exception itself).</item>
/// <item>DEBUG builds, where the default cap is unlimited. A developer's log is a diagnosis tool, the
/// <c>Sensitive*</c> lines only exist there in the first place, and truncating the thing being diagnosed would be
/// the opposite of useful.</item>
/// <item>The STATE. Only the formatted text is capped, so a structured sink behind this still sees the original
/// values.</item>
/// </list>
/// <para>
/// It is a SEPARATE decorator from <see cref="ScopeRenderingLoggerProvider"/> rather than another branch inside
/// it, so each type is named for the one thing it does. Composed scope-outside-cap in <c>Bootstrapper</c>: the
/// scope prefix is therefore inside the cap and survives truncation, because truncation keeps the HEAD of the
/// line — a capped line still says which run it belongs to.
/// </para>
/// </summary>
public sealed class LogMessageCapLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// The release cap. Two thousand characters is roughly 25 lines of prose: an order of magnitude above the
    /// longest deliberate line in this codebase (the tool-loop and compaction diagnostics), and an order of
    /// magnitude below a dumped file or an HTTP body. A number chosen to be uninteresting on purpose — if it ever
    /// starts truncating something real, the line was already telling the log too much.
    /// </summary>
    public const int ReleaseCapChars = 2000;

#if DEBUG
    /// <summary>DEBUG: no cap. See the type remarks.</summary>
    public const int DefaultCapChars = int.MaxValue;
#else
    /// <summary>RELEASE: <see cref="ReleaseCapChars"/>.</summary>
    public const int DefaultCapChars = ReleaseCapChars;
#endif

    private readonly ILoggerProvider _inner;
    private readonly int _capChars;

    /// <param name="capChars">Defaults to <see cref="DefaultCapChars"/>, i.e. the build's own answer. Explicit
    /// only in tests, which must be able to exercise truncation in EITHER configuration — a fact that only ran in
    /// Release would be a fact nobody runs.</param>
    public LogMessageCapLoggerProvider(ILoggerProvider inner, int capChars = DefaultCapChars)
    {
        _inner = inner;
        _capChars = Math.Max(1, capChars);
    }

    public ILogger CreateLogger(string categoryName) => new CappingLogger(_inner.CreateLogger(categoryName), _capChars);

    public void Dispose() => _inner.Dispose();

    /// <summary>
    /// Caps <paramref name="message"/>, keeping the head and stating how much was withheld. Internal so the
    /// rule can be asserted directly rather than only through a logger.
    /// </summary>
    internal static string Cap(string message, int capChars) =>
        message.Length <= capChars
            ? message
            : message[..capChars] + $"… [+{message.Length - capChars} chars withheld from the release log]";

    private sealed class CappingLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly int _capChars;

        internal CappingLogger(ILogger inner, int capChars)
        {
            _inner = inner;
            _capChars = capChars;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (_capChars == int.MaxValue)
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            // The exception is handed on untouched; only the formatted message passes through Cap.
            _inner.Log(logLevel, eventId, state, exception, (s, e) => Cap(formatter(s, e), _capChars));
        }
    }
}
