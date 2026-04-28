using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class ConservativeCrossTalkResolverTests
{
    private static (ConservativeCrossTalkResolver sut, ConsentStateManager mgr) Build()
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var sut = new ConservativeCrossTalkResolver(mgr, NullLogger<ConservativeCrossTalkResolver>.Instance);
        return (sut, mgr);
    }

    private static void Grant(ConsentStateManager mgr, string label)
        => mgr.RecordClassification(label,
            new ConsentClassification(ConsentDecision.Grant, 1.0f),
            "ja", "hash", "prompt", "stt");

    private static void Deny(ConsentStateManager mgr, string label)
        => mgr.RecordClassification(label,
            new ConsentClassification(ConsentDecision.Deny, 1.0f),
            "nein", "hash", "prompt", "stt");

    [Fact]
    public void Empty_Drops()
    {
        var (sut, _) = Build();
        Assert.Equal(GateDecision.Drop, sut.Resolve(Array.Empty<string>()));
    }

    [Fact]
    public void SingleGranted_Passes()
    {
        var (sut, mgr) = Build();
        Grant(mgr, "Speaker 1");
        Assert.Equal(GateDecision.PassToTranscript, sut.Resolve(new[] { "Speaker 1" }));
    }

    [Fact]
    public void GrantedPlusDenied_Drops()
    {
        var (sut, mgr) = Build();
        Grant(mgr, "Speaker 1");
        Deny(mgr, "Speaker 2");
        Assert.Equal(GateDecision.Drop, sut.Resolve(new[] { "Speaker 1", "Speaker 2" }));
    }

    [Fact]
    public void TwoGranted_Passes()
    {
        var (sut, mgr) = Build();
        Grant(mgr, "Speaker 1");
        Grant(mgr, "Speaker 2");
        Assert.Equal(GateDecision.PassToTranscript, sut.Resolve(new[] { "Speaker 1", "Speaker 2" }));
    }
}
