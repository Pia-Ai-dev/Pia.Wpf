using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

public sealed class ShellLauncherTests
{
    [Theory]
    [InlineData(@"C:\work\setup.exe")]
    [InlineData(@"C:\work\run.PS1")]
    [InlineData(@"C:\work\clickonce.appref-ms")]
    [InlineData(@"C:\work\shortcut.url")]
    [InlineData(@"C:\work\panel.settingcontent-ms")]
    [InlineData(@"C:\work\help.chm")]
    [InlineData(@"C:\work\gui.pyw")]
    [InlineData(@"C:\work\troubleshoot.diagcab")]
    [InlineData(@"C:\work\remote.rdp")]
    [InlineData(@"C:\work\package.msix")]
    [InlineData(@"C:\work\launch.jnlp")]
    public void Types_the_shell_would_run_are_revealed_not_opened(string path)
        => Assert.True(ShellLauncher.IsExecutable(path));

    [Theory]
    [InlineData(@"C:\work\report.html")]
    [InlineData(@"C:\work\notes.md")]
    [InlineData(@"C:\work\script.py")]
    [InlineData(@"C:\work\Program.cs")]
    [InlineData(@"C:\work\sheet.xlsx")]
    [InlineData(@"C:\work\noextension")]
    public void Documents_and_source_files_open_normally(string path)
        => Assert.False(ShellLauncher.IsExecutable(path));
}
