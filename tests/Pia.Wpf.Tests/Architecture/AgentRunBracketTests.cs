using System.Reflection;
using Pia.Services.Interfaces;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

/// <summary>
/// A2's BRACKET PREMISE. The executing-run index is populated from launch brackets, not from run state, so the
/// whole design rests on every path that actually EXECUTES an agent run also registering it. A future executor
/// that forgot would under-report SILENTLY: the composer would stay live for a chat a headless run is writing —
/// the exact data-loss window A2 closes — and no behavioural test would fail, because nothing observable
/// changes until a user happens to Send in that window. This is that test.
/// </summary>
public class AgentRunBracketTests
{
    /// <summary>
    /// The execution entry points. BOTH are required, and the second is the one that matters most: it is
    /// <c>RunShape.SingleTurn</c>, which before A2 was gated NOWHERE and — unlike the Planned path — had no
    /// <c>SaveMergedAsync</c> to heal a clobbered transcript.
    /// </summary>
    private static readonly Type[] ExecutorContracts =
    [
        typeof(IHeadlessRunLauncher),
        typeof(IBackgroundAssistantTurnRunner),
    ];

    /// <summary>
    /// Keyed on IMPLEMENTING an executor contract rather than on depending on one, which is the distinction
    /// that makes this rule mean something. An earlier form asked "does this type reference the orchestrator or
    /// the runner interface", and that was wrong in both directions: it MISSED
    /// <c>BackgroundAssistantTurnRunner</c> (it never references <c>AgentRunOrchestrator</c>, so the SingleTurn
    /// bracket sat outside the very rule meant to pin it), and it FLAGGED
    /// <c>ScheduledJobBackgroundService</c>, which only dispatches BY DELEGATION to the launcher and the runner
    /// and correctly owns no bracket of its own.
    /// </summary>
    [Fact]
    public void EveryAgentRunExecutor_MustOwnTheExecutingRunBracket()
    {
        var executors = PiaAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => ExecutorContracts.Any(c => c.IsAssignableFrom(t)))
            .ToList();

        // Anti-vacuity: HeadlessRunLauncher and BackgroundAssistantTurnRunner are both present today, so a
        // rule that finds fewer than two has stopped matching and would pass without asserting anything.
        Assert.True(executors.Count >= 2,
            "the executor scan must find both production executors, but it found: "
            + string.Join(", ", executors.Select(t => t.Name)));

        var unbracketed = executors
            .Where(t => !IsInjectedWithTheBracketStore(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(unbracketed.Count == 0,
            "a type that EXECUTES an agent run must take IExecutingRunStore and bracket the run, or an "
            + "activation of that run's chat cannot know a foreign writer owns the transcript. "
            + $"Unbracketed: {string.Join(", ", unbracketed)}. "
            + $"Executors seen: {string.Join(", ", executors.Select(t => t.Name).OrderBy(n => n))}");
    }

    /// <summary>
    /// Constructor injection is the check, because it is the only thing a test can assert cheaply AND is a
    /// genuine precondition: a type cannot bracket a run without a reference to the store. It does not prove
    /// the type CALLS <c>Register</c>/<c>Release</c> — that is what the behavioural tests
    /// (<c>LaunchedRun_HoldsTheComposerBracket_…</c>, <c>SingleTurnRun_HoldsTheComposerBracket_…</c>) cover, and
    /// each was verified to go red when its bracket is removed. Together they cover both halves; neither alone
    /// does.
    /// </summary>
    private static bool IsInjectedWithTheBracketStore(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IExecutingRunStore)));
}
