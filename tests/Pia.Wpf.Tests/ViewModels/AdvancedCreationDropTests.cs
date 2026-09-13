using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// A mail or document dropped on a question arrives as its extracted text. The panel renders behind the
/// dialog backdrop, so the failure path is the one worth pinning: a snackbar would never be seen, and the
/// error banner beside it offers Retry, which spends a model turn an unreadable file does not deserve.
/// </summary>
public sealed class AdvancedCreationDropTests : IDisposable
{
    private readonly string _dir;
    private readonly IAdvancedCreationService _service = Substitute.For<IAdvancedCreationService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public AdvancedCreationDropTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-advdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // Must be stubbed: the view model loads the picker on construction and would await a null task.
        _personas.GetPersonasAsync().Returns(Array.Empty<Persona>());
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "active", SystemPrompt = "p" });
        _settings.GetSettingsAsync().Returns(new AppSettings());

        // Every resolved string is its own key, so an asserted message names the branch that fired.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<AdvancedCreationViewModel> StartedWithOneQuestionAsync()
    {
        _service.MaxAskTurns.Returns(6);
        _service.StartAsync(Arg.Any<AdvancedCreationSession>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdvancedCreationTurn(
                IsComplete: false,
                Summary: "summary",
                Questions: [new AdvancedCreationQuestion(
                    "sample", AdvancedCreationAnswerKind.Sample, "A sample?", null, [], Optional: true)],
                DraftJson: null));

        var vm = new AdvancedCreationViewModel(
            _service, _loc, NullLogger<AdvancedCreationViewModel>.Instance, _personas, _settings,
            AdvancedCreationModes.Persona(), providerId: null, seed: "a persona");

        await vm.StartCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task DroppedFile_LandsInTheQuestionAsText()
    {
        var vm = await StartedWithOneQuestionAsync();
        var row = Assert.Single(vm.Questions);

        await row.FilesDroppedCommand.ExecuteAsync([WriteFile("note.txt", "Dear Sir, no.")]);

        Assert.Equal("Dear Sir, no.", row.Text);
        Assert.Null(vm.DropNotice);
    }

    [Fact]
    public async Task DroppingOntoABoxTheUserTypedIn_Appends()
    {
        var vm = await StartedWithOneQuestionAsync();
        var row = Assert.Single(vm.Questions);
        row.Text = "mine";

        await row.FilesDroppedCommand.ExecuteAsync([WriteFile("note.txt", "theirs")]);

        Assert.Equal($"mine{Environment.NewLine}{Environment.NewLine}theirs", row.Text);
    }

    [Fact]
    public async Task AFileThatCannotBeRead_ReportsOnTheNotice_NotTheRetryableBanner()
    {
        var vm = await StartedWithOneQuestionAsync();
        var row = Assert.Single(vm.Questions);

        await row.FilesDroppedCommand.ExecuteAsync([WriteFile("photo.png", "not really a png")]);

        Assert.Equal("Msg_File_Unsupported", vm.DropNotice);
        Assert.True(vm.HasDropNotice);
        // The Retry beside ErrorMessage re-runs the turn, which costs a model call.
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(string.Empty, row.Text);
    }

    [Fact]
    public async Task TheOpeningBoxTakesADropToo()
    {
        _service.MaxAskTurns.Returns(6);
        var vm = new AdvancedCreationViewModel(
            _service, _loc, NullLogger<AdvancedCreationViewModel>.Instance, _personas, _settings,
            AdvancedCreationModes.Template(), providerId: null, seed: null);

        await vm.OpeningFilesDroppedCommand.ExecuteAsync([WriteFile("brief.txt", "rewrite my notes")]);

        Assert.Equal("rewrite my notes", vm.Opening);
    }
}
