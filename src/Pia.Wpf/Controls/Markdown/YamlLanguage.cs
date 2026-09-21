using System;
using System.Collections.Generic;
using ColorCode;
using ColorCode.Common;

namespace Pia.Controls.Markdown;

/// <summary>ColorCode ships no YAML grammar, so Pia carries a small one — YAML is what the assistant
/// writes most and it was the only fenced language rendering monochrome.</summary>
internal sealed class YamlLanguage : ILanguage
{
    public string Id => "yaml";
    public string Name => "YAML";
    public string CssClassName => "yaml";
    public string FirstLinePattern => string.Empty;

    public bool HasAlias(string lang) => lang.Equals("yml", StringComparison.OrdinalIgnoreCase);

    public IList<LanguageRule> Rules => _rules;

    // Order is the tie-breaker when two rules can start at the same character: quoting wins over
    // everything, so a '#' or a ':' inside a string is not read as a comment or a key. Every
    // line-end lookahead tolerates a carriage return — a chat message may be stored CRLF.
    private static readonly List<LanguageRule> _rules =
    [
        new LanguageRule(
            @"""(?:[^""\x5C\r\n]|\x5C.)*""",
            new Dictionary<int, string> { { 0, ScopeName.String } }),
        new LanguageRule(
            @"'(?:[^'\r\n]|'')*'",
            new Dictionary<int, string> { { 0, ScopeName.String } }),
        new LanguageRule(
            @"#[^\r\n]*",
            new Dictionary<int, string> { { 0, ScopeName.Comment } }),
        new LanguageRule(
            @"(?m)^[ \t]*(?:-[ \t]+)?([A-Za-z0-9_.$/-]+)(?=[ \t]*:(?:[ \t\r]|$))",
            new Dictionary<int, string> { { 1, ScopeName.JsonKey } }),
        new LanguageRule(
            @"(?m)^[ \t]*(---|[.]{3})[ \t]*\r?$",
            new Dictionary<int, string> { { 1, ScopeName.Keyword } }),
        new LanguageRule(
            @"[&*][A-Za-z0-9_.-]+|![A-Za-z0-9_.!/-]+|[|>][-+]?(?=[ \t]*(?:#|\r?$))",
            new Dictionary<int, string> { { 0, ScopeName.Keyword } }),
        new LanguageRule(
            @"\b(true|false|null|True|False|Null|TRUE|FALSE|NULL|yes|no|Yes|No|on|off|On|Off)\b",
            new Dictionary<int, string> { { 1, ScopeName.JsonConst } }),
        new LanguageRule(
            @"-?\b\d+(?:[.]\d+)?(?:[eE][-+]?\d+)?\b",
            new Dictionary<int, string> { { 0, ScopeName.JsonNumber } }),
    ];
}
