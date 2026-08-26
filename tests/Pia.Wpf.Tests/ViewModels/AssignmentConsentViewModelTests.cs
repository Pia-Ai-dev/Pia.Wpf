using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Resources.Strings;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssignmentConsentViewModelTests
{
    private readonly IAssignmentScopeResolver _scope = Substitute.For<IAssignmentScopeResolver>();
    private readonly IAssignmentConsentStore _consent = Substitute.For<IAssignmentConsentStore>();
    private readonly IAssignmentRunOrchestrator _orchestrator = Substitute.For<IAssignmentRunOrchestrator>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AssignmentRequest? _sentRequest;
    private AssignmentConsentReceipt? _sentReceipt;

    public AssignmentConsentViewModelTests()
    {
        // NSubstitute's auto-value for a string is empty, which would let "each status has its own message"
        // pass on four empty strings. Echo the key instead.
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.Arg<string>());

        _consent.RecordAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AssignmentScopeItem>>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AssignmentConsentReceipt(
                Guid.NewGuid(),
                ci.ArgAt<string>(0),
                ci.ArgAt<IReadOnlyList<AssignmentScopeItem>>(2),
                DateTime.UtcNow));

        StartReturns(new AssignmentStartOutcome(AssignmentStartStatus.Started, Guid.NewGuid()));
    }

    private void StartReturns(AssignmentStartOutcome outcome) =>
        _orchestrator.StartAsync(
                Arg.Any<AssignmentRequest>(), Arg.Any<AssignmentConsentReceipt>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sentRequest = ci.Arg<AssignmentRequest>();
                _sentReceipt = ci.Arg<AssignmentConsentReceipt>();
                return outcome;
            });

    private AssignmentConsentViewModel Create() => new(
        _scope, _consent, _orchestrator, _localization,
        NullLogger<AssignmentConsentViewModel>.Instance);

    private static AssignmentSkill Skill(params string[] declaredInputTypes) =>
        new("brief", "Written brief", "brief", declaredInputTypes);

    private static AssignmentSurface Surface(params AssignmentSkill[] skills) => new(true, skills);

    private static AssignmentScopeItem Item(int chars, string title = "A record") =>
        new(AssignmentInputEntityTypes.Memory, Guid.NewGuid(), title, chars, DateTime.UtcNow);

    private void Offer(params AssignmentScopeItem[] items) =>
        _scope.ListAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(items);

    [Fact]
    public async Task ThePrimaryActionStaysDisabledUntilTheCheckboxIsTicked()
    {
        Offer(Item(100));
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);
        vm.Prompt = "Summarise this";

        Assert.False(vm.CanSend);

        vm.IsAffirmed = true;

        Assert.True(vm.CanSend);
    }

    [Fact]
    public async Task ABlankPromptKeepsThePrimaryActionDisabled()
    {
        Offer(Item(100));
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);
        vm.IsAffirmed = true;
        vm.Prompt = "   ";

        Assert.False(vm.CanSend);
    }

    [Fact]
    public async Task SendingWithoutTheAffirmationRecordsNothingAndStartsNothing()
    {
        Offer(Item(100));
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);
        vm.Prompt = "Summarise this";
        vm.Records[0].IsSelected = true;

        var status = await vm.SendAsync(Ct);

        Assert.Equal(AssignmentStartStatus.ConsentMissing, status);
        await _consent.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default!, default!, default, Ct);
        await _orchestrator.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task AnOverCapRecordCannotBeTicked()
    {
        Offer(Item(AssignmentInput.MaxItemChars + 1));
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);

        var row = Assert.Single(vm.Records);
        Assert.False(row.CanSelect);
        Assert.True(row.IsUnsendable);

        row.IsSelected = true;

        Assert.False(row.IsSelected);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task TickingPastTheItemCapIsRefusedWithAStatedReason()
    {
        Offer([.. Enumerable.Range(0, AssignmentInput.MaxItems + 1).Select(_ => Item(10))]);
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);

        foreach (var row in vm.Records) row.IsSelected = true;

        Assert.Equal(AssignmentInput.MaxItems, vm.SelectedCount);
        Assert.False(vm.Records[^1].IsSelected);
        Assert.Equal("AssignmentConsent_Cap_TooManyItems", vm.CapNotice);
    }

    [Fact]
    public async Task TickingPastTheTotalCharacterCapIsRefusedWithAStatedReason()
    {
        var big = AssignmentInput.MaxItemChars;
        Offer([.. Enumerable.Range(0, AssignmentInput.MaxTotalItemChars / big + 1).Select(_ => Item(big))]);
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);

        foreach (var row in vm.Records) row.IsSelected = true;

        Assert.Equal(AssignmentInput.MaxTotalItemChars, vm.SelectedChars);
        Assert.False(vm.Records[^1].IsSelected);
        Assert.Equal("AssignmentConsent_Cap_TotalChars", vm.CapNotice);
    }

    [Fact]
    public async Task TheReceiptIsMintedBeforeTheRunStartsAndCoversTheSameRecords()
    {
        var first = Item(100, "First");
        var second = Item(200, "Second");
        Offer(first, second);
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);
        vm.Prompt = "Summarise these";
        vm.IsAffirmed = true;
        vm.Records[0].IsSelected = true;
        vm.Records[1].IsSelected = true;

        var status = await vm.SendAsync(Ct);

        Assert.Equal(AssignmentStartStatus.Started, status);
        Received.InOrder(() =>
        {
            _consent.RecordAsync(
                "brief", "brief", Arg.Any<IReadOnlyList<AssignmentScopeItem>>(),
                AssignmentGranter.User, "Summarise these".Length, Arg.Any<CancellationToken>());
            _orchestrator.StartAsync(
                Arg.Any<AssignmentRequest>(), Arg.Any<AssignmentConsentReceipt>(), Arg.Any<CancellationToken>());
        });

        Assert.NotNull(_sentRequest);
        Assert.NotNull(_sentReceipt);
        Assert.Equal(
            _sentReceipt!.Items.Select(i => i.EntityId).OrderBy(id => id),
            _sentRequest!.Items.Select(i => i.EntityId).OrderBy(id => id));
        Assert.Equal(new[] { first.EntityId, second.EntityId }, _sentRequest.Items.Select(i => i.EntityId));
        Assert.Equal("Summarise these", _sentRequest.Prompt);
    }

    [Fact]
    public async Task TheRecordContentIsNeverReadWhileChoosing()
    {
        Offer(Item(100));
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);
        vm.Records[0].IsSelected = true;
        vm.Prompt = "Summarise this";
        vm.IsAffirmed = true;

        await vm.SendAsync(Ct);

        await _scope.DidNotReceiveWithAnyArgs().ReadTextAsync(default!, Ct);
    }

    [Fact]
    public async Task ASkillDeclaringNoInputTypesOffersNoRecordsAndStillSends()
    {
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill()), ct: Ct);
        vm.Prompt = "Research the market";
        vm.IsAffirmed = true;

        Assert.Empty(vm.Records);
        Assert.True(vm.IsPromptOnly);
        Assert.False(vm.OffersRecords);
        Assert.False(vm.ShowSkillPicker);
        await _scope.DidNotReceiveWithAnyArgs().ListAsync(default!, Ct);
        Assert.True(vm.CanSend);

        Assert.Equal(AssignmentStartStatus.Started, await vm.SendAsync(Ct));

        Assert.NotNull(_sentRequest);
        Assert.Empty(_sentRequest!.Items);
    }

    [Fact]
    public async Task ASecondSendIsRefused()
    {
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill()), ct: Ct);
        vm.Prompt = "Research the market";
        vm.IsAffirmed = true;

        Assert.Equal(AssignmentStartStatus.Started, await vm.SendAsync(Ct));
        Assert.Equal(AssignmentStartStatus.ConsentMissing, await vm.SendAsync(Ct));

        await _orchestrator.ReceivedWithAnyArgs(1).StartAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task ChangingTheSkillRelistsTheRecordsAndDropsTheSelection()
    {
        Offer(Item(100));
        var memories = Skill(AssignmentInputEntityTypes.Memory);
        var promptOnly = new AssignmentSkill("research", "Research", "research", []);
        var vm = Create();
        await vm.InitializeAsync(Surface(memories, promptOnly), ct: Ct);
        vm.Records[0].IsSelected = true;
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.ShowSkillPicker);

        vm.SelectedSkill = promptOnly;
        await vm.PendingRecordLoad;

        Assert.Empty(vm.Records);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task AStaleRecordListNeverLandsInANewerSkillsChoice()
    {
        var withMemories = new AssignmentSkill("a", "A", "brief", [AssignmentInputEntityTypes.Memory]);
        var withTodos = new AssignmentSkill("b", "B", "brief", [AssignmentInputEntityTypes.Todo]);
        var slow = new TaskCompletionSource<IReadOnlyList<AssignmentScopeItem>>();
        var quick = new TaskCompletionSource<IReadOnlyList<AssignmentScopeItem>>();
        _scope.ListAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<string>>(0)[0] == AssignmentInputEntityTypes.Memory
                ? slow.Task
                : quick.Task);
        var vm = Create();

        var initialising = vm.InitializeAsync(Surface(withMemories, withTodos), ct: Ct);
        vm.SelectedSkill = withTodos;
        var second = vm.PendingRecordLoad;

        quick.SetResult([new AssignmentScopeItem(
            AssignmentInputEntityTypes.Todo, Guid.NewGuid(), "From the newer skill", 10, DateTime.UtcNow)]);
        await second;
        slow.SetResult([Item(10, "From the older skill")]);
        await initialising;

        Assert.Equal(new[] { "From the newer skill" }, vm.Records.Select(r => r.Title));
        Assert.False(vm.IsLoadingRecords);
    }

    /// <summary>Arrowing through the picker is A → B → A, so two loads for the same skill can be in flight at
    /// once and the older one must not add its rows a second time.</summary>
    [Fact]
    public async Task ReturningToASkillMidLoadListsItsRecordsOnce()
    {
        var first = new AssignmentSkill("a", "A", "brief", [AssignmentInputEntityTypes.Memory]);
        var second = new AssignmentSkill("b", "B", "brief", [AssignmentInputEntityTypes.Todo]);
        var memories = new List<TaskCompletionSource<IReadOnlyList<AssignmentScopeItem>>>();
        var todos = new TaskCompletionSource<IReadOnlyList<AssignmentScopeItem>>();
        _scope.ListAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (ci.ArgAt<IReadOnlyList<string>>(0)[0] != AssignmentInputEntityTypes.Memory) return todos.Task;
                var pending = new TaskCompletionSource<IReadOnlyList<AssignmentScopeItem>>();
                memories.Add(pending);
                return pending.Task;
            });
        var vm = Create();

        var initialising = vm.InitializeAsync(Surface(first, second), ct: Ct);
        vm.SelectedSkill = second;
        var middle = vm.PendingRecordLoad;
        vm.SelectedSkill = first;
        var latest = vm.PendingRecordLoad;

        todos.SetResult([]);
        await middle;

        var record = Item(10, "The one record");
        memories[0].SetResult([record]);
        await initialising;
        Assert.True(vm.IsLoadingRecords);

        memories[1].SetResult([record]);
        await latest;

        Assert.Equal(new[] { record.EntityId }, vm.Records.Select(r => r.Item.EntityId));
        Assert.False(vm.IsLoadingRecords);
    }

    [Theory]
    [InlineData("AssignmentConsent_Record_Chars", 1)]
    [InlineData("AssignmentConsent_Record_TooLarge", 1)]
    [InlineData("AssignmentConsent_Cap_TooManyItems", 1)]
    [InlineData("AssignmentConsent_Cap_TotalChars", 1)]
    [InlineData("AssignmentConsent_Selection_Summary", 4)]
    public void EveryFormattedStringTakesTheSameArgumentsInAllThreeLocales(string key, int argumentCount)
    {
        var placeholder = new Regex(@"\{(\d+)");

        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var value = ViewStrings.ResourceManager.GetString(key, culture);
            Assert.False(string.IsNullOrEmpty(value), $"{culture.Name}: {key} is missing");

            var indexes = placeholder.Matches(value!)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(i => i);
            Assert.Equal(Enumerable.Range(0, argumentCount), indexes);
        }
    }

    [Theory]
    [InlineData(AssignmentStartStatus.Started)]
    [InlineData(AssignmentStartStatus.ConsentMissing)]
    [InlineData(AssignmentStartStatus.TooLarge)]
    [InlineData(AssignmentStartStatus.Refused)]
    public async Task EveryStartStatusSurfacesItsOwnMessage(AssignmentStartStatus status)
    {
        StartReturns(new AssignmentStartOutcome(status));
        var vm = Create();
        await vm.InitializeAsync(Surface(Skill()), ct: Ct);
        vm.Prompt = "Research the market";
        vm.IsAffirmed = true;

        Assert.Equal(status, await vm.SendAsync(Ct));
        Assert.Equal(AssignmentConsentViewModel.StartResultKey(status), vm.ResultMessage);
    }

    [Fact]
    public void TheStartStatusMessagesAreDistinctAndResolveInAllThreeLocales()
    {
        var keys = Enum.GetValues<AssignmentStartStatus>()
            .Select(AssignmentConsentViewModel.StartResultKey)
            .ToList();

        Assert.Equal(4, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());

        var entityKeys = new[]
        {
            AssignmentInputEntityTypes.AssistantChat, AssignmentInputEntityTypes.Session,
            AssignmentInputEntityTypes.Memory, AssignmentInputEntityTypes.Todo,
            AssignmentInputEntityTypes.Template, "somethingNewer",
        }.Select(AssignmentScopeItemViewModel.EntityTypeKey).Distinct().ToList();
        Assert.Equal(6, entityKeys.Count);

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            foreach (var key in keys.Concat(entityKeys).Append("AssignmentConsent_Result_Error"))
            {
                if (string.IsNullOrEmpty(ViewStrings.ResourceManager.GetString(key, culture)))
                    missing.Add($"{culture.Name}: {key}");
            }
        }

        Assert.True(missing.Count == 0, $"missing in some locale: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task AFailedRecordListLeavesTheDialogUsable()
    {
        _scope.ListAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the store is down"));
        var vm = Create();

        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);

        Assert.Empty(vm.Records);
        Assert.False(vm.IsLoadingRecords);
        Assert.True(vm.RecordsUnavailable);
        Assert.False(vm.HasNoOfferableRecords);
    }

    /// <summary>A read that failed and a user who owns nothing are different facts, and the dialog states one
    /// of them at the moment of affirmation.</summary>
    [Fact]
    public async Task ARecordListThatCameBackEmptyIsNotReportedAsUnreadable()
    {
        Offer();
        var vm = Create();

        await vm.InitializeAsync(Surface(Skill(AssignmentInputEntityTypes.Memory)), ct: Ct);

        Assert.True(vm.HasNoOfferableRecords);
        Assert.False(vm.RecordsUnavailable);
    }

    [Fact]
    public async Task APrefilledPromptIsTrimmedToTheCap()
    {
        var vm = Create();

        await vm.InitializeAsync(Surface(Skill()), new string('x', AssignmentInput.MaxPromptChars + 50), ct: Ct);

        Assert.Equal(AssignmentInput.MaxPromptChars, vm.Prompt.Length);
        vm.IsAffirmed = true;
        Assert.True(vm.CanSend);
    }

    /// <summary>The record listing runs off the skill change, so asserting the picked skill without asserting
    /// its records would pass on a dialog that selected one skill and listed another's records.</summary>
    [Fact]
    public async Task InitializeAsync_WithASkillName_SelectsThatSkill()
    {
        var vm = Create();
        OfferPerType();

        await vm.InitializeAsync(TwoSkills(), prefillSkillName: "second", ct: Ct);

        Assert.Equal("second", vm.SelectedSkill?.Name);
        Assert.True(vm.PendingRecordLoad.IsCompleted);
        Assert.Equal(new[] { "A todo" }, vm.Records.Select(r => r.Title));
    }

    [Fact]
    public async Task InitializeAsync_WithAnUnknownSkillName_FallsBackToTheFirst()
    {
        var vm = Create();
        OfferPerType();

        await vm.InitializeAsync(TwoSkills(), prefillSkillName: "no-such-skill", ct: Ct);

        Assert.Equal("first", vm.SelectedSkill?.Name);
        Assert.True(vm.PendingRecordLoad.IsCompleted);
        Assert.Equal(new[] { "A memory" }, vm.Records.Select(r => r.Title));
    }

    private static AssignmentSurface TwoSkills() => Surface(
        new AssignmentSkill("first", "First", "brief", [AssignmentInputEntityTypes.Memory]),
        new AssignmentSkill("second", "Second", "brief", [AssignmentInputEntityTypes.Todo]));

    private void OfferPerType()
    {
        IReadOnlyList<AssignmentScopeItem> memories = [Item(10, "A memory")];
        IReadOnlyList<AssignmentScopeItem> todos =
            [new AssignmentScopeItem(AssignmentInputEntityTypes.Todo, Guid.NewGuid(), "A todo", 10, DateTime.UtcNow)];

        _scope.ListAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<string>>(0)[0] == AssignmentInputEntityTypes.Memory
                ? memories
                : todos);
    }
}
