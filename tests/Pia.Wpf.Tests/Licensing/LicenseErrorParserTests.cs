using System.Net;
using System.Net.Http;
using System.Text;
using Pia.Infrastructure;
using Pia.Shared.Licensing;
using Xunit;

namespace Pia.Tests.Licensing;

public class LicenseErrorParserTests
{
    private static HttpResponseMessage CreateResponse(
        HttpStatusCode status,
        string? body,
        string contentType = "application/json")
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, contentType);
        }
        return response;
    }

    [Fact]
    public async Task TryParseAsync_NoLicenseWithSetupUrl_ReturnsDto()
    {
        var json = """{"error":"no_license","setupUrl":"/admin/setup","message":"Server not activated."}""";
        var response = CreateResponse(HttpStatusCode.Forbidden, json);

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("no_license", result!.Error);
        Assert.Equal("/admin/setup", result.SetupUrl);
        Assert.Equal("Server not activated.", result.Message);
    }

    [Fact]
    public async Task TryParseAsync_FeatureNotLicensed_ReturnsDtoWithFeature()
    {
        var json = """{"error":"feature_not_licensed","feature":"Sync"}""";
        var response = CreateResponse(HttpStatusCode.Forbidden, json);

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("feature_not_licensed", result!.Error);
        Assert.Equal("Sync", result.Feature);
    }

    [Fact]
    public async Task TryParseAsync_UserLimitReached_ReturnsDtoWithLimit()
    {
        var json = """{"error":"user_limit_reached","limit":5}""";
        var response = CreateResponse(HttpStatusCode.Forbidden, json);

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("user_limit_reached", result!.Error);
        Assert.Equal(5, result.Limit);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task TryParseAsync_NonForbiddenStatus_ReturnsNull(HttpStatusCode status)
    {
        var json = """{"error":"no_license"}""";
        var response = CreateResponse(status, json);

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryParseAsync_UnknownErrorKey_ReturnsNull()
    {
        var json = """{"error":"some_other_thing"}""";
        var response = CreateResponse(HttpStatusCode.Forbidden, json);

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryParseAsync_NonJsonBody_ReturnsNull()
    {
        var response = CreateResponse(HttpStatusCode.Forbidden, "not json at all", "text/plain");

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryParseAsync_EmptyBody_ReturnsNull()
    {
        var response = CreateResponse(HttpStatusCode.Forbidden, null);

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryParseAsync_MalformedJson_ReturnsNull()
    {
        var response = CreateResponse(HttpStatusCode.Forbidden, "{not valid json");

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryParseAsync_AfterParse_ResponseBodyStillReadable()
    {
        var json = """{"error":"no_license","setupUrl":"/admin/setup"}""";
        var response = CreateResponse(HttpStatusCode.Forbidden, json);

        var result = await LicenseErrorParser.TryParseAsync(response, CancellationToken.None);
        var bodyAfter = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(json, bodyAfter);
    }
}
