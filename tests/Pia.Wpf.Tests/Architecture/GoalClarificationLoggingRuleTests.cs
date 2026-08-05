using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// 18 Q7 / spec §8.6's closing note: <i>"the question is never plain-logged" is NOT in the acceptance list as a
/// sink assertion, deliberately</i> — <c>SensitiveDebug</c> is <c>[Conditional("DEBUG")]</c> and the suite runs
/// Debug, where the call emits like any other, so no assertion over sink output can tell a <c>SensitiveDebug</c>
/// call apart from a <c>LogInformation</c> one. It has to be a SOURCE-LEVEL fact instead: the model's
/// clarification question must never appear as an argument to a non-<c>Sensitive*</c> logger call. This is that
/// fact, copying <c>RunWorkspaceRuleTests.cs:15</c>'s shape — read the real source file off disk and scan its
/// text, because the rule is about what a call SITE looks like, not what a stub records at runtime.
/// <para>
/// <b>Scope.</b> The two production sites that hold the question's own text: <c>AgentPlanner.Declined</c> (18
/// G2's capture, <c>turn.Question</c>) and <c>AgentRunOrchestrator.SafePostClarificationQuestionAsync</c> (18
/// G3's chat post, the <c>question</c> parameter and <c>plan.ClarificationQuestion</c> at its call site). Every
/// OTHER file this batch touched only mentions the identifier in a doc comment, a method NAME
/// (<c>SafePostClarificationQuestionAsync</c> contains the substring by construction), or the
/// <c>PlanResult.ClarificationQuestion</c> record member's own declaration — none of those are a logger call, so
/// they are out of THIS rule's scope on their merits, not by omission.
/// </para>
/// <para>
/// <b>Why a presence check survives the scan.</b> <c>plan.ClarificationQuestion is not null</c> /
/// <c>turn.Question is not null</c> is the one shape this batch's own <c>LogInformation</c> lines use to say
/// "a question was worded" without saying WHAT it was — a <c>bool</c>, not the payload. The scan strips every
/// ORDINARY string literal out of a call's argument list first (so a message template that merely uses the
/// English word "question" in prose — e.g. <c>"...(question present={Present})"</c> — cannot false-positive),
/// then requires that any surviving identifier match is immediately followed by <c>is not null</c> / <c>is
/// null</c>.
/// </para>
/// <para>
/// <b>Interpolated-string holes are NOT stripped with the rest of the literal.</b> An adversarial review of
/// this file found that the naive "strip everything between two quotes" approach also erased the <c>{expr}</c>
/// HOLE of an interpolated string — so <c>_logger.LogWarning($"...: {question}")</c> would have scanned clean,
/// because the whole literal, hole included, matched as one opaque quoted span and vanished before the
/// identifier scan ever ran. <see cref="StripLiteralTextKeepingInterpolationHoles"/> walks the source instead of
/// matching it: it drops literal TEXT (single/verbatim/interpolated alike) but copies a <c>{expr}</c> hole's
/// CONTENT forward untouched, so the identifier scan still sees an interpolated leak.
/// </para>
/// <para>
/// <b>Scan surface.</b> <see cref="PlainLoggerCall"/> now also matches <c>LogCritical</c> (absent before) and a
/// bare <c>logger</c> receiver alongside this codebase's <c>_logger</c> convention — the two other gaps the same
/// review named. It does not attempt to match an arbitrarily-named receiver: CLAUDE.md's field-naming rule and a
/// repo-wide grep both confirm every logger field in these two files is <c>_logger</c>, so widening further would
/// only invite false positives against unrelated types.
/// </para>
/// </summary>
public class GoalClarificationLoggingRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    // The exact identifiers that carry the QUESTION'S OWN TEXT, not merely whether one was asked.
    // Matched with \b so "ClarificationQuestion" (one camelCase token, no boundary before "Question") is never
    // mistaken for a bare "Question"/"question" match — the two need separate arms in the regex below.
    private static readonly Regex QuestionIdentifier =
        new(@"\b(ClarificationQuestion|question)\b", RegexOptions.IgnoreCase);

    // _logger is this codebase's only convention (CLAUDE.md: fields are _camelCase), but a bare `logger` is
    // matched too — cheap to cover and it is one of the holes an adversarial review named.
    private static readonly Regex PlainLoggerCall =
        new(@"\b(?:_logger|logger)\.Log(Information|Warning|Error|Critical|Debug|Trace)\s*\(", RegexOptions.Singleline);

    [Theory]
    [InlineData("Services/AgentPlanner.cs")]
    [InlineData("Services/AgentRunOrchestrator.cs")]
    // 18 G5 added four more files that hold a question's own text, and each is here on its merits:
    //   • BackgroundAssistantTurnRunner.cs / ChatSession.cs — the two pre-route interception seams, which see the
    //     raw `question` tool argument and both log freely about tool calls a line above.
    //   • HeadlessTurnExecutor.cs — the executor that carries the captured question out on StepTurnResult, and
    //     which logs an Information line at the very same point.
    // Their plain Log* lines carry counts, ids and tokens only; the question reaches exactly one SensitiveDebug,
    // in AgentRunOrchestrator. Adding them widens the rule's SURFACE, which is the whole point of it being a
    // source scan rather than a checklist line.
    [InlineData("Services/BackgroundAssistantTurnRunner.cs")]
    [InlineData("Services/HeadlessTurnExecutor.cs")]
    [InlineData("ViewModels/Models/ChatSession.cs")]
    public void PlainLoggerCallsNeverCarryTheClarificationQuestionAsAValue(string relativePath)
    {
        var source = ReadSource(relativePath);

        var callsChecked = 0;
        foreach (Match call in PlainLoggerCall.Matches(source))
        {
            var body = ExtractParenthesizedBody(source, call.Index);
            var withoutTemplates = StripLiteralTextKeepingInterpolationHoles(body);
            callsChecked++;

            foreach (Match hit in QuestionIdentifier.Matches(withoutTemplates))
            {
                var after = withoutTemplates[(hit.Index + hit.Length)..].TrimStart();
                Assert.True(
                    after.StartsWith("is not null", StringComparison.Ordinal)
                        || after.StartsWith("is null", StringComparison.Ordinal),
                    $"{relativePath}: a plain (non-Sensitive*) logger call appears to carry the clarification "
                        + $"question as a VALUE, not a presence check — only '{{ }} is not null' / 'is null' may "
                        + $"follow '{hit.Value}' on this line:\n{body}");
            }
        }

        // Non-vacuity: both files really do call a plain Log* method today (the app-owned "a decline/park
        // happened" lines this rule must not silence), so a rename that made PlainLoggerCall match nothing
        // would not turn this fact vacuously green.
        Assert.True(callsChecked > 0, $"{relativePath}: expected at least one plain Log* call to scan; found none");
    }

    /// <summary>
    /// <b>18 G5, and a STRONGER statement than the scan above.</b> <c>UserInputRequestStore</c> is the sink that
    /// holds the mid-plan question's own text, and it takes no <c>ILogger</c> at all — so the rule there is not
    /// "no plain call carries the question" but "there is no logger call to carry it". Asserted as its own fact
    /// rather than as a row in the Theory, because the Theory's non-vacuity guard requires at least one plain
    /// <c>Log*</c> call per file and would fail on a file that correctly has none.
    /// <para>
    /// Injecting a logger here would not be wrong in itself — it is the natural place to count asks — but it
    /// would put a logger one line away from the payload, which is exactly the adjacency the whole rule exists to
    /// keep out of this batch. The COUNTS the observability half of 18 D4 needs are already logged by the two
    /// executors, from a scope that carries the run id and never the text.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAskSinkTakesNoLoggerAtAll()
    {
        var source = ReadSource("Services/UserInputRequestStore.cs");

        Assert.DoesNotContain(PlainLoggerCall.Matches(source).Cast<Match>(), _ => true);
        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        // Non-vacuity: this really is the file that holds the question, not an empty read.
        Assert.Contains("public string? Question", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The exact bypass an adversarial review demonstrated, closed.</b> Before this fix,
    /// <c>StringLiteral.Replace(body, "\"\"")</c> matched the WHOLE interpolated literal — hole included — as
    /// one opaque quoted span, so <c>{question}</c> vanished before <see cref="QuestionIdentifier"/> ever ran
    /// and this scan stayed green over a real leak. Proven directly on the reviewer's own repro line rather than
    /// through the Theory (which only scans the two files as they exist today, neither of which contains this
    /// shape): the identifier must SURVIVE stripping, and — because nothing here is an <c>is not null</c>/<c>is
    /// null</c> presence check — the surviving match must not be followed by one, which is what makes the outer
    /// test's assertion actually fire on a call shaped like this.
    /// </summary>
    [Fact]
    public void StripLiteralText_PreservesAnIdentifierInsideAnInterpolationHole()
    {
        const string leak = "_logger.LogWarning($\"Could not post the clarification question: {question}\")";
        var body = ExtractParenthesizedBody(leak, leak.IndexOf('.'));

        var stripped = StripLiteralTextKeepingInterpolationHoles(body);

        var hit = Assert.Single(QuestionIdentifier.Matches(stripped).Cast<Match>(), m =>
            !stripped[(m.Index + m.Length)..].TrimStart().StartsWith("is not null", StringComparison.Ordinal)
                && !stripped[(m.Index + m.Length)..].TrimStart().StartsWith("is null", StringComparison.Ordinal));
        Assert.Equal("question", hit.Value);
    }

    /// <summary>Companion positive control: an ORDINARY (non-interpolated) template mentioning the English word
    /// "question" in prose must still be stripped clean — the fix must not turn every prose mention into a
    /// false positive.</summary>
    [Fact]
    public void StripLiteralText_StillStripsAnOrdinaryTemplateMentioningTheWord()
    {
        const string benign = "_logger.LogInformation(\"a question was asked: {Present}\", true)";
        var body = ExtractParenthesizedBody(benign, benign.IndexOf('.'));

        var stripped = StripLiteralTextKeepingInterpolationHoles(body);

        Assert.DoesNotContain("question", stripped, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other two named gaps: <c>LogCritical</c> and a bare <c>logger</c> receiver now match, so a
    /// future call shaped either way is not silently invisible to the scan.</summary>
    [Theory]
    [InlineData("_logger.LogCritical(\"x\")")]
    [InlineData("logger.LogWarning(\"x\")")]
    [InlineData("logger.LogCritical(\"x\")")]
    public void PlainLoggerCall_MatchesTheNamedGapShapes(string snippet) =>
        Assert.True(PlainLoggerCall.IsMatch(snippet), $"expected PlainLoggerCall to match: {snippet}");

    private static string ReadSource(string relativePath)
    {
        var path = Path.Combine(SourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"source file not found: {path}");
        var source = File.ReadAllText(path);
        Assert.NotEmpty(source);
        return source;
    }

    // Extracts a call's full argument list, from the '(' right after the matched method name to its matching
    // ')', so a call spread across several lines (every call this rule cares about is) is scanned as one unit.
    private static string ExtractParenthesizedBody(string source, int matchStart)
    {
        var openParen = source.IndexOf('(', matchStart);
        var depth = 0;
        for (var i = openParen; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return source[openParen..(i + 1)];
            }
        }
        return source[openParen..];
    }

    /// <summary>
    /// Walks <paramref name="body"/> one character at a time and drops every string literal's TEXT — plain
    /// (<c>"..."</c>), verbatim (<c>@"..."</c>) and interpolated (<c>$"..."</c> / <c>$@"..."</c>) alike — while
    /// copying an interpolated literal's <c>{expr}</c> HOLE content through untouched (minus the braces
    /// themselves), so a leak riding an interpolation hole is still visible to <see cref="QuestionIdentifier"/>
    /// afterwards. <c>{{</c>/<c>}}</c> are the escaped-brace form and are dropped as literal text, never treated
    /// as a hole. Nesting inside a hole (e.g. a conditional expression with its own braces) is tracked by depth;
    /// the codebase's actual holes are simple member-access arguments, so this is deliberately not a full C#
    /// expression parser.
    /// </summary>
    private static string StripLiteralTextKeepingInterpolationHoles(string body)
    {
        var sb = new System.Text.StringBuilder(body.Length);
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            var isInterpolated = c == '$';
            var isVerbatimPrefixed = c == '@';
            if (isInterpolated || isVerbatimPrefixed || c == '"')
            {
                var probe = i;
                var interpolated = false;
                var verbatim = false;
                if (body[probe] == '$')
                {
                    interpolated = true;
                    probe++;
                    if (probe < body.Length && body[probe] == '@') { verbatim = true; probe++; }
                }
                else if (body[probe] == '@')
                {
                    verbatim = true;
                    probe++;
                    if (probe < body.Length && body[probe] == '$') { interpolated = true; probe++; }
                }

                if (probe >= body.Length || body[probe] != '"')
                {
                    // A bare '@'/'$' that is not actually opening a string literal (e.g. an attribute-style
                    // '@' on a keyword identifier) — copy the one character through and keep scanning.
                    sb.Append(c);
                    i++;
                    continue;
                }

                i = probe + 1; // past the opening quote
                while (i < body.Length)
                {
                    if (interpolated && body[i] == '{')
                    {
                        if (i + 1 < body.Length && body[i + 1] == '{') { i += 2; continue; } // escaped '{{'
                        var holeStart = ++i;
                        var depth = 1;
                        while (i < body.Length && depth > 0)
                        {
                            if (body[i] == '{') depth++;
                            else if (body[i] == '}') depth--;
                            if (depth > 0) i++;
                        }
                        sb.Append(' ').Append(body, holeStart, i - holeStart).Append(' ');
                        if (i < body.Length) i++; // past the closing '}'
                        continue;
                    }
                    if (interpolated && body[i] == '}' && i + 1 < body.Length && body[i + 1] == '}')
                    { i += 2; continue; } // escaped '}}'
                    if (body[i] == '"')
                    {
                        if (verbatim && i + 1 < body.Length && body[i + 1] == '"') { i += 2; continue; } // "" escape
                        i++; // past the closing quote
                        break;
                    }
                    if (!verbatim && body[i] == '\\' && i + 1 < body.Length) { i += 2; continue; } // \x escape
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
