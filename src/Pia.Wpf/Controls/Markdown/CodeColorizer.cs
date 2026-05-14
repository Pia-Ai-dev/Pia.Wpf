using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Documents;
using System.Windows.Media;
using ColorCode;
using ColorCode.Compilation;
using ColorCode.Common;
using ColorCode.Parsing;
using ColorCode.Styling;

namespace Pia.Controls.Markdown;

internal sealed class CodeColorizer : CodeColorizerBase
{
    private static readonly ILanguageParser _parser = BuildParser();

    private readonly List<Run> _runs = new();

    public CodeColorizer() : base(StyleDictionary.DefaultDark, _parser) { }

    public IReadOnlyList<Run> Highlight(string code, ILanguage? language)
    {
        _runs.Clear();
        if (string.IsNullOrEmpty(code))
        {
            return _runs;
        }

        if (language is null)
        {
            _runs.Add(BuildRun(code, scopeName: null));
            return _runs;
        }

        languageParser.Parse(code, language, (parsedSourceCode, scopes) => Write(parsedSourceCode, scopes));
        return _runs;
    }

    protected override void Write(string parsedSourceCode, IList<Scope> scopes)
    {
        if (string.IsNullOrEmpty(parsedSourceCode))
        {
            return;
        }

        var boundaries = new SortedSet<int> { 0, parsedSourceCode.Length };
        foreach (var scope in scopes)
        {
            CollectBoundaries(scope, boundaries);
        }

        var ordered = boundaries.ToArray();
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            var start = ordered[i];
            var end = ordered[i + 1];
            if (end <= start) continue;

            var scopeName = FindInnermostScope(scopes, start, end);
            var slice = parsedSourceCode.Substring(start, end - start);
            _runs.Add(BuildRun(slice, scopeName));
        }
    }

    private static void CollectBoundaries(Scope scope, SortedSet<int> sink)
    {
        sink.Add(scope.Index);
        sink.Add(scope.Index + scope.Length);
        if (scope.Children is null) return;
        foreach (var child in scope.Children)
        {
            CollectBoundaries(child, sink);
        }
    }

    private static string? FindInnermostScope(IList<Scope> scopes, int start, int end)
    {
        string? best = null;
        var bestLength = int.MaxValue;
        foreach (var scope in scopes)
        {
            var match = MatchScope(scope, start, end, bestLength);
            if (match is not null)
            {
                best = match.Value.name;
                bestLength = match.Value.length;
            }
        }
        return best;
    }

    private static (string name, int length)? MatchScope(Scope scope, int start, int end, int currentBestLength)
    {
        if (start < scope.Index || end > scope.Index + scope.Length)
        {
            return null;
        }

        (string name, int length)? best = scope.Length < currentBestLength
            ? (scope.Name, scope.Length)
            : null;

        if (scope.Children is not null)
        {
            foreach (var child in scope.Children)
            {
                var childMatch = MatchScope(child, start, end, best?.length ?? currentBestLength);
                if (childMatch is not null)
                {
                    best = childMatch;
                }
            }
        }

        return best;
    }

    private static Run BuildRun(string text, string? scopeName)
    {
        var run = new Run(text);
        if (CodeBlockPalette.ResolveBrush(scopeName ?? string.Empty) is Brush brush)
        {
            run.Foreground = brush;
        }
        return run;
    }

    private static ILanguageParser BuildParser()
    {
        var languageDict = Languages.All.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        var repository = new LanguageRepository(languageDict);
        var compiler = new LanguageCompiler(new Dictionary<string, CompiledLanguage>(StringComparer.OrdinalIgnoreCase), new ReaderWriterLockSlim());
        return new LanguageParser(compiler, repository);
    }

    public static ILanguage? ResolveLanguage(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        var normalized = hint.Trim().ToLowerInvariant();
        var alias = normalized switch
        {
            "c#" or "cs" or "csharp" or "dotnet" => "csharp",
            "js" or "node" or "nodejs" => "javascript",
            "ts" or "tsx" => "typescript",
            "py" or "python3" => "python",
            "xaml" or "wpf" or "axaml" => "xml",
            "ps" or "ps1" or "pwsh" or "powershell" => "powershell",
            "c++" => "cpp",
            "vb" or "vbnet" => "vb.net",
            _ => normalized,
        };

        return Languages.FindById(alias) ?? Languages.FindById(normalized);
    }
}
