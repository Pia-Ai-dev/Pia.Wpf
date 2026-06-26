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
/// by the §8 canonical order (alpha within a group), header metrics from
/// <see cref="IMemoryService.GetVaultMemoryStatsAsync"/>, and edit/delete routed through the vault verbs
/// <see cref="IMemoryService.UpdateSectionAsync"/> / <see cref="IMemoryService.ForgetAsync"/>.
/// </summary>
public class MemoryViewModelTests
{
    private static (MemoryViewModel Vm, IMemoryService Memory, IDialogService Dialog) Create(
        VaultMemoryItem[] items, int count, long bytes)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.ListMemoriesAsync().Returns(items);
        memory.GetVaultMemoryStatsAsync().Returns((count, bytes));

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
        var (vm, _, _) = Create(items, count: 4, bytes: 200);

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
        var (vm, memory, dialog) = Create([item], count: 1, bytes: 50);
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
        var (vm, memory, _) = Create([item], count: 1, bytes: 50);

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
        var (vm, memory, _) = Create([item], count: 1, bytes: 50);
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
}
