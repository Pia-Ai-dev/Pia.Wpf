using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pia.Logging;

/// <summary>
/// Scrubs a <c>pia-*.log</c> on its way into a diagnostics export. The log file itself is left exactly as
/// written: 523 call sites hand an exception to LogError/LogWarning, so its Message and stack trace are in
/// there by design, and rewriting all of them would cost more than it buys.
/// </summary>
public static class LogRedactor
{
    public const string DebugBody = "R01_DEBUG_BODY";
    public const string ResponseBody = "R02_RESPONSE_BODY";
    public const string ProcessAndWindow = "R03_PROCESS_AND_WINDOW";
    public const string ProfileRoots = "R04_PROFILE_ROOTS";
    public const string MachineName = "R05_MACHINE_NAME";
    public const string UserName = "R06_USER_NAME";
    public const string Url = "R07_URL";
    public const string HostLiterals = "R08_HOST_LITERALS";
    public const string ProviderNames = "R09_PROVIDER_NAMES";
    public const string Email = "R10_EMAIL";
    public const string Credentials = "R11_CREDENTIALS";
    public const string AbsolutePath = "R12_ABSOLUTE_PATH";

    /// <summary>The rule set, in application order. The order is load-bearing — see the export doc.</summary>
    public static IReadOnlyList<RedactionRuleDescriptor> Descriptors { get; } =
    [
        new(DebugBody, RedactionTier.Deterministic,
            "every DBUG/TRCE message body, which is where the whole Conditional(DEBUG) Sensitive* family lands"),
        new(ResponseBody, RedactionTier.BestEffort,
            "a provider response body quoted into an exception message"),
        new(ProcessAndWindow, RedactionTier.BestEffort,
            "foreground process, window class, and the window title interpolated into a restore failure"),
        new(ProfileRoots, RedactionTier.Deterministic,
            "the roaming, local and user profile roots, including inside stack frames"),
        new(MachineName, RedactionTier.Deterministic,
            "the machine name and any DNS suffix following it"),
        new(UserName, RedactionTier.Deterministic,
            "the account name where it appears outside a profile path"),
        new(Url, RedactionTier.BestEffort,
            "any http(s) URL, whole - path, query and fragment included"),
        new(HostLiterals, RedactionTier.Deterministic,
            "a configured server or provider host appearing outside a URL"),
        new(ProviderNames, RedactionTier.Deterministic,
            "user-chosen provider names"),
        new(Email, RedactionTier.BestEffort,
            "email addresses"),
        new(Credentials, RedactionTier.BestEffort,
            "bearer tokens, api-key assignments, JWTs, known key prefixes, credential query parameters"),
        new(AbsolutePath, RedactionTier.BestEffort,
            "the directory part of any remaining absolute or UNC path, leaf preserved"),
    ];

    public static IReadOnlyList<string> RuleIds { get; } = [.. Descriptors.Select(d => d.Id)];

    /// <summary>NReco's level abbreviations. A line whose level field is not one of these is a continuation.</summary>
    private static readonly string[] Levels = ["TRCE", "DBUG", "INFO", "WARN", "FAIL", "CRIT"];

    private const string DroppedMarker = "<debug-payload-dropped>";

    private static readonly Regex ResponseBodyPattern =
        new(@"(failed \(\d{3}\): )\{[^\r\n]*", RegexOptions.Compiled);

    private static readonly Regex ProcessQuotedPattern =
        new(@"process='[^']*', class='[^']*'", RegexOptions.Compiled);

    private static readonly Regex ProcessParenPattern =
        new(@"\(process: [^)]*\)", RegexOptions.Compiled);

    // OutputService interpolates the window title into the exception message that OptimizeViewModel logs at
    // Warning, so the title reaches a release log even though the same value is SensitiveDebug two lines up.
    private static readonly Regex RestoreFailurePattern =
        new(@"(Failed to restore previous window ')[^']*(' \()[^)]*(\))", RegexOptions.Compiled);

    private static readonly Regex UrlPattern =
        new(@"https?://[^\s,;""')\]<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AlreadyHostCoded =
        new(@"^host-\d{3}$", RegexOptions.Compiled);

    private static readonly Regex EmailPattern =
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled);

    // A \b anchor fails on the underscore in GITHUB_TOKEN=, which is the commonest real shape, and the
    // delimiter has to allow the space in "api_key: value".
    private static readonly Regex CredentialAssignmentPattern = new(
        @"(?<![A-Za-z0-9])(bearer\s+|basic\s+|authorization\s*[:=]\s*|api[_\-]?key\s*[:=""']+\s*" +
        @"|token\s*[:=""']+\s*|secret\s*[:=""']+\s*|password\s*[:=""']+\s*)([A-Za-z0-9._\-+/=~]{16,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CredentialQueryPattern = new(
        @"([?&](?:p|code|key|token|secret|password|access_token|refresh_token|sig)=)[^&\s""')\]]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JwtPattern =
        new(@"(?<![A-Za-z0-9])eyJ[A-Za-z0-9._\-]{20,}", RegexOptions.Compiled);

    private static readonly Regex KeyPrefixPattern = new(
        @"(?<![A-Za-z0-9])(?:sk|pk|ghp|gho|ghs|ghu|hf|glpat|xoxb|xoxp|AKIA)[-_][A-Za-z0-9._\-]{16,}",
        RegexOptions.Compiled);

    private static readonly Regex DriveDirectoryPattern =
        new(@"(?<!\w)[A-Za-z]:[\\/](?:[^\\/\r\n:*?""<>|]+[\\/])+", RegexOptions.Compiled);

    // Anchored so a JSON-escaped C:\\Users\\ - whose doubled separator follows a colon - cannot be read as
    // a UNC head.
    private static readonly Regex UncHeadPattern =
        new(@"(?<![\w\\/:])\\\\[A-Za-z0-9._\-]+\\", RegexOptions.Compiled);

    // Emits the separator it matched, so a forward-slash path does not come back mixed.
    private static readonly Regex TokenisedDirectoryPattern = new(
        @"(<profile-(?:roaming|local|user)>)([\\/])(?:[^\\/\r\n:*?""<>|]+[\\/])+", RegexOptions.Compiled);

    private static readonly Regex MachineSuffixPattern =
        new(@"<machine>(\.[A-Za-z0-9\-]+)+", RegexOptions.Compiled);

    /// <summary>
    /// Streams <paramref name="source"/> into <paramref name="destination"/>, applying every rule. Neither
    /// stream is disposed — the caller owns the zip entry.
    /// </summary>
    public static RedactionSummary Redact(Stream source, Stream destination, RedactionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(keys);

        var rules = BuildRules(keys);
        var hits = RuleIds.ToDictionary(id => id, _ => 0L);
        long linesRead = 0, linesWritten = 0, recordsDropped = 0;

        using var reader = new StreamReader(
            source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);
        var writer = new StreamWriter(
            destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024, leaveOpen: true) { NewLine = "\r\n" };

        // Starts CLOSED: a file whose first bytes are the tail of a dropped payload must not emit that
        // fragment merely because no record has been parsed yet.
        var dropping = true;

        while (reader.ReadLine() is { } line)
        {
            linesRead++;
            var fields = line.Split('\t', 5);

            if (!IsRecord(fields))
            {
                // A continuation line under a dropped record is omitted outright, and no rule runs on it.
                if (dropping)
                    continue;
                writer.WriteLine(Apply(rules, line, hits));
                linesWritten++;
                continue;
            }

            if (fields[1] is "DBUG" or "TRCE")
            {
                dropping = true;
                recordsDropped++;
                hits[DebugBody]++;
                writer.WriteLine($"{fields[0]}\t{fields[1]}\t{fields[2]}\t{fields[3]}\t{DroppedMarker}");
                linesWritten++;
                continue;
            }

            dropping = false;
            writer.WriteLine(
                $"{fields[0]}\t{fields[1]}\t{fields[2]}\t{fields[3]}\t{Apply(rules, fields[4], hits)}");
            linesWritten++;
        }

        writer.Flush();
        return new RedactionSummary(linesRead, linesWritten, recordsDropped, hits);
    }

    /// <summary>
    /// A stable three-digit code for a host, so support can correlate "every host-473 call fails" across
    /// exports without learning the host. Same formula as <see cref="SafeUrl"/>'s release arm, but
    /// unconditional — an export has to scrub in a debug build too.
    /// </summary>
    public static string HostCode(string? host)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes((host ?? string.Empty).ToLowerInvariant()), hash);
        return (BitConverter.ToUInt64(hash[..8]) % 1000).ToString("D3", CultureInfo.InvariantCulture);
    }

    private static bool IsRecord(string[] fields) =>
        fields.Length == 5
        && LooksLikeTimestamp(fields[0])
        && Array.IndexOf(Levels, fields[1]) >= 0
        && IsBracketed(fields[2])
        && IsBracketed(fields[3]);

    // Field 3 is [0] for most records but a named framework event id for 79,204 of them, so the bracket
    // shape is all that can be required.
    private static bool IsBracketed(string field) =>
        field.Length >= 2 && field[0] == '[' && field[^1] == ']';

    private static bool LooksLikeTimestamp(string field) =>
        field.Length >= 19 && field[4] == '-' && field[7] == '-' && field[10] == 'T'
        && field[13] == ':' && field[16] == ':'
        && DateTimeOffset.TryParse(
            field, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static string Apply(
        List<(string Id, Func<string, string> Run)> rules, string text, Dictionary<string, long> hits)
    {
        foreach (var (id, run) in rules)
        {
            var next = run(text);
            if (!string.Equals(next, text, StringComparison.Ordinal))
                hits[id]++;
            text = next;
        }

        return text;
    }

    private static List<(string Id, Func<string, string> Run)> BuildRules(RedactionKeys keys)
    {
        // Longest first: the profile roots CONTAIN the user name, so replacing the name first would leave
        // the whole directory structure standing with a single segment swapped.
        var rootPatterns = new (string? Key, string Token)[]
            {
                (keys.RoamingRoot, "<profile-roaming>"),
                (keys.LocalRoot, "<profile-local>"),
                (keys.UserProfileRoot, "<profile-user>"),
            }
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .SelectMany(r => SeparatorForms(r.Key!).Select(form => (Form: form, r.Token)))
            .OrderByDescending(r => r.Form.Length)
            .Select(r => (Pattern: Boundaried(r.Form), r.Token))
            .ToList();

        var hostPatterns = keys.Hosts
            .Where(h => !string.IsNullOrWhiteSpace(h) && h.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(h => h.Length)
            .Select(h => (Pattern: HostLiteral(h), Token: $"host-{HostCode(h)}"))
            .ToList();

        // Indexed by the position as PASSED, not as sorted, so the token is stable across the whole export
        // and support can still see that all 550 failures name the same provider.
        var providerPatterns = keys.ProviderNames
            .Select((Name, Index) => (Name, Index))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length >= 4)
            .OrderByDescending(p => p.Name.Length)
            .Select(p => (Pattern: Boundaried(p.Name), Token: $"<provider-{p.Index}>"))
            .ToList();

        return
        [
            (ResponseBody, text => ResponseBodyPattern.Replace(text, "$1<response-body>")),
            (ProcessAndWindow, text =>
            {
                text = ProcessQuotedPattern.Replace(text, "process='<process>', class='<window-class>'");
                text = ProcessParenPattern.Replace(text, "(process: <process>)");
                return RestoreFailurePattern.Replace(text, "$1<window-title>$2<process>$3");
            }),
            (ProfileRoots, text => ReplaceAll(rootPatterns, text)),
            (MachineName, text => MachineSuffixPattern.Replace(
                ReplaceKey(keys.MachineName, "<machine>", text), "<machine>")),
            (UserName, text => ReplaceKey(keys.UserName, "<user>", text)),
            (Url, text => UrlPattern.Replace(text, CollapseUrl)),
            (HostLiterals, text => ReplaceAll(hostPatterns, text)),
            (ProviderNames, text => ReplaceAll(providerPatterns, text)),
            (Email, text => EmailPattern.Replace(text, "<email>")),
            (Credentials, text =>
            {
                text = CredentialAssignmentPattern.Replace(text, "$1<token>");
                text = CredentialQueryPattern.Replace(text, "$1<token>");
                text = JwtPattern.Replace(text, "<token>");
                return KeyPrefixPattern.Replace(text, "<token>");
            }),
            (AbsolutePath, text =>
            {
                text = DriveDirectoryPattern.Replace(text, "<path>\\");
                text = UncHeadPattern.Replace(text, "<unc>\\");
                return TokenisedDirectoryPattern.Replace(text, "$1$2<path>$2");
            }),
        ];
    }

    private static string CollapseUrl(Match match)
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
            return $"<url:opaque://host-{HostCode(match.Value)}>";

        var host = AlreadyHostCoded.IsMatch(uri.Host) ? uri.Host : $"host-{HostCode(uri.Host)}";
        return $"<url:{uri.Scheme}://{host}>";
    }

    /// <summary>Both forms a root can appear in: Path.Combine writes backslashes, a stack frame can carry either.</summary>
    private static IEnumerable<string> SeparatorForms(string key)
    {
        yield return key;
        var flipped = key.Replace('\\', '/');
        if (!string.Equals(flipped, key, StringComparison.Ordinal))
            yield return flipped;
    }

    private static Regex Boundaried(string value) =>
        new(@"(?<![A-Za-z0-9])" + Regex.Escape(value) + "(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

    // A port is diagnostically load-bearing and is not user data, so the boundary stops before it.
    private static Regex HostLiteral(string host) =>
        new(@"(?<![\w.\-])" + Regex.Escape(host) + @"(?![\w.\-])", RegexOptions.IgnoreCase);

    private static string ReplaceAll(List<(Regex Pattern, string Token)> patterns, string text)
    {
        foreach (var (pattern, token) in patterns)
            text = pattern.Replace(text, token);
        return text;
    }

    // Under four characters a name is more likely to collide with ordinary prose than to identify anyone.
    private static string ReplaceKey(string? key, string token, string text) =>
        string.IsNullOrWhiteSpace(key) || key.Length < 4 ? text : Boundaried(key).Replace(text, token);
}
