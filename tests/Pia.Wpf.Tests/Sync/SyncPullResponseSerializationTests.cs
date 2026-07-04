using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using Xunit;

namespace Pia.Tests.Sync;

/// <summary>
/// Round-trip coverage for the additive <see cref="SyncPullResponse"/> members
/// (<see cref="SyncPullResponse.CatalogVersion"/>, <see cref="SyncPullResponse.PendingDevices"/>,
/// <see cref="SyncPullResponse.HasMore"/>). The shared client/server serializer introduced by a
/// downstream unit should extend this coverage rather than replace it.
/// </summary>
public class SyncPullResponseSerializationTests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Serialize_OmitsNewNullableMembers_WhenNull()
    {
        var response = new SyncPullResponse { ServerTimestamp = DateTime.UtcNow };

        var json = JsonSerializer.Serialize(response, WireOptions);

        Assert.DoesNotContain("catalogVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pendingDevices", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasMore", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_CamelCasePayload_PopulatesNewMembers()
    {
        var deviceId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var json = $$"""
            {
                "serverTimestamp": "2026-07-04T00:00:00Z",
                "catalogVersion": 42,
                "hasMore": true,
                "pendingDevices": [
                    { "id": "{{deviceId}}", "name": "Marco's Laptop", "createdAt": "{{createdAt:O}}" }
                ]
            }
            """;

        var response = JsonSerializer.Deserialize<SyncPullResponse>(json, WireOptions);

        Assert.NotNull(response);
        Assert.Equal(42, response!.CatalogVersion);
        Assert.True(response.HasMore);
        var pendingDevice = Assert.Single(response.PendingDevices!);
        Assert.Equal(deviceId, pendingDevice.Id);
        Assert.Equal("Marco's Laptop", pendingDevice.Name);
        Assert.Equal(createdAt, pendingDevice.CreatedAt);
    }

    [Fact]
    public void RoundTrip_PendingDevices_PreservesAllFields()
    {
        var response = new SyncPullResponse
        {
            ServerTimestamp = DateTime.UtcNow,
            CatalogVersion = 7,
            HasMore = false,
            PendingDevices = new List<SyncPendingDevice>
            {
                new() { Id = Guid.NewGuid(), Name = null, CreatedAt = DateTime.UtcNow },
            },
        };

        var json = JsonSerializer.Serialize(response, WireOptions);
        var roundTripped = JsonSerializer.Deserialize<SyncPullResponse>(json, WireOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(response.CatalogVersion, roundTripped!.CatalogVersion);
        Assert.Equal(response.HasMore, roundTripped.HasMore);
        var expected = response.PendingDevices![0];
        var actual = Assert.Single(roundTripped.PendingDevices!);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Null(actual.Name);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
    }
}
