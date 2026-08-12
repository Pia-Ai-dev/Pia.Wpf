using System;
using System.Collections.Generic;
using System.Text.Json;
using Pia.Shared.Operators;
using Xunit;

namespace Pia.Tests.Operators;

/// <summary>
/// Wire-shape coverage for the assignment contract. The options below deliberately carry NO
/// <c>DefaultIgnoreCondition</c>, and that omission is the entire point of this class: the server sets
/// <c>WhenWritingNull</c> globally, this side does not, and a test that configured the global would prove the
/// global works while saying nothing about whether the per-property attributes are present and correctly
/// targeted.
///
/// The specific way that goes wrong is silent. On a positional record an attribute written without a target
/// binds to the PARAMETER, where the serializer never looks — it compiles, it reviews clean, and the null is
/// emitted anyway. Only asserting the emitted JSON catches it, which is why these tests read strings rather
/// than the C# shape.
/// </summary>
public class AssignmentContractSerializationTests
{
    /// <summary>camelCase and case-insensitive, matching both sides' serializers — but no global
    /// null-omission, so every absence asserted below is the type's own doing.</summary>
    private static readonly JsonSerializerOptions AttributeOnlyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static AssignmentDto MinimalDto() => new(
        Guid.NewGuid(), "research", Mode: null, "Queued", 0, 0, 0,
        DateTime.UtcNow, DateTime.UtcNow, StartedAt: null, CompletedAt: null,
        ArtifactJson: null, ErrorCode: null, ErrorMessage: null);

    // ── The absence that carries meaning ──────────────────────────────────────

    /// <summary>
    /// The load-bearing one. <c>events</c> must be ABSENT on the list projection, not <c>null</c> and not an
    /// empty array: absent means "not loaded on this route", whereas an empty array would claim the assignment
    /// has no progress — which is never true, since a row always has the event written in the transaction that
    /// created it.
    /// </summary>
    [Fact]
    public void AssignmentDto_OmitsEventsEntirely_WhenNull()
    {
        var json = JsonSerializer.Serialize(MinimalDto(), AttributeOnlyOptions);

        Assert.DoesNotContain("events", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("startedAt")]
    [InlineData("completedAt")]
    [InlineData("artifactJson")]
    [InlineData("artifactText")]
    [InlineData("errorCode")]
    [InlineData("errorMessage")]
    [InlineData("plaintextDroppedAt")]
    public void AssignmentDto_OmitsEveryNullableMember_WhenNull(string wireName)
    {
        var json = JsonSerializer.Serialize(MinimalDto(), AttributeOnlyOptions);

        Assert.DoesNotContain(wireName, json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other half: omission must be conditional, not unconditional. A member the server DOES
    /// populate has to reach the wire, or the attribute is hiding data rather than nulls.</summary>
    [Fact]
    public void AssignmentDto_EmitsThoseMembers_WhenPopulated()
    {
        var dto = MinimalDto() with
        {
            Mode = "Research",
            ArtifactText = "the answer",
            PlaintextDroppedAt = DateTime.UtcNow,
            Events = new List<AssignmentEventDto>
            {
                new(Guid.NewGuid(), "queued", Message: null, DetailJson: null, DateTime.UtcNow),
            },
        };

        var json = JsonSerializer.Serialize(dto, AttributeOnlyOptions);

        Assert.Contains("\"mode\":\"Research\"", json, StringComparison.Ordinal);
        Assert.Contains("\"artifactText\":\"the answer\"", json, StringComparison.Ordinal);
        Assert.Contains("plaintextDroppedAt", json, StringComparison.Ordinal);
        Assert.Contains("\"events\":[", json, StringComparison.Ordinal);
        // The nested record's own attributes apply too — a nested type is where a missing target hides best.
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("detailJson", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An EMPTY events list is not the same as a null one and must be emitted — the single-row route
    /// always sends the array.</summary>
    [Fact]
    public void AssignmentDto_EmitsAnEmptyEventsArray_RatherThanOmittingIt()
    {
        var dto = MinimalDto() with { Events = Array.Empty<AssignmentEventDto>() };

        Assert.Contains("\"events\":[]", JsonSerializer.Serialize(dto, AttributeOnlyOptions), StringComparison.Ordinal);
    }

    // ── The envelope ──────────────────────────────────────────────────────────

    [Fact]
    public void AssignmentInput_RoundTrips_WithItemsAndOmittedOptionals()
    {
        var entityId = Guid.NewGuid();
        var input = new AssignmentInput(
            AssignmentInput.CurrentSchemaVersion,
            "Summarise these",
            new List<AssignmentInputItem>
            {
                new(AssignmentInputEntityTypes.Memory, entityId, Title: null, "remembered text", UpdatedAt: null),
            });

        var json = JsonSerializer.Serialize(input, AttributeOnlyOptions);
        Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updatedAt", json, StringComparison.OrdinalIgnoreCase);

        var back = JsonSerializer.Deserialize<AssignmentInput>(json, AttributeOnlyOptions);
        Assert.NotNull(back);
        Assert.Equal(AssignmentInput.CurrentSchemaVersion, back!.SchemaVersion);
        Assert.Equal("Summarise these", back.Prompt);
        var item = Assert.Single(back.Items);
        Assert.Equal(AssignmentInputEntityTypes.Memory, item.EntityType);
        Assert.Equal(entityId, item.EntityId);
        Assert.Null(item.Title);
        Assert.Null(item.UpdatedAt);
    }

    /// <summary>A body with no <c>schemaVersion</c> binds to 0, never to the current version. That is what
    /// lets the server refuse it instead of silently treating an unversioned body as v1.</summary>
    [Fact]
    public void AssignmentInput_MissingSchemaVersion_DoesNotDefaultToCurrent()
    {
        var input = JsonSerializer.Deserialize<AssignmentInput>(
            """{"prompt":"hi","items":[]}""", AttributeOnlyOptions);

        Assert.NotNull(input);
        Assert.NotEqual(AssignmentInput.CurrentSchemaVersion, input!.SchemaVersion);
    }

    // ── The vocabulary ────────────────────────────────────────────────────────

    /// <summary>Exact and ordinal. A closed vocabulary that normalises casing hides the caller's mistake
    /// instead of surfacing it, and this one is a security boundary.</summary>
    [Theory]
    [InlineData("assistantChat", true)]
    [InlineData("template", true)]
    [InlineData("AssistantChat", false)]
    [InlineData("assistantchat", false)]
    [InlineData("provider", false)]
    [InlineData("kanbanColumn", false)]
    [InlineData("researchSession", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EntityTypes_AreMatchedExactly(string? candidate, bool known) =>
        Assert.Equal(known, AssignmentInputEntityTypes.IsKnown(candidate));

    /// <summary>Pins the membership itself, so widening the boundary cannot happen as a quiet edit — the
    /// vocabulary is user-authored content only, and credential- or config-bearing families stay out.</summary>
    [Fact]
    public void EntityTypes_ContainExactlyTheContentFamilies() =>
        Assert.Equal(
            new[] { "assistantChat", "memory", "session", "template", "todo" },
            new SortedSet<string>(AssignmentInputEntityTypes.All, StringComparer.Ordinal));

    [Fact]
    public void AssignmentSkill_EmptyDeclaredInputTypes_SerializesAsAnEmptyArray_NotAbsent()
    {
        var skill = new AssignmentSkill("invoices", "Invoices", "Assistant", Array.Empty<string>());

        // Absent would let a client read "unknown, offer everything" — the inversion of the gate. Empty is a
        // real answer: this skill takes a prompt alone.
        Assert.Contains("\"declaredInputTypes\":[]", JsonSerializer.Serialize(skill, AttributeOnlyOptions), StringComparison.Ordinal);
    }
}
