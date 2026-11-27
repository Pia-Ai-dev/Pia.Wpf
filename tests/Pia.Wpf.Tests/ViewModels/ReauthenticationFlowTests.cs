namespace Pia.Tests.ViewModels;

using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Shared.Auth;
using Pia.ViewModels;
using Xunit;

public class ReauthenticationFlowTests
{
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly List<ReauthenticationFields> _sent = [];
    private readonly List<ReauthenticationViewModel> _prompts = [];

    public ReauthenticationFlowTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private void UserTypes(params Action<ReauthenticationViewModel>[] answers)
    {
        var queue = new Queue<Action<ReauthenticationViewModel>>(answers);
        _dialogs.ShowReauthenticationDialogAsync(Arg.Any<ReauthenticationViewModel>()).Returns(ci =>
        {
            var vm = ci.Arg<ReauthenticationViewModel>();
            _prompts.Add(vm);
            if (queue.Count == 0) return false;
            queue.Dequeue()(vm);
            return true;
        });
    }

    private ReauthenticatedAttempt ServerAnswers(params string?[] answers)
    {
        var queue = new Queue<string?>(answers);
        return fields =>
        {
            _sent.Add(fields);
            return Task.FromResult(queue.Dequeue());
        };
    }

    private Task<(ReauthenticationOutcome, string?)> Run(ReauthenticatedAttempt attempt, string? known = null) =>
        ReauthenticationFlow.RunAsync(_dialogs, _loc, attempt, known);

    [Fact]
    public async Task AKnownPassword_IsTriedWithoutAPrompt()
    {
        var result = await Run(ServerAnswers([null]), "pw");

        Assert.Equal((ReauthenticationOutcome.Succeeded, (string?)null), result);
        Assert.Equal([new ReauthenticationFields("pw")], _sent);
        await _dialogs.DidNotReceiveWithAnyArgs().ShowReauthenticationDialogAsync(default!);
    }

    [Fact]
    public async Task SecondFactorRequired_AsksForTheCode_AndRetriesWithIt()
    {
        UserTypes(vm => vm.Code = "123 456");

        var result = await Run(ServerAnswers(ReauthenticationErrorCodes.SecondFactorRequired, null), "pw");

        Assert.Equal(ReauthenticationOutcome.Succeeded, result.Item1);
        Assert.True(_prompts.Single().IsCodeRequired);
        Assert.Equal(new ReauthenticationFields("pw", Code: "123456"), _sent[1]);
    }

    [Fact]
    public async Task AnythingButSixDigits_GoesOutAsARecoveryCode()
    {
        UserTypes(vm => vm.Code = "abcd-efgh");

        await Run(ServerAnswers(ReauthenticationErrorCodes.SecondFactorRequired, null), "pw");

        Assert.Equal(new ReauthenticationFields("pw", RecoveryCode: "abcd-efgh"), _sent[1]);
    }

    [Fact]
    public async Task AWrongPassword_ClearsItAndAsksAgain()
    {
        UserTypes(vm => vm.Password = "right");

        var result = await Run(ServerAnswers(ReauthenticationErrorCodes.InvalidPassword, null), "wrong");

        Assert.Equal(ReauthenticationOutcome.Succeeded, result.Item1);
        Assert.Equal("Reauth_InvalidPassword", _prompts.Single().ErrorMessage);
        Assert.Equal("right", _sent[1].Password);
    }

    [Fact]
    public async Task AUsedCode_AsksForTheNextOne()
    {
        UserTypes(vm => vm.Code = "111111", vm => vm.Code = "222222");

        await Run(ServerAnswers(
            ReauthenticationErrorCodes.SecondFactorRequired, ReauthenticationErrorCodes.CodeAlreadyUsed, null), "pw");

        Assert.Equal("Reauth_CodeAlreadyUsed", _prompts[1].ErrorMessage);
        Assert.Equal("222222", _sent[2].Code);
    }

    [Fact]
    public async Task Cancelling_StopsWithoutAnotherCall()
    {
        UserTypes();

        var result = await Run(ServerAnswers(ReauthenticationErrorCodes.SecondFactorRequired), "pw");

        Assert.Equal(ReauthenticationOutcome.Cancelled, result.Item1);
        Assert.Single(_sent);
    }

    [Theory]
    [InlineData(ReauthenticationErrorCodes.AccountLocked)]
    [InlineData(ReauthenticationErrorCodes.RateLimited)]
    [InlineData("failed")]
    public async Task ALockoutOrFailure_EndsTheFlow(string code)
    {
        var result = await Run(ServerAnswers(code), "pw");

        Assert.Equal((ReauthenticationOutcome.Refused, (string?)code), result);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task WithoutAKnownPassword_PromptsFirst()
    {
        UserTypes(vm => vm.Password = "pw");

        await Run(ServerAnswers([null]));

        Assert.Equal("pw", _sent.Single().Password);
    }
}
