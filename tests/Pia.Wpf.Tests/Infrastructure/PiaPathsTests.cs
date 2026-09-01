using System.IO;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Paths;
using Pia.Services;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Two halves: the default roots must stay byte-identical to the literals the resolver replaced, and an override
/// applied after a consumer's type is already loaded must still reach it.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class PiaPathsTests
{
    private static readonly string RealRoaming = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pia");

    private static readonly string RealLocal = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pia");

    private static string TempRoot(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"pia-paths-{suffix}-{Guid.NewGuid():N}");

    [Fact]
    public void DataRoots_WithNoOverride_AreTheRealProfileDirectories()
    {
        using (PiaPaths.OverrideForTests(null, null))
        {
            Assert.Equal(RealRoaming, PiaPaths.RoamingDataDirectory);
            Assert.Equal(RealLocal, PiaPaths.LocalDataDirectory);
            Assert.False(PiaPaths.IsOverridden);
        }
    }

    /// <summary>The literals these replaced, spelled out again: a typo in the resolver has to fail here.</summary>
    [Fact]
    public void SharedArtifactDirectories_AreTheRealProfileDirectories()
    {
        AssertSharedArtifactsOnRealProfile();
    }

    /// <summary>Re-downloading gigabytes per run costs more than sharing them, so the override must not move
    /// these — a hermetic run is hermetic for data and shared for downloaded artifacts.</summary>
    [Fact]
    public void SharedArtifactDirectories_IgnoreTheOverride()
    {
        using (PiaPaths.OverrideForTests(TempRoot("roaming"), TempRoot("local")))
        {
            AssertSharedArtifactsOnRealProfile();
        }
    }

    private static void AssertSharedArtifactsOnRealProfile()
    {
        Assert.Equal(Path.Combine(RealLocal, "Models"), PiaPaths.ModelsDirectory);
        Assert.Equal(Path.Combine(RealLocal, "Piper"), PiaPaths.PiperDirectory);
        Assert.Equal(Path.Combine(RealLocal, "Browsers"), PiaPaths.BrowsersDirectory);
        Assert.Equal(Path.Combine(RealLocal, "plugins"), PiaPaths.PluginsDirectory);
        Assert.Equal(Path.Combine(RealLocal, "ConsentAudit"), PiaPaths.ConsentAuditDirectory);
        Assert.Equal(Path.Combine(RealLocal, "ConsentEvidence"), PiaPaths.ConsentEvidenceDirectory);
    }

    [Fact]
    public void DataRoots_WithOverride_UseTheOverrideVerbatim()
    {
        var roaming = TempRoot("roaming");
        var local = TempRoot("local");

        using (PiaPaths.OverrideForTests(roaming, local))
        {
            Assert.Equal(roaming, PiaPaths.RoamingDataDirectory);
            Assert.Equal(local, PiaPaths.LocalDataDirectory);
            Assert.True(PiaPaths.IsOverridden);
        }

        Assert.Equal(RealRoaming, PiaPaths.RoamingDataDirectory);
        Assert.False(PiaPaths.IsOverridden);
    }

    [Fact]
    public void DataRoots_WithRelativeOverride_AreRooted()
    {
        using (PiaPaths.OverrideForTests("pia-relative-probe", null))
        {
            Assert.True(Path.IsPathFullyQualified(PiaPaths.RoamingDataDirectory));
            Assert.EndsWith("pia-relative-probe", PiaPaths.RoamingDataDirectory, StringComparison.Ordinal);
        }
    }

    /// <summary>Whitespace is how a shell spells "unset", so it must not read as a real directory.</summary>
    [Fact]
    public void DataRoots_WithBlankOverride_FallBackToTheRealProfile()
    {
        using (PiaPaths.OverrideForTests("   ", "\t"))
        {
            Assert.Equal(RealRoaming, PiaPaths.RoamingDataDirectory);
            Assert.Equal(RealLocal, PiaPaths.LocalDataDirectory);
        }
    }

    /// <summary>The history DB is the file a replay used to share with the real install.</summary>
    [Fact]
    public void HistoryDatabase_FollowsTheOverride()
    {
        var local = TempRoot("local");
        using (PiaPaths.OverrideForTests(null, local))
        {
            using var context = new SqliteContext();
            Assert.Equal($"Data Source={Path.Combine(local, "history.db")}", context.ConnectionString);
        }
    }

    /// <summary>
    /// The initialization-order trap. Each member is read BEFORE the override is applied — that first read forces
    /// the declaring type's static initializer to run, so a <c>static readonly</c> field (or a
    /// <c>{ get; } = …</c> auto-property, which compiles to one) freezes the real profile path and the second read
    /// below returns it. Do not delete the first read: without it this test passes against the very bug it exists
    /// to catch.
    /// </summary>
    [Theory]
    [InlineData(SettingsDirectoryMember)]
    [InlineData(LegacyWorkdirMember)]
    [InlineData(RunsRootMember)]
    [InlineData(MeetingFolderMember)]
    [InlineData(VaultRootMember)]
    [InlineData(LogsDirectoryMember)]
    [InlineData(DiagnosticsDirectoryMember)]
    [InlineData(DropCacheDirectoryMember)]
    public void RoutedMember_ObservesAnOverrideAppliedAfterItsTypeIsLoaded(string member)
    {
        var read = ReaderFor(member);

        var beforeOverride = read();
        Assert.True(
            beforeOverride.StartsWith(RealRoaming, StringComparison.OrdinalIgnoreCase) ||
            beforeOverride.StartsWith(RealLocal, StringComparison.OrdinalIgnoreCase),
            $"{member} does not default to the real profile at all ('{beforeOverride}'), so this test cannot "
            + "tell a frozen static from a working override");

        var roaming = TempRoot("roaming");
        var local = TempRoot("local");
        using (PiaPaths.OverrideForTests(roaming, local))
        {
            var afterOverride = read();

            Assert.True(
                afterOverride.StartsWith(roaming, StringComparison.OrdinalIgnoreCase) ||
                afterOverride.StartsWith(local, StringComparison.OrdinalIgnoreCase),
                $"{member} resolved to '{afterOverride}', which is under neither override root. A static "
                + "readonly field cannot see an override applied after its type loaded — make it a property "
                + "that reads PiaPaths on every access.");
        }

        Assert.Equal(beforeOverride, read());
    }

    private const string LogsDirectoryMember = "PiaPaths.LogsDirectory";
    private const string DiagnosticsDirectoryMember = "PiaPaths.DiagnosticsDirectory";
    private const string DropCacheDirectoryMember = "PiaPaths.DropCacheDirectory";
    private const string SettingsDirectoryMember = "JsonPersistenceService.SettingsDirectory";
    private const string LegacyWorkdirMember = "AssistantWorkspace.LegacyWorkdir";
    private const string RunsRootMember = "AssistantWorkspace.RunsRoot";
    private const string MeetingFolderMember = "MeetingTranscriptPaths.DefaultMeetingFolder";
    private const string VaultRootMember = "VaultPathProvider.VaultRoot";

    private static Func<string> ReaderFor(string member) => member switch
    {
        SettingsDirectoryMember => () => SettingsDirectoryProbe.Value,
        LegacyWorkdirMember => () => AssistantWorkspace.LegacyWorkdir,
        RunsRootMember => () => AssistantWorkspace.RunsRoot,
        MeetingFolderMember => () => MeetingTranscriptPaths.DefaultMeetingFolder,
        VaultRootMember => () => new VaultPathProvider().VaultRoot,
        LogsDirectoryMember => () => PiaPaths.LogsDirectory,
        DiagnosticsDirectoryMember => () => PiaPaths.DiagnosticsDirectory,
        DropCacheDirectoryMember => () => PiaPaths.DropCacheDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "no reader for this member"),
    };

    /// <summary>Reaches <c>JsonPersistenceService.SettingsDirectory</c>, which is <c>protected static</c> and so
    /// visible only to a subclass.</summary>
    private sealed class SettingsDirectoryProbe : JsonPersistenceService<SettingsDirectoryProbe.Payload>
    {
        internal static string Value => SettingsDirectory;

        protected override string FileName => "probe.json";

        protected override Payload CreateDefault() => new();

        internal sealed class Payload;
    }

    /// <summary>An export written inside Logs would be picked up and shipped by the next one.</summary>
    [Fact]
    public void TheDiagnosticsDirectoryIsASiblingOfTheLogDirectory_NotAChildOfIt()
    {
        using (PiaPaths.OverrideForTests(TempRoot("roaming"), TempRoot("local")))
        {
            var logs = PiaPaths.LogsDirectory;
            var diagnostics = PiaPaths.DiagnosticsDirectory;

            Assert.NotEqual(logs, diagnostics);
            Assert.False(
                diagnostics.StartsWith(logs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"{diagnostics} sits inside {logs}, so every export would ship the previous one");
            Assert.Equal(Path.GetDirectoryName(logs), Path.GetDirectoryName(diagnostics));
        }
    }
}
