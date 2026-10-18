using System.IO;
using System.Text.RegularExpressions;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Finds a uv tool's console script in the tool's own receipt and dist-info, never in a dependency's.</summary>
public static partial class UvEntryPointResolver
{
    private const string ReceiptName = "uv-receipt.toml";
    private const string DistInfoSuffix = ".dist-info";

    private static readonly char[] VersionStart = ['@', '[', '=', '<', '>', '~', '!'];

    private static readonly EnumerationOptions NoLinks = new() { AttributesToSkip = FileAttributes.ReparsePoint };

    [GeneratedRegex(@"[-_.]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[ \t]*entrypoints[ \t]*=[ \t]*\[", RegexOptions.Multiline)]
    private static partial Regex EntryPointsStart();

    [GeneratedRegex(@"(?<key>[A-Za-z0-9_-]+)\s*=\s*(?:""(?<value>(?:[^""\\]|\\.)*)""|'(?<value>[^']*)')")]
    private static partial Regex InlinePair();

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$")]
    private static partial Regex TrailingExtra();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex DottedIdentifier();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex CommandNamePattern();

    /// <summary>PEP 503: lowercase, every run of '-', '_' and '.' becomes one '-'.</summary>
    public static string NormalizeName(string name) => Separators().Replace(name, "-").ToLowerInvariant();

    public static (string ToolFolder, string Module, string Attr)? Find(string stateDirectory, UvxSpec spec)
    {
        var packageName = PackageName(spec.Package);
        if (packageName.Length == 0)
            return null;

        var toolFolder = Path.Combine(stateDirectory, "tools", NormalizeName(packageName));
        var from = ReceiptEntryPointSource(Path.Combine(toolFolder, ReceiptName), spec.CommandName);
        if (from is null)
            return null;

        var sitePackages = Path.Combine(toolFolder, "Lib", "site-packages");
        if (!Directory.Exists(sitePackages))
            return null;

        var distribution = NormalizeName(from);
        foreach (var distInfo in Directory.EnumerateDirectories(sitePackages, "*" + DistInfoSuffix, NoLinks))
        {
            if (DistributionOf(Path.GetFileName(distInfo)) is not { } name || NormalizeName(name) != distribution)
                continue;

            if (ConsoleScript(Path.Combine(distInfo, "entry_points.txt"), spec.CommandName) is { } entry)
                return (toolFolder, entry.Module, entry.Attr);
        }

        return null;
    }

    public static string PythonFor(string toolFolder) => Path.Combine(toolFolder, "Scripts", "python.exe");

    public static string Code(string commandName, string module, string attr)
    {
        if (!CommandNamePattern().IsMatch(commandName))
            throw new ArgumentException("Not a console script name.", nameof(commandName));
        if (!DottedIdentifier().IsMatch(module))
            throw new ArgumentException("Not a module name.", nameof(module));
        if (!DottedIdentifier().IsMatch(attr))
            throw new ArgumentException("Not an attribute name.", nameof(attr));

        return $"import sys, importlib, functools; sys.argv[0] = '{commandName}'; "
            + $"sys.exit(functools.reduce(getattr, '{attr}'.split('.'), importlib.import_module('{module}'))())";
    }

    private static string PackageName(string package)
    {
        var end = package.IndexOfAny(VersionStart);
        return (end < 0 ? package : package[..end]).Trim();
    }

    // Quote-aware, so a path inside install-path never poses as a key; stops at the array's closing bracket.
    private static string? ReceiptEntryPointSource(string receiptPath, string commandName)
    {
        var text = ReadText(receiptPath);
        if (text is null)
            return null;

        var start = EntryPointsStart().Match(text);
        if (!start.Success)
            return null;

        char? quote = null;
        var tableStart = -1;
        for (var i = start.Index + start.Length; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '\\' && quote == '"')
                    i++;
                else if (c == quote)
                    quote = null;
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '#':
                    while (i + 1 < text.Length && text[i + 1] != '\n')
                        i++;
                    break;
                case '{':
                    tableStart = i + 1;
                    break;
                case '}' when tableStart >= 0:
                    if (EntryPointSource(text[tableStart..i], commandName) is { } from)
                        return from;
                    tableStart = -1;
                    break;
                case ']' when tableStart < 0:
                    return null;
            }
        }

        return null;
    }

    private static string? EntryPointSource(string table, string commandName)
    {
        string? name = null;
        string? from = null;
        foreach (Match pair in InlinePair().Matches(table))
        {
            var value = pair.Groups["value"].Value;
            switch (pair.Groups["key"].Value)
            {
                case "name":
                    name = value;
                    break;
                case "from":
                    from = value;
                    break;
            }
        }

        return name == commandName && !string.IsNullOrWhiteSpace(from) ? from : null;
    }

    private static string? DistributionOf(string? folderName)
    {
        if (folderName is null || !folderName.EndsWith(DistInfoSuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = folderName[..^DistInfoSuffix.Length];
        var versionSeparator = stem.LastIndexOf('-');
        return versionSeparator > 0 ? stem[..versionSeparator] : null;
    }

    private static (string Module, string Attr)? ConsoleScript(string entryPointsPath, string commandName)
    {
        var text = ReadText(entryPointsPath);
        if (text is null)
            return null;

        var inConsoleScripts = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inConsoleScripts = line == "[console_scripts]";
                continue;
            }

            var equals = line.IndexOf('=');
            if (!inConsoleScripts || equals < 0 || line[..equals].Trim() != commandName)
                continue;

            var target = TrailingExtra().Replace(line[(equals + 1)..], "").Trim();
            var colon = target.IndexOf(':');
            if (colon < 0)
                return null;

            var module = target[..colon].Trim();
            var attr = target[(colon + 1)..].Trim();
            return DottedIdentifier().IsMatch(module) && DottedIdentifier().IsMatch(attr) ? (module, attr) : null;
        }

        return null;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
