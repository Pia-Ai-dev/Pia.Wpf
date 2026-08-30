using Microsoft.Extensions.Logging;

namespace Pia.Logging;

/// <summary>
/// T2-18 — makes <see cref="ILogger.BeginScope{TState}"/> VISIBLE in the support log, by prefixing every line a
/// wrapped provider writes with the scopes open on the writing async flow.
/// <para>
/// WHY THIS TYPE HAS TO EXIST: the file sink is <c>NReco.Logging.File</c>, whose optional scope support (1.4.0+)
/// arrives only through <c>ISupportExternalScope</c> on a provider MEL registered itself — and its
/// <c>FormatLogEntry</c> delegate is handed (category, timestamp, level, eventId, message, exception) with no
/// way to reach a scope. The sink is constructed and wrapped by hand here, so <c>BeginScope(runId)</c> compiles,
/// costs an allocation, and is DISCARDED before it reaches <c>pia-*.log</c> — the one file a user attaches to a
/// support request. Correlation that only exists in a debugger is not correlation.
/// </para>
/// <para>
/// WHY IT MATTERS NOW: T1-1 made the run pool a user setting and T1-2 made a wide pool safe, so two or more
/// unattended runs interleave their lines in one file as a matter of course. Without a scope, "Round 1/10
/// starting" belongs to no run.
/// </para>
/// <para>
/// WHY AN AsyncLocal STACK RATHER THAN MEL's <c>IExternalScopeProvider</c>: the framework only hands a scope
/// provider to providers that implement <c>ISupportExternalScope</c>, and the value a logger sees then depends on
/// which providers share it. This decorator owns its own ambient stack, so its behaviour is a property of THIS
/// type and does not change when a provider is added or removed. The stack is <b>static</b> on purpose: a scope
/// opened through <c>ILogger&lt;AgentRunOrchestrator&gt;</c> must also label the lines a tool handler writes
/// through its OWN category inside that flow — which is the entire point.
/// </para>
/// <para>
/// PRIVACY, and it is a rule for CALLERS: whatever a scope's state stringifies to lands in a RELEASE log verbatim.
/// A scope may carry IDs and ordinals ONLY — never a goal, a path, a tool argument or a file name. The
/// <c>SensitiveDebug</c> family (compile-time erased) remains the only way user content may be logged at all.
/// </para>
/// </summary>
public sealed class ScopeRenderingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;

    public ScopeRenderingLoggerProvider(ILoggerProvider inner) => _inner = inner;

    public ILogger CreateLogger(string categoryName) => new ScopeRenderingLogger(_inner.CreateLogger(categoryName));

    public void Dispose() => _inner.Dispose();

    /// <summary>
    /// The innermost scope on this async flow, or null. <see cref="AsyncLocal{T}"/>, so a scope opened before an
    /// <c>await</c> still labels what happens after it — and so two runs on two threads never see each other's.
    /// </summary>
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>One open scope. Immutable and linked to its parent, so a push allocates one node and a pop is a
    /// reference assignment; nothing has to be copied or unwound.</summary>
    private sealed class Scope
    {
        internal Scope(string text, Scope? parent)
        {
            Text = text;
            Parent = parent;
        }

        internal string Text { get; }
        internal Scope? Parent { get; }
    }

    /// <summary>
    /// Restores the value that was current when the scope was opened, rather than popping one node: an
    /// out-of-order dispose (which a <c>using</c> makes hard but not impossible) then cannot strand a stale
    /// scope on the flow forever.
    /// </summary>
    private sealed class ScopeHandle : IDisposable
    {
        private readonly Scope? _previous;
        private bool _disposed;

        internal ScopeHandle(Scope? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = _previous;
        }
    }

    /// <summary>
    /// The open scopes, outermost first, as one bracketed prefix — or null when there are none, in which case the
    /// line goes through completely untouched (that is the "no scope, no change" property every existing log line
    /// depends on).
    /// </summary>
    internal static string? RenderPrefix()
    {
        var scope = Current.Value;
        if (scope is null) return null;

        // Outermost first: the run comes before the step, which is how a person reads a log.
        var parts = new List<string>(4);
        for (var s = scope; s is not null; s = s.Parent)
            parts.Add(s.Text);
        parts.Reverse();

        return "[" + string.Join(" ", parts) + "] ";
    }

    private sealed class ScopeRenderingLogger : ILogger
    {
        private readonly ILogger _inner;

        internal ScopeRenderingLogger(ILogger inner) => _inner = inner;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            // ToString() is the contract: MEL's own FormattedLogValues renders "run {RunId}" + args into
            // "run <guid>", so a call site controls the text with an ordinary message template.
            var text = state.ToString();
            if (string.IsNullOrEmpty(text))
                return NullScope.Instance;

            var previous = Current.Value;
            Current.Value = new Scope(text, previous);
            return new ScopeHandle(previous);
        }

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var prefix = RenderPrefix();
            if (prefix is null)
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            // The STATE is passed through unchanged and only the formatted text is prefixed, so a structured sink
            // behind this decorator still sees the original values.
            _inner.Log(logLevel, eventId, state, exception, (s, e) => prefix + formatter(s, e));
        }
    }

    /// <summary>A scope that did nothing, for a state whose text is empty.</summary>
    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
