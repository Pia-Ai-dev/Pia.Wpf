using System.Runtime.InteropServices;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class CommandLineTests
{
    [Fact]
    public void ExecutableIsAlwaysQuoted()
    {
        Assert.Equal("\"C:\\srv\\a.exe\"", CommandLine.Build(@"C:\srv\a.exe", []));
    }

    [Theory]
    [InlineData("--db", "--db")]
    [InlineData(@"C:\a\b.json", @"C:\a\b.json")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    public void ArgumentQuoting(string arg, string expected)
    {
        Assert.Equal("\"x.exe\" " + expected, CommandLine.Build("x.exe", [arg]));
    }

    [Fact]
    public void MultipleArguments_AreSpaceSeparated()
    {
        Assert.Equal("\"C:\\p f\\s.exe\" --db \"C:\\d d\\c.json\" --notes C:\\n",
            CommandLine.Build(@"C:\p f\s.exe", ["--db", @"C:\d d\c.json", "--notes", @"C:\n"]));
    }

    /// <summary>The child parses its command line with the real Windows rules, so the round trip goes through them.</summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData(@"C:\trailing\")]
    [InlineData(@"C:\trailing dir\")]
    [InlineData("quote\"inside")]
    [InlineData("back\\\\\"slash")]
    [InlineData("")]
    public void RoundTripsThroughCommandLineToArgvW(string arg)
    {
        var parsed = Split(CommandLine.Build(@"C:\x.exe", [arg, "next"]));

        Assert.Equal([@"C:\x.exe", arg, "next"], parsed);
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        try
        {
            return [.. Enumerable.Range(0, argc)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }
}
