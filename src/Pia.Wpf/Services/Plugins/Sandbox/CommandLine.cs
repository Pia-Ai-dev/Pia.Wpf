using System.Text;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Builds and splits a Windows command line with the CommandLineToArgvW quoting rules.</summary>
internal static class CommandLine
{
    /// <summary>Applies the argument rules to every token, the first included; a doubled quote inside quotes ends the quoted run.</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quoted = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (!quoted && c is ' ' or '\t')
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
                i++;
                continue;
            }

            inToken = true;
            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                }
                continue;
            }

            if (c == '"')
            {
                if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    quoted = false;
                    i += 2;
                    continue;
                }

                quoted = !quoted;
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (inToken)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>The executable is always quoted; an argument only when it is empty or holds whitespace or a quote.</summary>
    public static string Build(string executable, IEnumerable<string> args)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(executable).Append('"');
        foreach (var arg in args)
        {
            builder.Append(' ');
            AppendArgument(builder, arg);
        }
        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            builder.Append(arg);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            else
                builder.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
    }
}
