using System.IO;
using Pia.Helpers;
using Pia.Tests.TestInfrastructure;
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

    [Fact]
    public void IsPathInsideAnyVault_true_for_an_exact_match()
    {
        const string json = """{"vaults":{"bbb":{"path":"C:\\Users\\me\\Pia\\vault","ts":1}}}""";

        Assert.True(ObsidianLauncher.IsPathInsideAnyVault(json, "C:\\Users\\me\\Pia\\vault"));
    }

    // FindVaultId would miss this — no vault is filed under the note's own folder — but Obsidian's
    // path= form still resolves it, because it searches for the most specific containing vault.
    [Fact]
    public void IsPathInsideAnyVault_true_when_nested_under_a_registered_vault()
    {
        const string json = """{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""";

        Assert.True(ObsidianLauncher.IsPathInsideAnyVault(json, "C:\\Users\\me\\Notes\\Pia\\vault"));
    }

    [Fact]
    public void IsPathInsideAnyVault_false_when_no_registered_vault_contains_it()
    {
        const string json = """{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""";

        Assert.False(ObsidianLauncher.IsPathInsideAnyVault(json, "C:\\Users\\me\\Pia\\vault"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"vaults":[]}""")]
    [InlineData("""{"frames":{}}""")]
    public void IsPathInsideAnyVault_false_for_a_malformed_or_unexpected_registry(string json)
        => Assert.False(ObsidianLauncher.IsPathInsideAnyVault(json, "C:\\Users\\me\\Pia\\vault"));

    // Empty only: Obsidian leaves a zero-byte obsidian.json behind if it is interrupted mid-write, and
    // there is nothing there to lose. A vaults ARRAY is not Obsidian's format either, but the other
    // top-level keys around it still are, so those survive.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"vaults":[]}""")]
    public void AddVaultEntry_starts_a_fresh_vault_list_when_there_is_none_to_lose(string? existing)
    {
        var updated = ObsidianLauncher.AddVaultEntry(existing, "abc123", "C:\\Users\\me\\Pia\\vault", 1700000000000);

        Assert.Equal("abc123", ObsidianLauncher.FindVaultId(updated!, "C:\\Users\\me\\Pia\\vault"));
    }

    // The whole point of the merge: rewriting a registry that will not parse would drop every vault the
    // user has AND Obsidian's other top-level settings. Refusing is the only non-destructive answer.
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes"}""")] // truncated mid-write
    [InlineData("[]")]
    [InlineData("42")]
    public void AddVaultEntry_refuses_to_rewrite_a_registry_it_cannot_parse(string existing)
        => Assert.Null(ObsidianLauncher.AddVaultEntry(existing, "abc123", "C:\\Users\\me\\Pia\\vault", 1700000000000));

    [Fact]
    public void AddVaultEntry_keeps_the_top_level_keys_it_does_not_own()
    {
        const string json =
            """{"frames":[{"x":1}],"updateDisabled":true,"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""";

        var updated = ObsidianLauncher.AddVaultEntry(json, "bbb", "C:\\Users\\me\\Pia\\vault", 1700000000000);

        Assert.Contains("frames", updated);
        Assert.Contains("updateDisabled", updated);
        Assert.Equal("aaa", ObsidianLauncher.FindVaultId(updated!, "C:\\Users\\me\\Notes"));
    }

    [Fact]
    public void AddVaultEntry_leaves_every_other_vault_untouched()
    {
        const string json = """{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""";

        var updated = ObsidianLauncher.AddVaultEntry(json, "bbb", "C:\\Users\\me\\Pia\\vault", 1700000000000);

        Assert.Equal("aaa", ObsidianLauncher.FindVaultId(updated!, "C:\\Users\\me\\Notes"));
        Assert.Equal("bbb", ObsidianLauncher.FindVaultId(updated!, "C:\\Users\\me\\Pia\\vault"));
    }

    [Fact]
    public void AddVaultEntry_is_addressable_by_the_id_it_was_given()
    {
        var updated = ObsidianLauncher.AddVaultEntry(null, "abc123", "C:\\Users\\me\\Pia\\vault", 1700000000000);

        Assert.Equal(
            "obsidian://open?vault=abc123",
            ObsidianLauncher.ComposeUri("C:\\Users\\me\\Pia\\vault", null, ObsidianLauncher.FindVaultId(updated!, "C:\\Users\\me\\Pia\\vault")));
    }

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

    // TryRegisterVault edits a file Obsidian owns, so these run against a redirected registry — never the
    // developer's real %APPDATA%.
    [Fact]
    public void TryRegisterVault_refuses_when_obsidian_keeps_no_registry_here()
    {
        using var registry = new TempRegistry(content: null);

        Assert.False(ObsidianLauncher.TryRegisterVault("C:\\Users\\me\\Pia\\vault"));
        Assert.False(File.Exists(registry.RegistryPath));
    }

    [Fact]
    public void TryRegisterVault_leaves_a_registry_it_cannot_parse_byte_identical()
    {
        const string garbage = """{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes"}""";
        using var registry = new TempRegistry(garbage);

        Assert.False(ObsidianLauncher.TryRegisterVault("C:\\Users\\me\\Pia\\vault"));
        Assert.Equal(garbage, File.ReadAllText(registry.RegistryPath));
    }

    [Fact]
    public void TryRegisterVault_merges_into_an_existing_registry()
    {
        using var registry = new TempRegistry("""{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""");

        Assert.True(ObsidianLauncher.TryRegisterVault("C:\\Users\\me\\Pia\\vault"));

        var written = File.ReadAllText(registry.RegistryPath);
        Assert.Equal("aaa", ObsidianLauncher.FindVaultId(written, "C:\\Users\\me\\Notes"));
        Assert.NotNull(ObsidianLauncher.FindVaultId(written, "C:\\Users\\me\\Pia\\vault"));
        Assert.Equal(
            VaultRegistrationState.Registered,
            ObsidianLauncher.GetRegistrationState("C:\\Users\\me\\Pia\\vault"));
    }

    // A registry we cannot read is not evidence the vault is unregistered. Collapsing this into Registrable
    // would let the caller write an id into a file Obsidian never reads, after which every open fires a URI
    // it rejects; collapsing it into Registered would silently swallow the case instead of telling the user.
    [Fact]
    public void GetRegistrationState_is_undetermined_when_there_is_no_registry_to_consult()
    {
        using var registry = new TempRegistry(content: null);

        Assert.Equal(
            VaultRegistrationState.Undetermined,
            ObsidianLauncher.GetRegistrationState("C:\\Users\\me\\Pia\\vault"));
    }

    [Fact]
    public void GetRegistrationState_is_registrable_when_a_readable_registry_does_not_list_it()
    {
        using var registry = new TempRegistry("""{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""");

        Assert.Equal(
            VaultRegistrationState.Registrable,
            ObsidianLauncher.GetRegistrationState("C:\\Users\\me\\Pia\\vault"));
    }

    // Nested under a registered vault is what obsidian://open?path= resolves, so it needs no registration.
    [Fact]
    public void GetRegistrationState_is_registered_for_a_folder_inside_a_registered_vault()
    {
        using var registry = new TempRegistry("""{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""");

        Assert.Equal(
            VaultRegistrationState.Registered,
            ObsidianLauncher.GetRegistrationState("C:\\Users\\me\\Notes\\Pia\\vault"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetRegistrationState_is_undetermined_for_a_missing_vault_root(string? vaultRoot)
    {
        using var registry = new TempRegistry("""{"vaults":{"aaa":{"path":"C:\\Users\\me\\Notes","ts":1}}}""");

        Assert.Equal(VaultRegistrationState.Undetermined, ObsidianLauncher.GetRegistrationState(vaultRoot));
    }

    /// <summary>Redirects Obsidian's registry into a throwaway folder for the life of one test.</summary>
    private sealed class TempRegistry : IDisposable
    {
        private readonly string _dir;
        private readonly IDisposable _override;

        internal TempRegistry(string? content)
        {
            _dir = Path.Combine(Path.GetTempPath(), "pia-obsidian-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            RegistryPath = Path.Combine(_dir, "obsidian.json");
            if (content is not null) File.WriteAllText(RegistryPath, content);
            _override = ObsidianLauncher.OverrideRegistryPathForTests(RegistryPath);
        }

        internal string RegistryPath { get; }

        public void Dispose()
        {
            _override.Dispose();
            TempPath.Remove(_dir);
        }
    }

}
