using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models.Vault;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The Memory view drives off the vault: <see cref="IMemoryService.ListMemoriesAsync"/> items grouped
/// by the §8 canonical order (alpha within a group), header metrics from the same snapshot, and
/// edit/delete routed through the vault verbs
/// <see cref="IMemoryService.UpdateSectionAsync"/> / <see cref="IMemoryService.ForgetAsync"/>.
/// </summary>
public class MemoryViewModelTests
{
    private static (MemoryViewModel Vm, IMemoryService Memory, IDialogService Dialog) Create(
        VaultMemoryItem[] items, long bytes = 0)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot(items, bytes));

        var dialog = Substitute.For<IDialogService>();
        var vm = new MemoryViewModel(
            NullLogger<MemoryViewModel>.Instance,
            memory,
            Substitute.For<IEmbeddingService>(),
            dialog,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<IClipboardService>());
        return (vm, memory, dialog);
    }

    private static VaultMemoryItem Item(string reference, string filePath, string type, string title, string body = "body")
        => new(reference, filePath, type, title, body, null);

    [Fact]
    public async Task LoadMemories_groups_in_canonical_order_with_items_alpha_and_wires_stats()
    {
        var items = new[]
        {
            Item("memory/notes/z.md", "memory/notes/z.md", "note", "Zebra note"),
            Item("memory/notes/a.md", "memory/notes/a.md", "note", "Apple note"),
            Item("memory/contacts.md#John", "memory/contacts.md", "contact_list", "John"),
            Item("memory/profile.md#Coffee", "memory/profile.md", "personal_profile", "Coffee"),
        };
        var (vm, _, _) = Create(items, bytes: 200);

        await vm.OnNavigatedToAsync(null);

        // §8 canonical order: Personal Profile, Contacts, ..., Notes.
        Assert.Equal(
            new[] { "personal_profile", "contact_list", "note" },
            vm.MemoryGroups.Select(g => g.Type).ToArray());
        Assert.Equal(
            new[] { "Personal Profile", "Contacts", "Notes" },
            vm.MemoryGroups.Select(g => g.DisplayName).ToArray());

        // Within the notes group, items are alphabetical by title.
        var notes = vm.MemoryGroups.Single(g => g.Type == "note");
        Assert.Equal(new[] { "Apple note", "Zebra note" }, notes.Items.Select(i => i.Title).ToArray());
        Assert.Equal(2, notes.ItemCount);

        Assert.Equal(4, vm.TotalObjectCount);
        Assert.Equal("200 B", vm.StorageSizeText);

        vm.Dispose();
    }

    [Fact]
    public async Task Delete_forgets_by_reference_and_drops_the_row()
    {
        var item = Item("memory/contacts.md#John Smith", "memory/contacts.md", "contact_list", "John Smith");
        var (vm, memory, dialog) = Create([item], bytes: 50);
        dialog.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await vm.OnNavigatedToAsync(null);
        await vm.DeleteMemoryCommand.ExecuteAsync(item);

        await memory.Received(1).ForgetAsync("memory/contacts.md#John Smith");
        Assert.Empty(vm.MemoryGroups); // the only group emptied and was removed

        vm.Dispose();
    }

    [Fact]
    public async Task Save_updates_the_section_body_by_reference()
    {
        var item = Item("memory/contacts.md#John Smith", "memory/contacts.md", "contact_list", "John Smith", body: "old");
        var (vm, memory, _) = Create([item], bytes: 50);

        await vm.OnNavigatedToAsync(null);
        await vm.EditMemoryCommand.ExecuteAsync(item);
        Assert.True(vm.IsEditing);
        Assert.Equal("old", vm.EditingData);

        vm.EditingData = "- email: new@x";
        await vm.SaveEditCommand.ExecuteAsync(null);

        await memory.Received(1).UpdateSectionAsync("memory/contacts.md#John Smith", "- email: new@x");
        Assert.False(vm.IsEditing);

        vm.Dispose();
    }

    [Fact]
    public async Task Search_projects_recall_hits_back_to_full_items_by_reference()
    {
        var item = Item("memory/contacts.md#John Smith", "memory/contacts.md", "contact_list", "John Smith", body: "real body");
        var (vm, memory, _) = Create([item], bytes: 50);
        memory.RecallAsync("john", Arg.Any<int>())
            .Returns([new RecallHit("memory/contacts.md", "John Smith", "snippet…", 0.9f)]);

        vm.SearchQuery = "john";
        await vm.RefreshCommand.ExecuteAsync(null);

        var group = Assert.Single(vm.MemoryGroups);
        var found = Assert.Single(group.Items);
        Assert.Equal("John Smith", found.Title);
        // The full vault body is surfaced (re-read by reference), not just the recall snippet.
        Assert.Equal("real body", found.Body);

        vm.Dispose();
    }

    [Fact]
    public async Task LoadMemories_builds_full_vault_composition_in_canonical_order()
    {
        var items = new[]
        {
            Item("memory/notes/z.md", "memory/notes/z.md", "note", "Zebra note"),
            Item("memory/notes/a.md", "memory/notes/a.md", "note", "Apple note"),
            Item("memory/contacts.md#John", "memory/contacts.md", "contact_list", "John"),
            Item("memory/profile.md#Coffee", "memory/profile.md", "personal_profile", "Coffee"),
            // A foreign-typed record the canonical grouping drops: it must be excluded from BOTH the
            // composition and the header total, so the bar and header agree.
            Item("memory/misc.md#Junk", "memory/misc.md", "random", "Junk"),
        };
        var (vm, _, _) = Create(items, bytes: 200);

        await vm.OnNavigatedToAsync(null);

        // Segments follow the §8 CanonicalGroups order (profile, contacts, ..., notes); zero-count types
        // are absent and the foreign "random" type never appears.
        Assert.Equal(
            new[] { "personal_profile", "contact_list", "note" },
            vm.VaultComposition.Select(s => s.Type).ToArray());
        Assert.Equal(
            new[] { "Personal Profile", "Contacts", "Notes" },
            vm.VaultComposition.Select(s => s.DisplayName).ToArray());
        Assert.Equal(new[] { 1, 1, 2 }, vm.VaultComposition.Select(s => s.Count).ToArray());

        // Bar and header agree by construction, and both exclude the foreign-typed record.
        Assert.Equal(vm.TotalObjectCount, vm.VaultComposition.Sum(s => s.Count));
        Assert.Equal(4, vm.TotalObjectCount);

        // Fractions are count / totalDisplayable and sum to 1.0.
        Assert.Equal(1.0, vm.VaultComposition.Sum(s => s.Fraction), 9);
        Assert.Equal(2 / 4.0, vm.VaultComposition.Single(s => s.Type == "note").Fraction, 9);

        Assert.True(vm.IsVaultOverviewVisible);
        Assert.False(vm.IsInspectorPlaceholderVisible);

        vm.Dispose();
    }

    [Fact]
    public async Task Empty_vault_shows_placeholder_not_overview()
    {
        var (vm, _, _) = Create([], bytes: 0);

        await vm.OnNavigatedToAsync(null);

        Assert.Empty(vm.VaultComposition);
        Assert.False(vm.IsVaultOverviewVisible);
        Assert.True(vm.IsInspectorPlaceholderVisible);

        vm.Dispose();
    }

    [Fact]
    public async Task Selecting_a_memory_hides_the_overview_and_raises_change()
    {
        var item = Item("memory/notes/a.md", "memory/notes/a.md", "note", "Apple note");
        var (vm, _, _) = Create([item], bytes: 50);

        await vm.OnNavigatedToAsync(null);
        Assert.True(vm.IsVaultOverviewVisible);
        Assert.False(vm.IsInspectorPlaceholderVisible);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedMemory = item;

        Assert.False(vm.IsVaultOverviewVisible);
        Assert.Contains(nameof(MemoryViewModel.IsVaultOverviewVisible), raised);

        vm.Dispose();
    }

    [Fact]
    public async Task Composition_reflects_full_snapshot_during_search()
    {
        // Two notes in the vault, but search recalls only one. The composition must still reflect the
        // FULL snapshot (both notes) rather than collapsing to the single filtered hit.
        var a = Item("memory/notes/a.md", "memory/notes/a.md", "note", "Apple note", body: "apple");
        var b = Item("memory/notes/b.md", "memory/notes/b.md", "note", "Banana note", body: "banana");
        var (vm, memory, _) = Create([a, b], bytes: 100);
        memory.RecallAsync("apple", Arg.Any<int>())
            .Returns([new RecallHit("memory/notes/a.md", "", "apple", 0.9f)]);

        vm.SearchQuery = "apple";
        await vm.RefreshCommand.ExecuteAsync(null);

        // Grouped list is filtered to the one hit, but the composition sees the whole vault.
        Assert.Equal(2, vm.VaultComposition.Single(s => s.Type == "note").Count);
        Assert.Equal(2, vm.TotalObjectCount);

        vm.Dispose();
    }

    [Fact]
    public async Task Search_does_not_crash_on_duplicate_headings()
    {
        // A hand-edited file can carry two identical ## headings -> two items with the SAME reference.
        // The search join must collapse (last-wins) rather than throw.
        var dup1 = Item("memory/contacts.md#John", "memory/contacts.md", "contact_list", "John", body: "first");
        var dup2 = Item("memory/contacts.md#John", "memory/contacts.md", "contact_list", "John", body: "second");
        var (vm, memory, _) = Create([dup1, dup2], bytes: 60);
        memory.RecallAsync("john", Arg.Any<int>())
            .Returns([new RecallHit("memory/contacts.md", "John", "snippet", 0.9f)]);

        vm.SearchQuery = "john";
        await vm.RefreshCommand.ExecuteAsync(null);

        var group = Assert.Single(vm.MemoryGroups);
        Assert.Single(group.Items);

        vm.Dispose();
    }
}
