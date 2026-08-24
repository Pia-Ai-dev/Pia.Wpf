using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Logging;
using Pia.Services.Diagnostics;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Temp directories only. The service resolves no path of its own, so nothing here needs
/// RedirectedProfileFixture or the PiaPathsStatic collection, and nothing here can reach the real profile.
/// </summary>
public sealed class DiagnosticsExportServiceTests : IDisposable
{
    private const string Prefix = "2026-08-22T10:34:29.8969808+02:00\tINFO\t[Bootstrapper]\t[0]\t";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"pia-diag-{Guid.NewGuid():N}");

    private readonly string _source;
    private readonly string _output;

    public DiagnosticsExportServiceTests()
    {
        _source = Path.Combine(_root, "Logs");
        _output = Path.Combine(_root, "Diagnostics");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_output);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private static readonly RedactionKeys Keys = new(
        RoamingRoot: @"C:\Users\lovelace\AppData\Roaming\Pia",
        LocalRoot: @"C:\Users\lovelace\AppData\Local\Pia",
        UserProfileRoot: @"C:\Users\lovelace",
        MachineName: "WORKBENCH",
        UserName: "lovelace",
        Hosts: [],
        ProviderNames: []);

    private static readonly DiagnosticsEnvironment Environment = new(
        1, DateTimeOffset.UnixEpoch, "1.2.3", "Windows", "X64", "X64", ".NET 10", "EN", true, false,
        new Dictionary<string, int> { ["PiaCloud"] = 1 }, 1);

    private static DiagnosticsExportService Build()
    {
        var collector = Substitute.For<IDiagnosticsEnvironmentCollector>();
        collector.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DiagnosticsExportContext(Environment, Keys)));
        return new DiagnosticsExportService(
            NullLogger<DiagnosticsExportService>.Instance, collector);
    }

    private void Log(string date, string message) =>
        File.WriteAllText(Path.Combine(_source, $"pia-{date}.log"), Prefix + message + "\r\n");

    private void LogOfSize(string date, int bytes) =>
        File.WriteAllText(
            Path.Combine(_source, $"pia-{date}.log"), Prefix + new string('x', bytes) + "\r\n");

    private string ZipPath => Path.Combine(_output, "export.zip");

    private Task<DiagnosticsExportResult> ExportAsync(DiagnosticsExportCaps? caps = null) =>
        Build().ExportAsync(
            new DiagnosticsExportRequest(_source, ZipPath, caps ?? DiagnosticsExportCaps.Default),
            CancellationToken.None);

    private static string[] Entries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return [.. archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal)];
    }

    private static string ReadEntry(string zipPath, string entryName)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        using var stream = archive.GetEntry(entryName)!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// An EXACT set, not an absence check. A deny-list assertion ("does not contain providers.json") goes
    /// vacuous the day a new file type lands in the profile; this one fails.
    /// </summary>
    [Fact]
    public async Task TheArchiveHoldsExactlyTheExpectedEntries_WithEveryDecoyLeftBehind()
    {
        Log("2026-08-22", "one");
        Log("2026-08-23", "two");
        // Everything a naive "zip the folder" would have shipped.
        File.WriteAllText(Path.Combine(_source, "providers.json"), "{\"key\":\"secret\"}");
        File.WriteAllText(Path.Combine(_source, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_source, "history.db"), "sqlite");
        File.WriteAllText(Path.Combine(_source, "history.db-wal"), "wal");
        File.WriteAllText(Path.Combine(_source, "pia.log"), Prefix + "unrolled\r\n");
        File.WriteAllText(Path.Combine(_source, "Logs.zip"), "zip");
        File.WriteAllText(Path.Combine(_source, "transcript.md"), "chat");

        var result = await ExportAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["README.txt", "environment.json", "logs/pia-2026-08-22.log", "logs/pia-2026-08-23.log",
                "manifest.json"],
            Entries(ZipPath));
    }

    /// <summary>
    /// The sink holds today's file open for writing, so File.OpenRead would be refused for exactly the file
    /// a support request needs most. This is the assertion that pins FileShare.ReadWrite.
    /// </summary>
    [Fact]
    public async Task ALogFileHeldOpenForWriting_IsStillExported()
    {
        Log("2026-08-22", "before");
        var held = Path.Combine(_source, "pia-2026-08-23.log");
        File.WriteAllText(held, Prefix + "held open\r\n");

        using (var writer = new FileStream(held, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            // Proves the premise rather than assuming it: this is what the exporter must not do.
            Assert.Throws<IOException>(() => File.OpenRead(held).Dispose());

            var result = await ExportAsync();

            Assert.True(result.Succeeded);
            Assert.Contains("logs/pia-2026-08-23.log", Entries(ZipPath));
            Assert.Contains("held open", ReadEntry(ZipPath, "logs/pia-2026-08-23.log"),
                StringComparison.Ordinal);
            writer.Flush();
        }
    }

    /// <summary>Otherwise the second export ships the first.</summary>
    [Theory]
    [InlineData("export.zip")]
    [InlineData("nested/export.zip")]
    public async Task AnOutputPathInsideTheSourceDirectory_IsRefused(string relative)
    {
        Log("2026-08-22", "one");
        var target = Path.Combine(_source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var result = await Build().ExportAsync(
            new DiagnosticsExportRequest(_source, target, DiagnosticsExportCaps.Default),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticsExportFailure.OutputInsideSourceDirectory, result.Failure);
        Assert.False(File.Exists(target));
    }

    /// <summary>A sibling whose name merely starts with the source path is not inside it.</summary>
    [Fact]
    public async Task ASiblingDirectoryWithASharedPrefix_IsNotTreatedAsInside()
    {
        Log("2026-08-22", "one");
        var sibling = _source + "-out";
        Directory.CreateDirectory(sibling);

        var result = await Build().ExportAsync(
            new DiagnosticsExportRequest(
                _source, Path.Combine(sibling, "export.zip"), DiagnosticsExportCaps.Default),
            CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AnEmptySourceDirectory_ReportsNoLogFiles_AndWritesNothing()
    {
        var result = await ExportAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticsExportFailure.NoLogFiles, result.Failure);
        Assert.False(File.Exists(ZipPath));
    }

    [Fact]
    public async Task AMissingSourceDirectory_FailsGracefullyAndPlansEmpty()
    {
        var missing = Path.Combine(_root, "gone");

        var result = await Build().ExportAsync(
            new DiagnosticsExportRequest(missing, ZipPath, DiagnosticsExportCaps.Default),
            CancellationToken.None);

        Assert.Equal(DiagnosticsExportFailure.SourceDirectoryMissing, result.Failure);
        Assert.Same(DiagnosticsExportPlan.Empty, Build().Plan(missing, DiagnosticsExportCaps.Default));
        Assert.Same(DiagnosticsExportPlan.Empty, Build().Plan("", DiagnosticsExportCaps.Default));
    }

    [Fact]
    public async Task AMissingOutputDirectory_ReportsItRatherThanThrowing()
    {
        Log("2026-08-22", "one");

        var result = await Build().ExportAsync(
            new DiagnosticsExportRequest(
                _source, Path.Combine(_root, "nope", "export.zip"), DiagnosticsExportCaps.Default),
            CancellationToken.None);

        Assert.Equal(DiagnosticsExportFailure.OutputDirectoryMissing, result.Failure);
    }

    [Fact]
    public async Task AnExistingArchive_IsNeverOverwritten()
    {
        Log("2026-08-22", "one");
        File.WriteAllText(ZipPath, "not a zip");

        var result = await ExportAsync();

        Assert.Equal(DiagnosticsExportFailure.OutputAlreadyExists, result.Failure);
        Assert.Equal("not a zip", File.ReadAllText(ZipPath));
    }

    /// <summary>
    /// A contiguous newest-first run, and every file the exporter saw is in the manifest whether it made it
    /// in or not — that is what makes the exclusion visible from inside the archive.
    /// </summary>
    [Fact]
    public async Task TheByteCapTakesAContiguousNewestRun_AndNamesEveryExcludedFile()
    {
        LogOfSize("2026-08-20", 400);
        LogOfSize("2026-08-21", 400);
        LogOfSize("2026-08-22", 10);
        LogOfSize("2026-08-23", 400);

        var result = await ExportAsync(new DiagnosticsExportCaps(MaxLogFiles: 9, MaxTotalSourceBytes: 900));

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["logs/pia-2026-08-22.log", "logs/pia-2026-08-23.log"],
            Entries(ZipPath).Where(e => e.StartsWith("logs/", StringComparison.Ordinal)));

        // 08-20 is small enough to fit after 08-21 is refused, and is still excluded: a set with a hole in
        // it is not something a support engineer can reason about.
        var plan = result.Plan;
        Assert.True(plan.CapApplied);
        Assert.Equal(4, plan.Files.Count);
        Assert.Equal(
            DiagnosticsExclusionReason.OverTotalByteCap,
            plan.Files.Single(f => f.FileName == "pia-2026-08-21.log").ExclusionReason);
        Assert.Equal(
            DiagnosticsExclusionReason.OverFileCountCap,
            plan.Files.Single(f => f.FileName == "pia-2026-08-20.log").ExclusionReason);
        Assert.Equal(new DateOnly(2026, 8, 22), plan.OldestIncluded);
        Assert.Equal(new DateOnly(2026, 8, 23), plan.NewestIncluded);
    }

    [Fact]
    public void TheFileCountCapKeepsTheNewest()
    {
        Log("2026-08-20", "a");
        Log("2026-08-21", "b");
        Log("2026-08-22", "c");

        var plan = Build().Plan(_source, new DiagnosticsExportCaps(MaxLogFiles: 2));

        Assert.Equal(2, plan.IncludedCount);
        Assert.Equal(
            ["pia-2026-08-22.log", "pia-2026-08-21.log"],
            plan.Files.Where(f => f.Included).Select(f => f.FileName));
    }

    [Fact]
    public void AFileNameThatIsNotADate_IsListedWithItsReason()
    {
        Log("2026-08-22", "a");
        File.WriteAllText(Path.Combine(_source, "pia.log"), "x");
        File.WriteAllText(Path.Combine(_source, "pia-nope.log"), "x");
        File.WriteAllText(Path.Combine(_source, "pia-2026-08-22x.log"), "x");

        var plan = Build().Plan(_source, DiagnosticsExportCaps.Default);

        Assert.Equal(1, plan.IncludedCount);
        Assert.Equal(
            ["pia-2026-08-22x.log", "pia-nope.log", "pia.log"],
            plan.Files.Where(f => f.ExclusionReason == DiagnosticsExclusionReason.UnrecognisedName)
                .Select(f => f.FileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The sink rolls at 10 MB and the rolled name carries a suffix. A fixed-width pia-????-??-??.log
    /// pattern would have dropped it from the export AND from the manifest, so nothing would say it existed.
    /// </summary>
    [Fact]
    public void ARolledFileIsIncludedAlongsideItsDay()
    {
        Log("2026-08-22", "first");
        File.WriteAllText(Path.Combine(_source, "pia-2026-08-22-1.log"), Prefix + "rolled\r\n");

        var plan = Build().Plan(_source, DiagnosticsExportCaps.Default);

        Assert.Equal(2, plan.IncludedCount);
        Assert.All(plan.Files, f => Assert.Null(f.ExclusionReason));
    }

    [Fact]
    public async Task TheExportedLogIsRedacted_AndItsPrefixSurvives()
    {
        Log("2026-08-22",
            @"Data directories: Roaming=C:\Users\lovelace\AppData\Roaming\Pia, Overridden=False");

        var result = await ExportAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            Prefix + "Data directories: Roaming=<profile-roaming>, Overridden=False\r\n",
            ReadEntry(ZipPath, "logs/pia-2026-08-22.log"));
    }

    [Fact]
    public async Task EnvironmentJsonNamesEveryRuleWithItsTierAndHitCount()
    {
        Log("2026-08-22", @"path C:\Users\lovelace\AppData\Local\Pia\x.log");

        await ExportAsync();
        using var document = JsonDocument.Parse(ReadEntry(ZipPath, "environment.json"));
        var rules = document.RootElement.GetProperty("RedactionRulesApplied");

        Assert.Equal(
            LogRedactor.RuleIds,
            [.. rules.EnumerateArray().Select(r => r.GetProperty("Id").GetString()!)]);
        Assert.All(rules.EnumerateArray(), r => Assert.Contains(
            r.GetProperty("Tier").GetString(), new[] { "Deterministic", "BestEffort" }));
        Assert.Equal(
            1,
            rules.EnumerateArray()
                .Single(r => r.GetProperty("Id").GetString() == LogRedactor.ProfileRoots)
                .GetProperty("Hits").GetInt64());
        Assert.Equal("1.2.3",
            document.RootElement.GetProperty("Environment").GetProperty("AppVersion").GetString());
    }

    /// <summary>
    /// The three generated entries are NOT redacted, so the only thing keeping a path out of them is their
    /// shape. This is what enforces it.
    /// </summary>
    [Fact]
    public async Task NoGeneratedEntryCarriesAPathTheRulesWouldHaveRemoved()
    {
        Log("2026-08-22", @"path C:\Users\lovelace\AppData\Local\Pia\x.log");
        File.WriteAllText(Path.Combine(_source, "pia-nope.log"), "x");

        await ExportAsync();

        foreach (var entry in new[] { "README.txt", "manifest.json", "environment.json" })
        {
            var text = ReadEntry(ZipPath, entry);
            Assert.DoesNotContain(_source, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\Users", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ManifestJsonListsEveryFileTheExporterSaw()
    {
        Log("2026-08-22", "a");
        File.WriteAllText(Path.Combine(_source, "pia-nope.log"), "x");
        File.WriteAllText(Path.Combine(_source, "pia.log"), "x");

        await ExportAsync();
        using var document = JsonDocument.Parse(ReadEntry(ZipPath, "manifest.json"));
        var names = document.RootElement.GetProperty("Files").EnumerateArray()
            .Select(f => f.GetProperty("FileName").GetString()!)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(["pia-2026-08-22.log", "pia-nope.log", "pia.log"], names);
    }

    [Fact]
    public async Task TheArchiveIsFullyFlushedAndReadable()
    {
        Log("2026-08-22", "a");

        await ExportAsync();

        using var archive = ZipFile.OpenRead(ZipPath);
        Assert.All(archive.Entries, e => Assert.True(e.Length > 0, e.FullName));
    }

    [Fact]
    public void TheFileNameIsUniqueToTheSecondAndCarriesNoPath()
    {
        var name = DiagnosticsExportRequest.BuildFileName(
            new DateTimeOffset(2026, 8, 24, 17, 5, 9, TimeSpan.Zero));

        Assert.Equal("pia-diagnostics-2026-08-24-170509.zip", name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadmeStatesWhatIsNotInTheArchive()
    {
        Log("2026-08-22", "a");

        await ExportAsync();
        var readme = ReadEntry(ZipPath, "README.txt");

        Assert.Contains("No chat transcripts", readme, StringComparison.Ordinal);
        Assert.Contains("DETERMINISTIC", readme, StringComparison.Ordinal);
        Assert.Contains("BEST-EFFORT", readme, StringComparison.Ordinal);
        Assert.Contains("never sent", readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledExport_LeavesNoArchiveBehind()
    {
        Log("2026-08-22", "a");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build().ExportAsync(
            new DiagnosticsExportRequest(_source, ZipPath, DiagnosticsExportCaps.Default),
            cancelled.Token));

        Assert.False(File.Exists(ZipPath));
    }

    /// <summary>Non-UTF8 bytes must not abort the export; U+FFFD in one message is the accepted cost.</summary>
    [Fact]
    public async Task ASourceFileWithInvalidUtf8_IsStillExported()
    {
        var bytes = new List<byte>(Encoding.UTF8.GetBytes(Prefix + "before "));
        bytes.AddRange([0xFF, 0xFE]);
        bytes.AddRange(Encoding.UTF8.GetBytes(" after\r\n"));
        File.WriteAllBytes(Path.Combine(_source, "pia-2026-08-22.log"), [.. bytes]);

        var result = await ExportAsync();

        Assert.True(result.Succeeded);
        Assert.Contains("after", ReadEntry(ZipPath, "logs/pia-2026-08-22.log"), StringComparison.Ordinal);
    }
}
