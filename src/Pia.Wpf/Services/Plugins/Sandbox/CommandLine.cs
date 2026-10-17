using System.Text;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Builds a Windows command line with the CommandLineToArgvW quoting rules.</summary>
internal static class CommandLine
{
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
