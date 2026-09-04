using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// The relay exists so the "only on change" rule is testable without a capture source or a VAD
/// model. A hypothesis is re-read every audio frame and is usually identical to the last one.
/// </summary>
public class PartialHypothesisRelayTests
{
    [Fact]
    public void An_unchanged_hypothesis_does_not_fire_again()
    {
        var seen = new List<string>();
        var relay = new PartialHypothesisRelay(seen.Add);

        relay.Offer("guten");
        relay.Offer("guten");
        relay.Offer("guten Morgen");

        Assert.Equal(["guten", "guten Morgen"], seen);
    }

    [Fact]
    public void Clear_emits_empty_once_and_then_stays_quiet()
    {
        var seen = new List<string>();
        var relay = new PartialHypothesisRelay(seen.Add);

        relay.Offer("guten Morgen");
        relay.Clear();
        relay.Clear();

        Assert.Equal(["guten Morgen", ""], seen);
    }

    [Fact]
    public void A_null_hypothesis_is_treated_as_empty()
    {
        var seen = new List<string>();
        var relay = new PartialHypothesisRelay(seen.Add);

        relay.Offer(null!);

        Assert.Empty(seen);
    }
}
