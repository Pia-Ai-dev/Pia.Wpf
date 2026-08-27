using System.IO;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Covers the deterministic parts of the launcher — the note-type gate, the URI it hands Obsidian, the
/// vault lookup in <c>obsidian.json</c> and the protocol-command parse. Detection and icon extraction
/// depend on the machine's Obsidian install and are not covered.
/// </summary>
public sealed class ObsidianLauncherTests
{
    [Theory]
    [InlineData("memory/topics/ada.md")]
    [InlineData("C:\\Users\\me\\Pia\\vault\\memory\\profile.MD")] // case-insensitive extension match
    [InlineData("notes.markdown")]
    public void IsMarkdownNote_TrueForMarkdown(string path)
        => Assert.True(ObsidianLauncher.IsMarkdownNote(path));

    [Theory]
    [InlineData("sources/report.pdf")]
    [InlineData("sources/sheet.xlsx")]
    [InlineData("memory/topics/noextension")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMarkdownNote_FalseForEverythingElse(string? path)
        => Assert.False(ObsidianLauncher.IsMarkdownNote(path));

    [Fact]
    public void ComposeUri_addresses_a_registered_vault_by_its_id()
        => Assert.Equal(
            "obsidian://open?vault=abc123",
            ObsidianLauncher.ComposeUri("C:\\Users\\me\\Pia\\vault", null, "abc123"));

    [Fact]
    public void ComposeUri_percent_encodes_the_vault_relative_note_path()
        => Assert.Equal(
            "obsidian://open?vault=abc123&file=memory%2Ftopics%2Fada%20lovelace.md",
            ObsidianLauncher.ComposeUri("C:\\Users\\me\\Pia\\vault", "memory/topics/ada lovelace.md", "abc123"));

    [Theory]
    [InlineData("\\memory/topics/ada.md")]
    [InlineData("memory\\topics\\ada.md")]
    [InlineData("  memory/topics/ada.md  ")]
    public void ComposeUri_normalizes_separators_and_leading_slashes(string reference)
        => Assert.Equal(
            "obsidian://open?vault=v&file=memory%2Ftopics%2Fada.md",
            ObsidianLauncher.ComposeUri("C:\\vault", reference, "v"));

    // No vault id means Obsidian has not been told about this folder; the absolute-path form still opens the
    // note when it sits inside a vault the user added under a parent.
    [Fact]
    public void ComposeUri_falls_back_to_the_absolute_path_when_the_vault_is_unregistered()
    {
        var uri = ObsidianLauncher.ComposeUri("C:\\Users\\me\\Pia\\vault", "memory/topics/ada.md", null);

        Assert.StartsWith("obsidian://open?path=", uri, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(Path.Combine("C:\\Users\\me\\Pia\\vault", "memory", "topics", "ada.md")),
            uri, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeUri_falls_back_to_the_vault_root_when_no_note_is_given()
        => Assert.Equal(
            $"obsidian://open?path={Uri.EscapeDataString("C:\\Users\\me\\Pia\\vault")}",
            ObsidianLauncher.ComposeUri("C:\\Users\\me\\Pia\\vault", null, null));

    [Fact]
    public void FindVaultId_matches_the_registered_path_ignoring_case_and_a_trailing_separator()
    {
        const string json = """
            {"vaults":{
              "aaa":{"path":"C:\\Users\\me\\Notes","ts":1},
              "bbb":{"path":"c:\\users\\me\\pia\\vault\\","ts":2}
            }}
            """;

        Assert.Equal("bbb", ObsidianLauncher.FindVaultId(json, "C:\\Users\\me\\Pia\\vault"));
    }

    [Fact]
    public void FindVaultId_returns_null_when_the_vault_is_not_registered()
    {
        const string json = """{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""";

        Assert.Null(ObsidianLauncher.FindVaultId(json, "C:\\Users\\me\\Pia\\vault"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"vaults":[]}""")]
    [InlineData("""{"vaults":{"aaa":{"ts":1}}}""")]
    [InlineData("""{"frames":{}}""")]
    public void FindVaultId_returns_null_for_a_malformed_or_unexpected_registry(string json)
        => Assert.Null(ObsidianLauncher.FindVaultId(json, "C:\\Users\\me\\Pia\\vault"));

    [Theory]
    [InlineData("\"C:\\Program Files\\Obsidian\\Obsidian.exe\" \"%1\"", "C:\\Program Files\\Obsidian\\Obsidian.exe")]
    [InlineData("C:\\Obsidian\\Obsidian.exe \"%1\"", "C:\\Obsidian\\Obsidian.exe")]
    [InlineData("  \"C:\\Obsidian\\Obsidian.exe\"  ", "C:\\Obsidian\\Obsidian.exe")]
    public void ExtractExecutable_reads_the_exe_out_of_a_registered_shell_command(string command, string expected)
        => Assert.Equal(expected, ObsidianLauncher.ExtractExecutable(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    [InlineData("rundll32 shell32.dll,OpenAs_RunDLL %1")] // no .exe to cut at
    public void ExtractExecutable_returns_null_for_a_command_it_cannot_parse(string? command)
        => Assert.Null(ObsidianLauncher.ExtractExecutable(command));
}
