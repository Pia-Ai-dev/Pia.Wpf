using System.Reflection;
using Pia.Models;
using Pia.Services;
using Pia.Shared.Policy;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A key whose value cannot take effect before a restart puts the app behind a blocking overlay,
/// so every setting is classified by hand here and a new one has to be classified too.</summary>
public class PolicyRestartClassificationTests
{
    private static readonly string[] LiveAlready =
    [
        nameof(AppSettings.AgentMaxReplans),
        nameof(AppSettings.AgentMaxSteps),
        nameof(AppSettings.AgentPersonaRoster),
        nameof(AppSettings.AgentPlanReasoningTurnEnabled),
        nameof(AppSettings.AgentRunAutoApproveBuiltInWrites),
        nameof(AppSettings.AgentWallClockMinutes),
        nameof(AppSettings.AutoCaptureSelectedText),
        nameof(AppSettings.AutoTypeDelayMs),
        nameof(AppSettings.AutoUpdateEnabled),
        nameof(AppSettings.BlockedBuiltInPersonas),
        nameof(AppSettings.ChatAutoTitleEnabled),
        nameof(AppSettings.ChatHistoryEnabled),
        nameof(AppSettings.ChatHistoryRetentionDays),
        nameof(AppSettings.DefaultOutputAction),
        nameof(AppSettings.DefaultWindowMode),
        nameof(AppSettings.EnableMeetingDiarization),
        nameof(AppSettings.LastCounterpartName),
        nameof(AppSettings.MaxParallelBackgroundRuns),
        nameof(AppSettings.MaxParallelRequestsPerProvider),
        nameof(AppSettings.MaxToolRoundsPerStep),
        nameof(AppSettings.MeetingAttendeeDisplayName),
        nameof(AppSettings.MeetingAttendeeRosterSnapshotMinutes),
        nameof(AppSettings.MeetingAttendeeShowBrowserWindow),
        nameof(AppSettings.MeetingBrowserSelection),
        nameof(AppSettings.MeetingMaxSpeakers),
        nameof(AppSettings.MeetingMinSpeechSeconds),
        nameof(AppSettings.MeetingSmartSpeakerDetection),
        nameof(AppSettings.MeetingSuppressSpeakerLabels),
        nameof(AppSettings.MeetingTranscriptFolder),
        nameof(AppSettings.ModePersonaDefaults),
        nameof(AppSettings.ModeProviderDefaults),
        nameof(AppSettings.ScheduledMaxReplans),
        nameof(AppSettings.ScheduledMaxSteps),
        nameof(AppSettings.ScheduledWallClockMinutes),
        nameof(AppSettings.SpeakerEmbeddingThreshold),
        nameof(AppSettings.SttBackend),
        nameof(AppSettings.TargetSpeechLanguage),
        nameof(AppSettings.Theme),
        nameof(AppSettings.UserOperatingMode),
        nameof(AppSettings.UseSameProviderForAllModes),
        nameof(AppSettings.WhisperModel)
    ];

    private static readonly string[] LiveWithWork =
    [
        nameof(AppSettings.AllowedSyncProviders),
        nameof(AppSettings.AllowPersonaManagement),
        nameof(AppSettings.AllowProviderManagement),
        nameof(AppSettings.AlwaysAllowedTools),
        nameof(AppSettings.AssistantAgentModeDefault),
        nameof(AppSettings.AssistantDefaultWorkingDirectory),
        nameof(AppSettings.AssistantFilesFolder),
        nameof(AppSettings.AssistantFileToolsEnabled),
        nameof(AppSettings.AssistantGitToolsEnabled),
        nameof(AppSettings.AssistantHotkey),
        nameof(AppSettings.AssistantSuggestionsEnabled),
        nameof(AppSettings.AutoIngestSources),
        nameof(AppSettings.DefaultTemplateId),
        nameof(AppSettings.DirectTranscriptionEnabled),
        nameof(AppSettings.FastPathHotkey),
        nameof(AppSettings.LaunchAtStartup),
        nameof(AppSettings.MeetingAttendeeEnabled),
        nameof(AppSettings.OptimizeHotkey),
        nameof(AppSettings.TargetLanguage),
        nameof(AppSettings.TtsEnabled),
        nameof(AppSettings.TtsVoiceModelKey),
        nameof(AppSettings.UiLanguage)
    ];

    private static readonly string[] RestartRequired =
    [
        nameof(AppSettings.AssistantChatsBackfilledAt),
        nameof(AppSettings.AssistantFolderLayoutVersion),
        nameof(AppSettings.EncryptedRefreshToken),
        nameof(AppSettings.HasCompletedFirstRunWizard),
        nameof(AppSettings.IngestSchemaVersion),
        nameof(AppSettings.Privacy),
        nameof(AppSettings.ServerUrl),
        nameof(AppSettings.StartMinimized),
        nameof(AppSettings.SyncEnabled),
        nameof(AppSettings.SyncProvider),
        nameof(AppSettings.SyncUserEmail),
        nameof(AppSettings.VaultVersion)
    ];

    private static readonly string[] NoRuntimeEffect =
    [
        nameof(AppSettings.ClientPolicyInitialized),
        nameof(AppSettings.DefaultProviderId),
        nameof(AppSettings.DraftText),
        nameof(AppSettings.E2EEDeviceId),
        nameof(AppSettings.E2EEEncryptedUmk),
        nameof(AppSettings.E2EERecoveryConfigured),
        nameof(AppSettings.E2EEUmkVersion),
        nameof(AppSettings.EncryptedAccessToken),
        nameof(AppSettings.FlowPinned),
        nameof(AppSettings.IsE2EEEnabled),
        nameof(AppSettings.LastActiveView),
        nameof(AppSettings.LastCatalogVersion),
        nameof(AppSettings.LastChatPullETag),
        nameof(AppSettings.LastPullETag),
        nameof(AppSettings.LastPushedSettingsHash),
        nameof(AppSettings.LastSyncTimestamp),
        nameof(AppSettings.ManagedPersonaStoreInitialized),
        nameof(AppSettings.SyncDeviceId),
        nameof(AppSettings.SyncUserDisplayName),
        nameof(AppSettings.SyncUserId),
        nameof(AppSettings.TodoColumnWidths),
        nameof(AppSettings.TrustSelfSignedCertificates),
        nameof(AppSettings.WindowHeight),
        nameof(AppSettings.WindowLeft),
        nameof(AppSettings.WindowTop),
        nameof(AppSettings.WindowWidth)
    ];

    private static string[] Classified() =>
        [.. LiveAlready, .. LiveWithWork, .. RestartRequired, .. NoRuntimeEffect];

    /// <summary>The same predicate <c>PolicyService</c> uses; a looser one would classify names policy can
    /// never reach.</summary>
    private static HashSet<string> SettableSettings() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EverySettableSettingIsClassified()
    {
        var settable = SettableSettings();
        var classified = Classified();

        var unknown = classified.Where(n => !settable.Contains(n)).ToArray();
        Assert.True(
            unknown.Length == 0,
            "classified names that are not settable settings: " + string.Join(", ", unknown));

        var unclassified = settable.Where(n => !classified.Contains(n)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            unclassified.Length == 0,
            "settings with no liveness classification: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void NoSettingIsClassifiedTwice()
    {
        var duplicated = Classified()
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(duplicated.Length == 0, "settings classified more than once: " + string.Join(", ", duplicated));
    }

    [Fact]
    public void ANewSettingForcesAnExplicitClassification()
    {
        Assert.True(
            LiveAlready.Length == 41 && LiveWithWork.Length == 22
                && RestartRequired.Length == 12 && NoRuntimeEffect.Length == 26,
            "the four sets are written out in full, found "
                + $"{LiveAlready.Length}/{LiveWithWork.Length}/{RestartRequired.Length}/{NoRuntimeEffect.Length}");

        Assert.Contains(nameof(AppSettings.Privacy), RestartRequired);
        Assert.Contains(nameof(AppSettings.Theme), LiveAlready);
    }

    /// <summary>Exact, not a subset: StartMinimized and HasCompletedFirstRunWizard are restart-required as
    /// values yet nothing misbehaves in-session, so adding either would block the app for nothing.</summary>
    [Fact]
    public void TheOverlayListIsPrivacyAlone()
    {
        Assert.Equal(
            new[] { nameof(AppSettings.Privacy) },
            PolicyChangeCoordinator.RestartRequiredKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var denied = PolicyChangeCoordinator.RestartRequiredKeys.Where(ClientPolicyContract.IsDenied).ToArray();
        Assert.True(denied.Length == 0, "a server policy can never set: " + string.Join(", ", denied));
    }
}
