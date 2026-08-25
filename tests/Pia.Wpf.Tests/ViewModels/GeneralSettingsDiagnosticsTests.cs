using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Diagnostics;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The consent dialog only ever stated the included count, so a capped export read as complete.</summary>
public class GeneralSettingsDiagnosticsTests
{
    private const string BaseKey = "Settings_ExportDiagnostics_Confirm_Message";
    private const string CapKey = "Settings_ExportDiagnostics_Confirm_ExcludedByCap";
    private const string ExcludedKey = "Settings_ExportDiagnostics_Confirm_Excluded";

    [Fact]
    public async Task ConsentDialogNamesTheFilesLeftOutAndTheCap()
    {
        var harness = Build(PlanOf(included: 3, byCap: 32));

        await harness.Vm.ExportDiagnosticsCommand.ExecuteAsync(null);

        var tail = Render(CapKey, 32, 7, 10);
        Assert.Contains("32", tail, StringComparison.Ordinal);
        Assert.EndsWith(tail, harness.Captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("[", harness.Captured.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsentDialogNamesTheFilesLeftOutWithoutBlamingTheCap()
    {
        var harness = Build(PlanOf(included: 2, unrecognised: 1));

        await harness.Vm.ExportDiagnosticsCommand.ExecuteAsync(null);

        var tail = Render(ExcludedKey, 1);
        Assert.Contains("1", tail, StringComparison.Ordinal);
        Assert.EndsWith(tail, harness.Captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("[", harness.Captured.Body, StringComparison.Ordinal);
    }

    /// <summary>An undated name is left out at any cap, so the cap must not be blamed for it.</summary>
    [Fact]
    public async Task ConsentDialogBlamesTheCapForOnlyWhatTheCapLeftOut()
    {
        var harness = Build(PlanOf(included: 7, byCap: 3, unrecognised: 1));

        await harness.Vm.ExportDiagnosticsCommand.ExecuteAsync(null);

        Assert.Contains(Render(CapKey, 3, 7, 10), harness.Captured.Body, StringComparison.Ordinal);
        Assert.EndsWith(Render(ExcludedKey, 1), harness.Captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Render(CapKey, 4, 7, 10), harness.Captured.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsentDialogAddsNoLineWhenNothingIsLeftOut()
    {
        var harness = Build(PlanOf(included: 7));

        await harness.Vm.ExportDiagnosticsCommand.ExecuteAsync(null);

        // Derived from the template, not hard-coded: the rendered base message embeds a machine-dependent path.
        var template = LocalizationSource.Instance[BaseKey];
        Assert.EndsWith(template[(template.LastIndexOf('}') + 1)..], harness.Captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("left out", harness.Captured.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsentDialogIsNotShownWhenThereIsNothingToExport()
    {
        var harness = Build(DiagnosticsExportPlan.Empty);

        await harness.Vm.ExportDiagnosticsCommand.ExecuteAsync(null);

        await harness.Dialogs.DidNotReceive().ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>());
        harness.Snackbar.ReceivedWithAnyArgs(1).Show(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task DecliningTheConsentDialogExportsNothing()
    {
        var harness = Build(PlanOf(included: 3, byCap: 32));

        await harness.Vm.ExportDiagnosticsCommand.ExecuteAsync(null);

        await harness.Export.DidNotReceive().ExportAsync(
            Arg.Any<DiagnosticsExportRequest>(), Arg.Any<CancellationToken>());
    }

    private static string Render(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, LocalizationSource.Instance[key], args);

    /// <summary>Both counts come off the rows rather than being passed beside them: a plan whose
    /// ExcludedCount disagreed with its files is what hid the cap blaming an undated name.</summary>
    private static DiagnosticsExportPlan PlanOf(int included, int byCap = 0, int unrecognised = 0)
    {
        var files = new List<DiagnosticsLogFile>();
        for (var i = 0; i < included; i++)
            files.Add(new DiagnosticsLogFile($"pia-included-{i}.log", 10, true, null));
        for (var i = 0; i < byCap; i++)
            files.Add(new DiagnosticsLogFile(
                $"pia-capped-{i}.log", 10, false, DiagnosticsExclusionReason.OverFileCountCap));
        for (var i = 0; i < unrecognised; i++)
            files.Add(new DiagnosticsLogFile(
                $"pia-nodate-{i}.log", 10, false, DiagnosticsExclusionReason.UnrecognisedName));

        var excludedCount = files.Count(f => !f.Included);
        return new DiagnosticsExportPlan(
            files, files.Count - excludedCount, 10L * (files.Count - excludedCount), excludedCount,
            null, null, byCap > 0);
    }

    private sealed class BodyCapture
    {
        public string Body { get; set; } = string.Empty;
    }

    private sealed record Harness(
        GeneralSettingsViewModel Vm,
        IDialogService Dialogs,
        IDiagnosticsExportService Export,
        global::Wpf.Ui.ISnackbarService Snackbar,
        BodyCapture Captured);

    /// <summary>Every fact declines the dialog, so nothing here creates a directory or writes an archive.</summary>
    private static Harness Build(DiagnosticsExportPlan plan)
    {
        var logger = NullLogger<SettingsViewModel>.Instance;
        var settings = Substitute.For<ISettingsService>();
        var policy = Substitute.For<IPolicyService>();
        var snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();

        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization[Arg.Any<string>()].Returns(c => LocalizationSource.Instance[(string)c[0]]);
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(
            c => Render((string)c[0], (object[])c[1]));

        var captured = new BodyCapture();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Do<string>(m => captured.Body = m))
            .Returns(Task.FromResult(false));

        var export = Substitute.For<IDiagnosticsExportService>();
        export.Plan(Arg.Any<string>(), Arg.Any<DiagnosticsExportCaps>()).Returns(plan);

        var vm = new GeneralSettingsViewModel(
            logger, settings, Substitute.For<ITranscriptionService>(), dialogs,
            Substitute.For<ITrayIconService>(), Substitute.For<ITtsService>(), snackbar, localization,
            Substitute.For<IAutostartService>(), policy,
            new PrivacySettingsViewModel(logger, settings, policy),
            Substitute.For<ISyncClientService>(), export);

        return new Harness(vm, dialogs, export, snackbar, captured);
    }
}
