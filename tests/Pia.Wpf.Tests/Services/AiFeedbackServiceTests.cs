using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public class AiFeedbackServiceTests
{
    private static AssistantMessage PiaCloudAnswer() =>
        new(ChatRole.Assistant, "The capital of France is Berlin.") { Stats = new AnswerStats(20, AnswerProvenance.PiaCloudLabel) };

    private static (AiFeedbackService Sut, CapturingRequestHandler Http, ITokenMapService TokenMap) Build(
        AppSettings settings, string? token = "tok")
    {
        var handler = new CapturingRequestHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(settings);

        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync(Arg.Any<bool>(), Arg.Any<string?>()).Returns(token);

        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => $"T({ci.Arg<string>()})");

        var sut = new AiFeedbackService(factory, settingsService, auth, () => tokenMap, NullLogger<AiFeedbackService>.Instance);
        return (sut, handler, tokenMap);
    }

    [Fact]
    public async Task BuildRequest_TokenizesCommentAndAnswer_WhenThePrivacySettingIsOn()
    {
        var settings = new AppSettings();
        settings.Privacy.TokenizationEnabled = true;
        var (sut, _, tokenMap) = Build(settings);
        var message = PiaCloudAnswer();
        var chatId = Guid.NewGuid();

        var request = await sut.BuildRequestAsync(message, chatId, AiFeedbackRequest.RatingDown, "  Anna's address is wrong ", includeAnswer: true);

        await tokenMap.Received(1).InitializeAsync();
        Assert.Equal(message.Id, request.MessageId);
        Assert.Equal(chatId, request.ChatId);
        Assert.Equal("down", request.Rating);
        Assert.Equal("T(Anna's address is wrong)", request.Comment);
        Assert.Equal("T(The capital of France is Berlin.)", request.AnswerText);
        Assert.True(request.PiiTokenized);
        Assert.Equal(AnswerProvenance.PiaCloudLabel, request.Model);
        Assert.Equal(AppVersionInfo.Version, request.AppVersion);
    }

    [Fact]
    public async Task BuildRequest_SendsTextAsShown_WhenTokenizationIsOff_AndOmitsTheAnswerWithoutConsent()
    {
        var settings = new AppSettings();
        settings.Privacy.TokenizationEnabled = false;
        var (sut, _, tokenMap) = Build(settings);

        var request = await sut.BuildRequestAsync(PiaCloudAnswer(), null, AiFeedbackRequest.RatingDown, "wrong", includeAnswer: false);

        await tokenMap.DidNotReceive().InitializeAsync();
        Assert.Equal("wrong", request.Comment);
        Assert.Null(request.AnswerText);
        Assert.False(request.PiiTokenized);
    }

    [Fact]
    public async Task BuildRequest_ThumbsUp_CarriesNoText_AndNeverTouchesTheTokenizer()
    {
        var settings = new AppSettings();
        settings.Privacy.TokenizationEnabled = true;
        var (sut, _, tokenMap) = Build(settings);

        var request = await sut.BuildRequestAsync(PiaCloudAnswer(), null, AiFeedbackRequest.RatingUp, null, includeAnswer: false);

        await tokenMap.DidNotReceive().InitializeAsync();
        Assert.Equal("up", request.Rating);
        Assert.Null(request.Comment);
        Assert.Null(request.AnswerText);
        Assert.False(request.PiiTokenized);
    }

    [Fact]
    public async Task Send_PostsCamelCaseJson_WithTheBearerToken_ToTheFeedbackEndpoint()
    {
        var (sut, http, _) = Build(new AppSettings { ServerUrl = "https://cloud.example/" });
        var request = new AiFeedbackRequest { MessageId = Guid.NewGuid(), Rating = "down", Comment = "c", AnswerText = "a" };

        var sent = await sut.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.Equal("https://cloud.example/api/ai-feedback", http.LastRequestUri!.ToString());
        Assert.Equal("Bearer tok", http.LastAuthorization);
        using var doc = JsonDocument.Parse(http.LastBody!);
        Assert.Equal("down", doc.RootElement.GetProperty("rating").GetString());
        Assert.Equal(request.MessageId, doc.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Send_IsRefusedLocally_WithoutAServerOrASession()
    {
        var (noServer, http, _) = Build(new AppSettings());
        Assert.False(await noServer.SendAsync(new AiFeedbackRequest(), TestContext.Current.CancellationToken));
        Assert.Null(http.LastRequestUri);

        var (signedOut, http2, _) = Build(new AppSettings { ServerUrl = "https://cloud.example" }, token: null);
        Assert.False(await signedOut.SendAsync(new AiFeedbackRequest(), TestContext.Current.CancellationToken));
        Assert.Null(http2.LastRequestUri);
    }
}
