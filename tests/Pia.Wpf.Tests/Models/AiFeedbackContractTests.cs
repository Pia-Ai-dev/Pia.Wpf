using System.Text.Json;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AiFeedbackContractTests
{
    [Fact]
    public void Response_FromAServerThatSendsOnlyTheId_HasNoDelivery()
    {
        var response = JsonSerializer.Deserialize<AiFeedbackResponse>(
            """{"id":"7d1f6c2e-3b6a-4a55-9d0e-2f1c8b9a4e11"}""", JsonSerializerOptions.Web)!;

        Assert.Equal(Guid.Parse("7d1f6c2e-3b6a-4a55-9d0e-2f1c8b9a4e11"), response.Id);
        Assert.Null(response.Delivery);
    }

    [Theory]
    [InlineData("notified", AiFeedbackResponse.DeliveryNotified)]
    [InlineData("stored_only", AiFeedbackResponse.DeliveryStoredOnly)]
    public void Response_ReadsTheDeliveryValue(string wire, string expected)
    {
        var response = JsonSerializer.Deserialize<AiFeedbackResponse>(
            $$"""{"id":"7d1f6c2e-3b6a-4a55-9d0e-2f1c8b9a4e11","delivery":"{{wire}}"}""", JsonSerializerOptions.Web)!;

        Assert.Equal(expected, response.Delivery);
    }

    [Fact]
    public void Request_SendsPrivacyConcernWithoutBumpingTheSchema()
    {
        var json = JsonSerializer.Serialize(new AiFeedbackRequest { PrivacyConcern = true }, JsonSerializerOptions.Web);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("privacyConcern").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }
}
