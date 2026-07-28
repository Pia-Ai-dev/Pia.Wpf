using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

// Covers PreserveDeviceLocalProviderFields, which stops a sync pull from silently resetting the six
// AiProvider fields SyncProvider deliberately omits. FromSyncProvider builds a FRESH AiProvider from the
// DTO, so before the preserve every omitted field came back as its C# default: the compaction budget went
// to null (turning agent context compaction OFF for that provider) and SupportsStreaming went back to its
// `true` default, re-enabling streaming a user had switched off.
//
// Same lesson as SyncMapperModeDefaultsMergeTests: a pull must merge, not wholesale-replace.
public class SyncMapperProviderDeviceLocalTests
{
    private static SyncMapper Make()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        return new SyncMapper(dpapi);
    }

    // A provider row as this device has it: every device-local field set to a NON-default value, so a
    // reset to defaults is unambiguously detectable.
    private static AiProvider LocalRow() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "local",
        Endpoint = "https://example.invalid/v1",
        ProviderType = AiProviderType.OpenAICompatible,
        SupportsStreaming = false,          // non-default: the default is true
        MaxContextWindowTokens = 128_000,
        MaxOutputTokens = 16_384,
        ReasoningEffort = ReasoningEffort.High,
        EnableWebSearch = true,             // non-default: the default is false
        MistralAgentId = "agent-local",
    };

    private static SyncProvider WireRow() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "from-server",
        ProviderType = (int)AiProviderType.OpenAICompatible,
        Endpoint = "https://example.invalid/v1",
        SupportsToolCalling = true,
        TimeoutSeconds = 300,
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        UpdatedAt = DateTime.UtcNow,
    };

    // The regression itself: what a pull produces before any preserve runs.
    [Fact]
    public void FromSyncProvider_alone_loses_every_device_local_field()
    {
        var mapped = Make().FromSyncProvider(WireRow());

        Assert.Null(mapped.MaxContextWindowTokens);
        Assert.Null(mapped.MaxOutputTokens);
        Assert.True(mapped.SupportsStreaming);      // reverted to the default
        Assert.Null(mapped.ReasoningEffort);
        Assert.False(mapped.EnableWebSearch);
        Assert.Null(mapped.MistralAgentId);
    }

    [Fact]
    public void Preserve_restores_the_compaction_budget_a_pull_would_have_nulled()
    {
        var mapper = Make();
        var existing = LocalRow();
        var incoming = mapper.FromSyncProvider(WireRow());

        mapper.PreserveDeviceLocalProviderFields(incoming, existing);

        Assert.Equal(128_000, incoming.MaxContextWindowTokens);
        Assert.Equal(16_384, incoming.MaxOutputTokens);
    }

    [Fact]
    public void Preserve_restores_all_six_device_local_fields()
    {
        var mapper = Make();
        var existing = LocalRow();
        var incoming = mapper.FromSyncProvider(WireRow());

        mapper.PreserveDeviceLocalProviderFields(incoming, existing);

        Assert.False(incoming.SupportsStreaming);
        Assert.Equal(128_000, incoming.MaxContextWindowTokens);
        Assert.Equal(16_384, incoming.MaxOutputTokens);
        Assert.Equal(ReasoningEffort.High, incoming.ReasoningEffort);
        Assert.True(incoming.EnableWebSearch);
        Assert.Equal("agent-local", incoming.MistralAgentId);
    }

    // The preserve must not become a general "local wins" merge — everything the DTO DOES carry has to
    // keep coming from the server, or the pull stops applying remote edits.
    [Fact]
    public void Preserve_leaves_every_synced_field_on_the_incoming_value()
    {
        var mapper = Make();
        var existing = LocalRow();
        var incoming = mapper.FromSyncProvider(WireRow());

        mapper.PreserveDeviceLocalProviderFields(incoming, existing);

        Assert.Equal("from-server", incoming.Name);
        Assert.Equal("https://example.invalid/v1", incoming.Endpoint);
        Assert.Equal(AiProviderType.OpenAICompatible, incoming.ProviderType);
        Assert.Equal(300, incoming.TimeoutSeconds);
        Assert.True(incoming.SupportsToolCalling);
    }

    // A null budget on this device must stay null: the preserve carries the local value whatever it is,
    // it does not resurrect a value from the wire (there is none) or invent a default.
    [Fact]
    public void Preserve_carries_a_null_local_budget_as_null()
    {
        var mapper = Make();
        var existing = LocalRow();
        existing.MaxContextWindowTokens = null;
        existing.MaxOutputTokens = null;
        var incoming = mapper.FromSyncProvider(WireRow());

        mapper.PreserveDeviceLocalProviderFields(incoming, existing);

        Assert.Null(incoming.MaxContextWindowTokens);
        Assert.Null(incoming.MaxOutputTokens);
    }

    // Round-trip guard: pushing then pulling this device's own row must not change its own budget.
    // Before the preserve, a push/pull cycle against an unchanged server row silently disabled compaction.
    [Fact]
    public void Push_then_pull_of_our_own_row_keeps_compaction_configured()
    {
        var mapper = Make();
        var existing = LocalRow();

        var pushed = mapper.ToSyncProvider(existing);
        var pulled = mapper.FromSyncProvider(pushed);
        mapper.PreserveDeviceLocalProviderFields(pulled, existing);

        Assert.Equal(existing.MaxContextWindowTokens, pulled.MaxContextWindowTokens);
        Assert.Equal(existing.MaxOutputTokens, pulled.MaxOutputTokens);
        Assert.Equal(existing.SupportsStreaming, pulled.SupportsStreaming);
    }

    // Pins the design decision: these fields are preserved, NOT put on the wire. If someone later adds
    // them to SyncProvider, one machine's tuning would start governing another's runs — this fails first.
    [Fact]
    public void The_budget_is_still_absent_from_the_wire_dto()
    {
        var names = typeof(SyncProvider).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(nameof(AiProvider.MaxContextWindowTokens), names);
        Assert.DoesNotContain(nameof(AiProvider.MaxOutputTokens), names);
        Assert.DoesNotContain(nameof(AiProvider.SupportsStreaming), names);
        Assert.DoesNotContain(nameof(AiProvider.ReasoningEffort), names);
    }

    // Guard against the list going stale: if a new device-local field is added to AiProvider and left out
    // of both SyncProvider and the preserve, the next pull silently resets it. This test names the exact
    // set that is known-omitted-and-preserved, so adding a field forces a decision here.
    [Fact]
    public void Every_field_the_dto_omits_is_either_preserved_or_deliberately_excluded()
    {
        var wireNames = typeof(SyncProvider).GetProperties().Select(p => p.Name).ToHashSet();

        // Carried by the preserve.
        var preserved = new[]
        {
            nameof(AiProvider.SupportsStreaming),
            nameof(AiProvider.MaxContextWindowTokens),
            nameof(AiProvider.MaxOutputTokens),
            nameof(AiProvider.ReasoningEffort),
            nameof(AiProvider.EnableWebSearch),
            nameof(AiProvider.MistralAgentId),
        };

        // Omitted from the DTO on purpose and handled elsewhere:
        // EncryptedApiKey is preserved conditionally by ProviderService.UpdateProviderAsync (an E2EE pull
        // can carry a real rotated key, so it must not be clobbered unconditionally).
        var handledElsewhere = new[] { nameof(AiProvider.EncryptedApiKey) };

        var unaccounted = typeof(AiProvider).GetProperties()
            .Select(p => p.Name)
            .Where(n => !wireNames.Contains(n))
            .Where(n => !preserved.Contains(n))
            .Where(n => !handledElsewhere.Contains(n))
            .ToList();

        Assert.Empty(unaccounted);
    }
}
