using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Integration.Compaction;

// No Microsoft.Agents.AI.Compaction type appears here: every assertion goes through AgentContextCompactor, which
// is both the containment rule and the guarantee that this measures the shipped path.
public class SyntheticTranscriptTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    private static readonly AgentContextBudget SmallWindow = new(8_000, 2_000);

    [Fact]
    public void Build_IsDeterministic_ForTheSameSeed()
    {
        var options = new SyntheticTranscriptOptions();

        var first = SyntheticTranscript.Build(options);
        var second = SyntheticTranscript.Build(options);

        Assert.Equal("synthetic-chat-tool-light-20260822-40", first.Id);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Messages.Count, second.Messages.Count);

        for (var i = 0; i < first.Messages.Count; i++)
        {
            Assert.Equal(first.Messages[i].Role, second.Messages[i].Role);
            Assert.Equal(first.Messages[i].Text, second.Messages[i].Text);
        }

        // Fails the moment Random.Shared, DateTime.Now or Guid.NewGuid creeps into the generator.
        var reseeded = SyntheticTranscript.Build(options with { Seed = options.Seed + 1 });
        Assert.NotEqual(first.Fingerprint, reseeded.Fingerprint);
    }

    [Fact]
    public void EveryPlantedAnswer_AppearsExactlyOnceInTheTranscript()
    {
        foreach (var shape in Enum.GetValues<SyntheticTranscriptShape>())
        {
            var transcript = SyntheticTranscript.Build(new SyntheticTranscriptOptions { Shape = shape });
            var trace = SyntheticTranscript.Trace(transcript.Messages);

            foreach (var fact in transcript.Facts)
            {
                var occurrences = SyntheticTranscript.CountOccurrences(trace, fact.Answer);

                // A fact restated later is answerable without the removed region, so it measures luck.
                Assert.True(
                    occurrences == 1,
                    $"{shape}/{fact.Id} must appear exactly once, but appeared {occurrences} times");
            }
        }
    }

    [Fact]
    public void NoPlantedFact_LandsOnAMessageThatCompactionPins()
    {
        foreach (var shape in Enum.GetValues<SyntheticTranscriptShape>())
        {
            var transcript = SyntheticTranscript.Build(new SyntheticTranscriptOptions { Shape = shape });

            var systemCount = 0;
            while (systemCount < transcript.Messages.Count
                && transcript.Messages[systemCount].Role == ChatRole.System)
            {
                systemCount++;
            }

            foreach (var fact in transcript.Facts)
            {
                Assert.True(
                    fact.MessageIndex > systemCount,
                    $"{shape}/{fact.Id} landed at {fact.MessageIndex}, inside the pinned leading system run of {systemCount} plus the run goal");

                Assert.NotEqual(transcript.Messages.Count - 1, fact.MessageIndex);
                Assert.DoesNotContain(transcript.Messages[fact.MessageIndex].Contents, c => c is DataContent);
            }
        }
    }

    [Fact]
    public async Task Build_ProducesATranscriptThatActuallyCompacts()
    {
        foreach (var shape in Enum.GetValues<SyntheticTranscriptShape>())
        {
            var transcript = SyntheticTranscript.Build(new SyntheticTranscriptOptions { Shape = shape });

            var result = await AgentContextCompactor.CompactAsync(
                transcript.Messages, SmallWindow, Logger, TestContext.Current.CancellationToken);

            Assert.True(
                result.Count < transcript.Messages.Count,
                $"the {shape} fixture must be over budget or it proves nothing, but {transcript.Messages.Count} messages came back as {result.Count}");
        }
    }

    [Fact]
    public async Task Compaction_EvictsAtLeastOnePlantedFact_Entirely()
    {
        var transcript = SyntheticTranscript.Build(new SyntheticTranscriptOptions());

        var result = await AgentContextCompactor.CompactAsync(
            transcript.Messages, SmallWindow, Logger, TestContext.Current.CancellationToken);

        var retained = SyntheticTranscript.Trace(result);
        var evicted = transcript.Facts
            .Where(f => !retained.Contains(f.Answer, StringComparison.Ordinal))
            .ToList();

        // Nothing on this path summarizes, so an evicted fact leaves no trace in text, tool arguments or
        // tool results — the removed set is measurable because the loss is total.
        Assert.True(
            evicted.Count > 0,
            $"compaction must remove at least one planted fact or the corpus measures nothing, but all {transcript.Facts.Count} survived");
    }

    [Fact]
    public void ToolHeavyShape_NeverOrphansAToolCall()
    {
        var transcript = SyntheticTranscript.Build(
            new SyntheticTranscriptOptions { Shape = SyntheticTranscriptShape.ChatToolHeavy });

        var callIds = transcript.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet();
        var resultIds = transcript.Messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(c => c.CallId)
            .ToHashSet();

        // A malformed fixture makes every arm's request a provider 400 and the numbers mean nothing.
        Assert.NotEmpty(callIds);
        Assert.Equal(callIds, resultIds);
    }

    [Fact]
    public void Filler_ContainsNoDigitsOrPathSeparators()
    {
        var rng = new Random(20260822);

        for (var i = 0; i < 8; i++)
            Assert.Matches("^[a-z .]+$", SyntheticTranscript.Filler(rng, 200));
    }

    [Fact]
    public void AgentRunWithImageShape_CarriesExactlyOneFusedImageTurn()
    {
        var options = new SyntheticTranscriptOptions { Shape = SyntheticTranscriptShape.AgentRunWithImage };

        var first = SyntheticTranscript.Build(options);
        var second = SyntheticTranscript.Build(options);

        // Filtered with plain LINQ so no Enumerable.Any() lands inside an assertion argument.
        var imageTurns = first.Messages.Where(m => m.Contents.OfType<DataContent>().Any()).ToList();
        var fused = Assert.Single(imageTurns);
        Assert.Contains(fused.Contents, c => c is TextContent);

        var image = fused.Contents.OfType<DataContent>().Single();
        Assert.Equal("image/png", image.MediaType);

        var replayed = second.Messages
            .SelectMany(m => m.Contents.OfType<DataContent>())
            .Single();

        Assert.True(
            image.Data.Span.SequenceEqual(replayed.Data.Span),
            $"the image bytes must be seeded, but {image.Data.Length} bytes differed from the replayed {replayed.Data.Length}");
    }
}
