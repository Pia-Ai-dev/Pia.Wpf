using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class HistoryViewModelTests
{
    private readonly IHistoryService _history = Substitute.For<IHistoryService>();
    private readonly ITemplateService _templates = Substitute.For<ITemplateService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IOutputService _output = Substitute.For<IOutputService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    /// <summary>Counted rather than asserted with <c>Received(n)</c>: seeding the start-date filter also
    /// schedules a 500 ms debounced reload, so an exact call count is a race on a loaded machine.</summary>
    private int _searches;

    private HistoryViewModel CreateSut()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        // OnNavigatedToAsync swallows exceptions into a log, so an auto-stubbed null collection would
        // skip the load and let a test pass without ever reaching the query. The reference data is
        // non-empty on purpose: the "already loaded" guard is a Count check, so an empty result
        // legitimately re-fetches and would make the once-only assertion meaningless.
        _templates.GetTemplatesAsync().Returns(
            Task.FromResult<IReadOnlyList<OptimizationTemplate>>(
                [new OptimizationTemplate { Name = "T", Prompt = "P" }]));
        _providers.GetProvidersAsync().Returns(
            Task.FromResult<IReadOnlyList<AiProvider>>(
                [new AiProvider { Name = "P", Endpoint = "https://example.invalid" }]));
        _history.SearchSessionsAsync().ReturnsForAnyArgs(_ =>
        {
            _searches++;
            return Task.FromResult<IReadOnlyList<OptimizationSession>>(Array.Empty<OptimizationSession>());
        });
        _history.GetSessionCountAsync().ReturnsForAnyArgs(Task.FromResult(0));

        return new HistoryViewModel(
            NullLogger<HistoryViewModel>.Instance,
            _history, _templates, _providers, _output, _dialog, _loc);
    }

    [Fact]
    public async Task OnNavigatedToAsync_SeedsNoEndDate_SoLaterSessionsAreNotFilteredOut()
    {
        // The end date used to be seeded from DateTime.Today once per app run, which went stale at the
        // next midnight and made the SQL filter drop every newer session.
        var sut = CreateSut();

        await sut.OnNavigatedToAsync(null);

        await _history.Received().SearchSessionsAsync(
            searchText: Arg.Any<string?>(),
            templateId: Arg.Any<Guid?>(),
            fromDate: Arg.Any<DateTime?>(),
            toDate: null,
            offset: Arg.Any<int>(),
            limit: Arg.Any<int>());
        Assert.Null(sut.FilterEndDate);
        sut.Dispose();
    }

    [Fact]
    public async Task OnNavigatedToAsync_Twice_ReloadsSessions_ButFetchesReferenceDataOnce()
    {
        // A single `if (_templates.Count > 0) return;` guard used to skip the whole method, so
        // re-opening the page kept showing whatever it saw first.
        var sut = CreateSut();

        await sut.OnNavigatedToAsync(null);
        var afterFirst = _searches;
        await sut.OnNavigatedToAsync(null);
        var afterSecond = _searches;
        sut.Dispose();

        Assert.True(afterFirst > 0, "the first navigation did not load at all");
        Assert.True(afterSecond > afterFirst, "the second navigation did not reload the session list");
        await _templates.Received(1).GetTemplatesAsync();
        await _providers.Received(1).GetProvidersAsync();
    }
}
