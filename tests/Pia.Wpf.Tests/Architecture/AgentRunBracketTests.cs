using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

/// <summary>
/// A2's BRACKET PREMISE. The executing-run index is populated from launch brackets, not from run state, so
/// the whole design rests on every code path that dispatches an agent run also registering it. A future
/// third dispatcher that forgot would under-report SILENTLY: the composer would stay live for a chat a
/// headless run is writing — the exact data-loss window A2 closes — and no behavioural test would fail,
/// because nothing observable changes until a user happens to Send in that window. This is that test.
/// </summary>
public class AgentRunBracketTests
{
    private const string Orchestrator = "Pia.Services.AgentRunOrchestrator";
    private const string BracketStore = "Pia.Services.Interfaces.IExecutingRunStore";

    [Fact]
    public void EveryTypeThatDispatchesAnAgentRun_MustAlsoOwnTheExecutingRunBracket()
    {
        var bracketOwners = TopLevelTypesDependingOn(BracketStore);
        var dispatchers = TopLevelTypesDependingOn(Orchestrator)
            // A type is not its own dispatcher. AgentRunOrchestrator names itself (ILogger<T>, its own async
            // state machines), and NetArchTest's dependency search reads method bodies and generic arguments,
            // so it may or may not report that self-edge. Excluding it makes the rule independent of which
            // way that goes instead of silently depending on it.
            .Where(t => t.FullName != Orchestrator)
            .ToHashSet();

        // Guards against the rule passing vacuously if the dependency search ever stops matching: today
        // Bootstrapper, HeadlessRunLauncher and ChatSessionManager all reach AgentRunOrchestrator.
        Assert.NotEmpty(dispatchers);

        var unbracketed = dispatchers
            .Where(t => !bracketOwners.Contains(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(unbracketed.Count == 0,
            "a type that dispatches an agent run must also register it with IExecutingRunStore (A2's launch "
            + "bracket), or an activation of that run's chat cannot know a foreign writer owns the transcript. "
            + $"Unbracketed: {string.Join(", ", unbracketed)}. "
            // Both sets are printed so a failure is self-diagnosing: if the offender is a type nobody would
            // call a dispatcher, this is a NetArchTest artefact (add it to the filter above), not a real gap.
            + $"All dispatchers seen: {string.Join(", ", dispatchers.Select(t => t.Name).OrderBy(n => n))}. "
            + $"All bracket owners seen: {string.Join(", ", bracketOwners.Select(t => t.Name).OrderBy(n => n))}");
    }

    /// <summary>
    /// Both sides are normalised to the TOP-LEVEL declaring type on purpose: the launcher resolves the
    /// orchestrator inside an async dispatch lambda, so the reference may be attributed to a compiler-
    /// generated state machine. Comparing declaring types makes the rule hold whichever way the dependency
    /// search treats those, instead of quietly depending on it.
    /// </summary>
    private static HashSet<Type> TopLevelTypesDependingOn(string dependency) =>
        Types.InAssembly(PiaAssembly)
            .That().HaveDependencyOn(dependency)
            .GetTypes()
            .Select(TopLevel)
            .ToHashSet();

    private static Type TopLevel(Type type)
    {
        while (type.DeclaringType is { } declaring)
            type = declaring;
        return type;
    }
}
