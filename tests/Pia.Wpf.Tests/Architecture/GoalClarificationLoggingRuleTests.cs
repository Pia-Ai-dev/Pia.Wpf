using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary><c>SensitiveDebug</c> is <c>[Conditional("DEBUG")]</c> and emits like any other call in a Debug test run, so no assertion over sink output can tell it apart from <c>LogInformation</c>; this scans the real source text instead, for a plain logger call that carries the clarification question's own text.</summary>
public class GoalClarificationLoggingRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    // Identifiers that carry the question's own text, not merely whether one was asked.
    private static readonly Regex QuestionIdentifier =
        new(@"\b(ClarificationQuestion|question)\b", RegexOptions.IgnoreCase);

    // _logger is this codebase's convention, but a bare `logger` receiver is matched too, just in case.
    private static readonly Regex PlainLoggerCall =
        new(@"\b(?:_logger|logger)\.Log(Information|Warning|Error|Critical|Debug|Trace)\s*\(", RegexOptions.Singleline);

    [Theory]
    [InlineData("Services/AgentPlanner.cs")]
    [InlineData("Services/AgentRunOrchestrator.cs")]
    // These files also see the question's raw text but only log counts, ids and tokens around it.
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

        // Non-vacuity: confirms at least one plain Log* call exists, so a rename that broke the regex
        // wouldn't leave this fact vacuously green.
        Assert.True(callsChecked > 0, $"{relativePath}: expected at least one plain Log* call to scan; found none");
    }

    /// <summary><c>UserInputRequestStore</c> holds the mid-plan question's own text and takes no <c>ILogger</c> at all, asserted directly since the Theory's non-vacuity guard requires at least one plain Log* call per file.</summary>
    [Fact]
    public void TheAskSinkTakesNoLoggerAtAll()
    {
        var source = ReadSource("Services/UserInputRequestStore.cs");

        Assert.DoesNotContain(PlainLoggerCall.Matches(source).Cast<Match>(), _ => true);
        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        // Non-vacuity: this really is the file that holds the question, not an empty read.
        Assert.Contains("public string? Question", source, StringComparison.Ordinal);
    }

    /// <summary>An identifier inside an interpolation hole must survive stripping, so a leak riding a hole (e.g. <c>$"...{question}"</c>) is still detected instead of vanishing with the rest of the literal.</summary>
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

    /// <summary><c>LogCritical</c> and a bare <c>logger</c> receiver must also match, so calls shaped either way aren't invisible to the scan.</summary>
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

    // Matches parens by depth so a call spread across multiple lines is scanned as one unit.
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

    /// <summary>Strips string literal text (plain/verbatim/interpolated) but keeps an interpolated literal's <c>{expr}</c> hole content, so a leak riding a hole is still visible to <see cref="QuestionIdentifier"/> afterwards.</summary>
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
