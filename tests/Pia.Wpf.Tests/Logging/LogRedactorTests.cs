using System.IO;
using System.Text;
using Pia.Logging;
using Xunit;

namespace Pia.Tests.Logging;

/// <summary>
/// Pure string-in/string-out over MemoryStreams. No temp directory, no profile fixture, no PiaPaths — the
/// redactor takes its keys as data, which is what keeps this whole file off the machine it runs on.
/// </summary>
public class LogRedactorTests
{
    private static readonly RedactionKeys Keys = new(
        RoamingRoot: @"C:\Users\lovelace\AppData\Roaming\Pia",
        LocalRoot: @"C:\Users\lovelace\AppData\Local\Pia",
        UserProfileRoot: @"C:\Users\lovelace",
        MachineName: "WORKBENCH",
        UserName: "lovelace",
        Hosts: ["localhost", "api.example.test"],
        ProviderNames: ["Acme Cloud", "Local Ollama"]);

    private static string Run(string input, RedactionKeys? keys = null)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        LogRedactor.Redact(source, destination, keys ?? Keys);
        return Encoding.UTF8.GetString(destination.ToArray());
    }

    private static RedactionSummary Summarise(string input, RedactionKeys? keys = null)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        return LogRedactor.Redact(source, destination, keys ?? Keys);
    }

    private static string Record(string level, string message) =>
        $"2026-08-22T10:34:29.8969808+02:00\t{level}\t[Pia.Services.Probe]\t[0]\t{message}";

    // The real Bootstrapper line, which is LogInformation and not #if DEBUG — so it is in every release log
    // and is the single strongest justification for the whole rule set. Asserted whole, not by absence.
    [Fact]
    public void TheProfileRootLine_IsRedactedAndItsPrefixSurvivesByteForByte()
    {
        var output = Run(Record(
            "INFO",
            @"Data directories: Roaming=C:\Users\lovelace\AppData\Roaming\Pia, "
            + @"Local=C:\Users\lovelace\AppData\Local\Pia, Overridden=False"));

        Assert.Equal(
            "2026-08-22T10:34:29.8969808+02:00\tINFO\t[Pia.Services.Probe]\t[0]\t"
            + "Data directories: Roaming=<profile-roaming>, Local=<profile-local>, Overridden=False\r\n",
            output);
    }

    /// <summary>The longest key must win: replacing the user name first would leave the directory standing.</summary>
    [Fact]
    public void TheLongestProfileKeyWins_SoTheRootsAreNotReducedToTheUserNameOnly()
    {
        var output = Run(Record("WARN", @"reading C:\Users\lovelace\AppData\Local\Pia\Logs\pia.log"));

        // The Logs\ segment goes with the rest of the chain; the leaf is what a support engineer needs.
        Assert.EndsWith(@"reading <profile-local>\<path>\pia.log" + "\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<user>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADebugRecordBody_IsDroppedWholesale()
    {
        var output = Run(Record("DBUG", "Engine done: Them 6243ms text='my name is Ada Lovelace' (len=24)"));

        Assert.Equal(
            "2026-08-22T10:34:29.8969808+02:00\tDBUG\t[Pia.Services.Probe]\t[0]\t<debug-payload-dropped>\r\n",
            output);
    }

    [Fact]
    public void ATraceRecordBody_IsDroppedToo()
    {
        Assert.Contains("<debug-payload-dropped>", Run(Record("TRCE", "anything at all")), StringComparison.Ordinal);
    }

    /// <summary>A stack trace under a dropped record is payload, so it is omitted rather than swept.</summary>
    [Fact]
    public void ContinuationLinesUnderADroppedRecord_AreOmittedEntirely()
    {
        var output = Run(
            Record("DBUG", "tool result:") + "\r\n"
            + "   line two of the payload, mentioning Ada Lovelace\r\n"
            + "   line three\r\n"
            + Record("INFO", "back to normal"));

        Assert.DoesNotContain("Lovelace", output, StringComparison.Ordinal);
        Assert.DoesNotContain("line three", output, StringComparison.Ordinal);
        Assert.Contains("back to normal", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void AContinuationLineUnderAKeptRecord_IsSwept()
    {
        var output = Run(
            Record("FAIL", "the step threw") + "\r\n"
            + @"   at Pia.Services.Probe.Run() in C:\Users\lovelace\src\probe\Probe.cs:line 42");

        Assert.Contains(@"in <profile-user>\<path>\Probe.cs:line 42", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file whose first bytes are the tail of a dropped payload must emit nothing until it has parsed a
    /// record — the drop state starts closed, not open.
    /// </summary>
    [Fact]
    public void AStreamThatStartsMidRecord_EmitsNothingBeforeItsFirstRecord()
    {
        var output = Run(
            "   orphan tail naming Ada Lovelace\r\n"
            + "   another orphan\r\n"
            + Record("INFO", "first real record"));

        Assert.Equal(Record("INFO", "first real record") + "\r\n", output);
    }

    [Fact]
    public void TheUserNameOutsideAPath_IsReplaced()
    {
        Assert.Contains("account <user> is signed in", Run(Record("INFO", "account lovelace is signed in")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheMachineName_IsReplacedWithItsDnsSuffix()
    {
        Assert.Contains("peer <machine> answered",
            Run(Record("INFO", "peer WORKBENCH.corp.example.test answered")), StringComparison.Ordinal);
    }

    [Fact]
    public void AUrl_IsCollapsedToItsSchemeAndAStableHostCode()
    {
        var output = Run(Record("INFO", "GET https://api.example.test/v1/chat?key=abcd1234abcd1234 failed"));

        Assert.Contains($"<url:https://host-{LogRedactor.HostCode("api.example.test")}> failed", output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("abcd1234", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/chat", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostCode_IsStableAndCaseInsensitive()
    {
        Assert.Equal(LogRedactor.HostCode("Example.Test"), LogRedactor.HostCode("example.test"));
        Assert.Matches("^[0-9]{3}$", LogRedactor.HostCode("example.test"));
    }

    /// <summary>A port is a diagnostic fact, not user data, so the boundary stops before it.</summary>
    [Fact]
    public void AConfiguredHostOutsideAUrl_IsReplacedButItsPortSurvives()
    {
        Assert.Contains($"host-{LogRedactor.HostCode("localhost")}:8081",
            Run(Record("INFO", "server resolved to localhost:8081")), StringComparison.Ordinal);
    }

    /// <summary>Indexed by the position as PASSED, so support can still see that every failure names one provider.</summary>
    [Fact]
    public void ProviderNames_AreReplacedByTheirDeclaredIndex()
    {
        var output = Run(Record("FAIL", "provider Acme Cloud threw; Local Ollama is fine"));

        Assert.Contains("provider <provider-0> threw; <provider-1> is fine", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmailAddress_IsReplaced()
    {
        Assert.Contains("signed in as <email> ok",
            Run(Record("INFO", "signed in as grace.hopper@example.test ok")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Bearer sk-abcdefghijklmnopqrstuvwx", "<token>")]
    [InlineData("api_key: ABCDEFGHIJKLMNOPQRSTUV", "<token>")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9zzz", "<token>")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz012345", "<token>")]
    public void ACredential_IsReplaced(string message, string expected)
    {
        var output = Run(Record("WARN", message));

        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOP", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The underscore in GITHUB_TOKEN= is what a \b anchor gets wrong, and a half-redacted token reads as if
    /// it had been vetted.
    /// </summary>
    [Fact]
    public void ACredentialBehindAnUnderscoredName_IsReplacedWhole()
    {
        var output = Run(Record("WARN", "env GITHUB_TOKEN=ghp_ABCDefghIJKLmnopQRSTuvwx0123 rejected"));

        Assert.Contains("<token>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("QRSTuvwx", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialQueryParameter_IsReplacedEvenWithoutAParseableUrl()
    {
        Assert.Contains("&access_token=<token>",
            Run(Record("WARN", "callback ?state=x&access_token=zzzzzzzzzzzz done")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsolutePathOutsideTheProfile_LosesItsDirectoryAndKeepsItsLeaf()
    {
        Assert.Contains(@"wrote <path>\report.md",
            Run(Record("INFO", @"wrote D:\Shared\Q3 Numbers\report.md")), StringComparison.Ordinal);
    }

    /// <summary>A JSON-escaped drive path doubles its separators, which must not read as a UNC head.</summary>
    [Fact]
    public void AUncHeadIsReplaced_ButAJsonEscapedDrivePathIsNotMistakenForOne()
    {
        Assert.Contains(@"<unc>\", Run(Record("WARN", @"opening \\fileserver\team\notes.txt")),
            StringComparison.Ordinal);
        Assert.DoesNotContain("<unc>", Run(Record("WARN", @"{""path"":""C:\\Data\\notes.txt""}")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// OutputService interpolates the window title into an exception message that is logged at Warning, so
    /// the title reaches a release log even though the same value is SensitiveDebug two lines above it.
    /// </summary>
    [Fact]
    public void TheWindowTitleInARestoreFailure_IsReplaced()
    {
        var output = Run(Record(
            "WARN", "Failed to restore previous window 'Q3 layoffs.xlsx - Excel' (EXCEL)"));

        Assert.Contains("'<window-title>' (<process>)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("layoffs", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderResponseBodyQuotedIntoAnException_IsReplaced()
    {
        var output = Run(Record(
            "FAIL", @"Acme Cloud chat failed (502): {""error"":""Bad Gateway"",""detail"":""echoed prompt""}"));

        Assert.Contains("failed (502): <response-body>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("echoed prompt", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tracked window via cursor: handle=264190, process='explorer', class='CabinetWClass'",
        "process='<process>', class='<window-class>'")]
    [InlineData("RestorePreviousWindow: SetForegroundWindow(1) (process: devenv) returned True",
        "(process: <process>)")]
    public void BothProcessTemplates_AreCovered(string message, string expected)
    {
        Assert.Contains(expected, Run(Record("INFO", message)), StringComparison.Ordinal);
    }

    /// <summary>Requiring [digits] in the event-id field would reject 79,204 real records.</summary>
    [Fact]
    public void ARecordWithANamedEventId_IsStillARecord()
    {
        var output = Run(
            "2026-08-22T10:34:29.8969808+02:00\tDBUG\t[System.Net.Http.HttpClient]\t[RequestStart]\tpayload");

        Assert.Contains("<debug-payload-dropped>", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A five-column tabular line inside a payload satisfies "five tab-separated fields" by accident. If the
    /// level field were not checked it would clear the drop state and let the rest of the payload out.
    /// </summary>
    [Fact]
    public void AFiveColumnTabularLineInsideAPayload_IsNotMistakenForARecord()
    {
        var output = Run(
            Record("DBUG", "tool result:") + "\r\n"
            + "2026-08-22T10:34:29.8969808+02:00\tada\t[secret]\t[secret]\tAda Lovelace\r\n"
            + "   still payload\r\n");

        Assert.DoesNotContain("Lovelace", output, StringComparison.Ordinal);
        Assert.DoesNotContain("still payload", output, StringComparison.Ordinal);
    }

    /// <summary>Split with a count of 5 keeps a tab inside the message where it belongs.</summary>
    [Fact]
    public void ATabInsideAMessage_Survives()
    {
        Assert.Contains("left\tright", Run(Record("INFO", "left\tright")), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRuleIsCounted_IncludingTheOnesThatNeverFire()
    {
        var summary = Summarise(Record("INFO", "nothing to redact here"));

        Assert.Equal(LogRedactor.RuleIds.Count, summary.HitsByRuleId.Count);
        Assert.All(LogRedactor.RuleIds, id => Assert.True(summary.HitsByRuleId.ContainsKey(id), id));
        Assert.All(summary.HitsByRuleId.Values, hits => Assert.Equal(0, hits));
    }

    [Fact]
    public void TheSummaryCountsLinesAndDroppedRecords()
    {
        var summary = Summarise(
            Record("INFO", "one") + "\r\n" + Record("DBUG", "two") + "\r\n" + "   payload\r\n");

        Assert.Equal(3, summary.LinesRead);
        Assert.Equal(2, summary.LinesWritten);
        Assert.Equal(1, summary.RecordsDropped);
    }

    /// <summary>The two tiers are code, not prose: every rule declares which one it is in.</summary>
    [Fact]
    public void EveryRuleDeclaresATierAndTheIdListMatchesTheDescriptors()
    {
        Assert.Equal(LogRedactor.RuleIds, [.. LogRedactor.Descriptors.Select(d => d.Id)]);
        Assert.Equal(LogRedactor.RuleIds.Distinct().Count(), LogRedactor.RuleIds.Count);
        Assert.Contains(LogRedactor.Descriptors, d => d.Tier == RedactionTier.Deterministic);
        Assert.Contains(LogRedactor.Descriptors, d => d.Tier == RedactionTier.BestEffort);
        Assert.All(LogRedactor.Descriptors, d => Assert.False(string.IsNullOrWhiteSpace(d.Covers)));
    }

    /// <summary>Under four characters a name collides with ordinary prose more often than it identifies anyone.</summary>
    [Fact]
    public void AKeyShorterThanFourCharacters_IsIgnored()
    {
        var keys = RedactionKeys.None with { UserName = "al", MachineName = "pc" };

        Assert.Contains("al on pc", Run(Record("INFO", "al on pc"), keys), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyKeys_LeaveTheRecordIntactApartFromTheShapeRules()
    {
        Assert.Contains("plain message", Run(Record("INFO", "plain message"), RedactionKeys.None),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheOutputIsCrlfWithNoByteOrderMark()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Record("INFO", "a")));
        using var destination = new MemoryStream();
        LogRedactor.Redact(source, destination, Keys);
        var bytes = destination.ToArray();

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(0x0D, bytes[^2]);
        Assert.Equal(0x0A, bytes[^1]);
    }

    /// <summary>A source BOM must be consumed, not carried into the first timestamp field.</summary>
    [Fact]
    public void ASourceByteOrderMark_IsConsumedRatherThanEmitted()
    {
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes(Record("INFO", "after the mark")));

        using var source = new MemoryStream([.. bytes]);
        using var destination = new MemoryStream();
        LogRedactor.Redact(source, destination, Keys);

        Assert.Equal(Record("INFO", "after the mark") + "\r\n",
            Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public void TheDestinationStreamIsNotClosed_SoAZipEntryStaysWritable()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Record("INFO", "a")));
        using var destination = new MemoryStream();
        LogRedactor.Redact(source, destination, Keys);

        destination.WriteByte(0x21);
        Assert.True(destination.CanWrite);
    }
}
