using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Gates the background-continuation flip: once two chats can run turns
/// concurrently, each must own its token map so chat A's <c>[Person_1]</c> never
/// detokenizes to chat B's value after B re-uses the counter-based namespace
/// (the cross-chat PII leak the plan calls out).
/// </summary>
public class TokenMapIsolationTests
{
    private static TokenMapService NewMap()
    {
        var pii = Substitute.For<IPiiDetector>();
        var memory = Substitute.For<IMemoryService>();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        memory.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
        return new TokenMapService(pii, memory, settings);
    }

    [Fact]
    public void TwoSessions_ShareNoNamespace_DespiteIdenticalTokenIds()
    {
        var sessionA = NewMap();
        var sessionB = NewMap();

        // Both sessions independently mint the SAME token id for DIFFERENT people.
        var tokenA = sessionA.Tokenize("Alice Anderson", "Person");
        var tokenB = sessionB.Tokenize("Bob Brown", "Person");
        Assert.Equal("[Person_1]", tokenA);
        Assert.Equal("[Person_1]", tokenB); // same id, different map

        // Each map resolves [Person_1] to ITS OWN value — no cross-contamination.
        Assert.Equal("Alice Anderson", sessionA.Detokenize("[Person_1]"));
        Assert.Equal("Bob Brown", sessionB.Detokenize("[Person_1]"));
    }

    [Fact]
    public void ClearAndReinit_OnOneSession_DoesNotPoisonAnother()
    {
        var background = NewMap();
        background.Tokenize("Alice Anderson", "Person"); // backgrounded turn's mapping

        // Simulate "New Chat" / re-init on a DIFFERENT (active) session's map.
        var active = NewMap();
        active.Clear();              // resets the active map's counters only
        active.Tokenize("Bob Brown", "Person");

        // The backgrounded session's [Person_1] still resolves to Alice (its own map
        // was never touched) — the leak the per-session map prevents.
        Assert.Equal("Alice Anderson", background.Detokenize("[Person_1]"));
        Assert.Equal("Bob Brown", active.Detokenize("[Person_1]"));
    }

    [Fact]
    public async Task TokenMapAmbient_IsolatesInterleavedLogicalFlows()
    {
        var mapA = NewMap();
        var mapB = NewMap();
        mapA.Tokenize("Alice Anderson", "Person");
        mapB.Tokenize("Bob Brown", "Person");

        ITokenMapService? seenByA = null;
        ITokenMapService? seenByB = null;

        // Two logical async flows interleaved on the same thread: AsyncLocal carries
        // each flow's own ambient value across awaits (this is the WaitingForTool-await
        // scenario in miniature).
        async Task FlowA()
        {
            TokenMapAmbient.Current = mapA;
            await Task.Yield();
            seenByA = TokenMapAmbient.Current;
        }

        async Task FlowB()
        {
            TokenMapAmbient.Current = mapB;
            await Task.Yield();
            seenByB = TokenMapAmbient.Current;
        }

        await Task.WhenAll(FlowA(), FlowB());

        Assert.Same(mapA, seenByA);
        Assert.Same(mapB, seenByB);
    }

    [Fact]
    public void TokenMapAmbient_DefaultsToNull_ForNonTurnCallers()
    {
        // Outside any RunTurnAsync flow the decorator must fall back to its scope map.
        TokenMapAmbient.Current = null;
        Assert.Null(TokenMapAmbient.Current);
    }
}
