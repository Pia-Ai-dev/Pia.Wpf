using System;
using System.IO;
using System.Linq;
using System.Threading;
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
        VaultMemoryItem[] items, long bytes = 0, VaultSourceItem[]? sources = null)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot(items, bytes));

        var vaultSources = Substitute.For<IVaultSourcesService>();
        vaultSources.ListSourcesAsync().Returns(sources ?? []);

        var localization = Substitute.For<ILocalizationService>();
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}:{string.Join(",", ci.ArgAt<object[]>(1))}");
        localization[Arg.Any<string>()].Returns(ci => ci.ArgAt<string>(0));

        var dialog = Substitute.For<IDialogService>();
        var vm = new MemoryViewModel(
            NullLogger<MemoryViewModel>.Instance,
            memory,
            Substitute.For<IEmbeddingService>(),
            dialog,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            localization,
            Substitute.For<IClipboardService>(),
            vaultSources,
            Substitute.For<IIngestScheduler>());
        return (vm, memory, dialog);
    }

    private static (MemoryViewModel Vm, IMemoryService Memory, global::Wpf.Ui.ISnackbarService Snackbar) CreateWithSnackbar(
        VaultMemoryItem[] items, long bytes = 0)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot(items, bytes));

        var vaultSources = Substitute.For<IVaultSourcesService>();
        vaultSources.ListSourcesAsync().Returns([]);

        var localization = Substitute.For<ILocalizationService>();
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}:{string.Join(",", ci.ArgAt<object[]>(1))}");
        localization[Arg.Any<string>()].Returns(ci => ci.ArgAt<string>(0));

        var snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();
        var vm = new MemoryViewModel(
            NullLogger<MemoryViewModel>.Instance,
            memory,
            Substitute.For<IEmbeddingService>(),
            Substitute.For<IDialogService>(),
            snackbar,
            localization,
            Substitute.For<IClipboardService>(),
            vaultSources,
            Substitute.For<IIngestScheduler>());
        return (vm, memory, snackbar);
    }

    private static VaultSourceItem Source(string name, long bytes = 10, bool isText = true, int pages = 0)
        => new($"sources/{name}", name, bytes, DateTime.MinValue, isText, pages);

    private static VaultMemoryItem Item(string reference, string filePath, string type, string title, string body = "body")
        => new(reference, filePath, type, title, body, null);

    [Fact]
    public async Task AddSourceFiles_copies_only_text_files_into_sources_and_starts_ingest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pia-memvm-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var textFile = Path.Combine(tempRoot, "note.txt");
            var binaryFile = Path.Combine(tempRoot, "image.png");
            File.WriteAllText(textFile, "hello");
            File.WriteAllBytes(binaryFile, [1, 2, 3]);

            var memory = Substitute.For<IMemoryService>();
            memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot([], 0));
            memory.VaultRoot.Returns(tempRoot);

            var vaultSources = Substitute.For<IVaultSourcesService>();
            vaultSources.ListSourcesAsync().Returns([]);

            var localization = Substitute.For<ILocalizationService>();
            localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.ArgAt<string>(0));
            localization[Arg.Any<string>()].Returns(ci => ci.ArgAt<string>(0));

            var scheduler = Substitute.For<IIngestScheduler>();
            scheduler.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new IngestResult("x", []));

            var vm = new MemoryViewModel(
                NullLogger<MemoryViewModel>.Instance,
                memory,
                Substitute.For<IEmbeddingService>(),
                Substitute.For<IDialogService>(),
                Substitute.For<global::Wpf.Ui.ISnackbarService>(),
                localization,
                Substitute.For<IClipboardService>(),
                vaultSources,
                scheduler);

            await vm.AddSourceFilesCommand.ExecuteAsync(new[] { textFile, binaryFile });

            var sourcesDir = Path.Combine(tempRoot, "sources");
            Assert.True(File.Exists(Path.Combine(sourcesDir, "note.txt")));   // text copied
            Assert.False(File.Exists(Path.Combine(sourcesDir, "image.png"))); // binary skipped
            await scheduler.Received(1).RunAsync("sources/note.txt", Arg.Any<CancellationToken>());
            await scheduler.DidNotReceive().RunAsync("sources/image.png", Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AddSourceFiles_uniquifies_name_collision_without_clobbering()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pia-memvm-{Guid.NewGuid()}");
        var sourcesDir = Path.Combine(tempRoot, "sources");
        Directory.CreateDirectory(sourcesDir);
        try
        {
            // A source already staged under sources/note.txt, and a different file with the same name
            // dropped from elsewhere — the collision must not clobber the staged one.
            File.WriteAllText(Path.Combine(sourcesDir, "note.txt"), "existing");
            var dropDir = Path.Combine(tempRoot, "drop");
            Directory.CreateDirectory(dropDir);
            var dropped = Path.Combine(dropDir, "note.txt");
            File.WriteAllText(dropped, "dropped");

            var memory = Substitute.For<IMemoryService>();
            memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot([], 0));
            memory.VaultRoot.Returns(tempRoot);

            var vaultSources = Substitute.For<IVaultSourcesService>();
            vaultSources.ListSourcesAsync().Returns([]);

            var localization = Substitute.For<ILocalizationService>();
            localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.ArgAt<string>(0));
            localization[Arg.Any<string>()].Returns(ci => ci.ArgAt<string>(0));

            var scheduler = Substitute.For<IIngestScheduler>();
            scheduler.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new IngestResult("x", []));

            var vm = new MemoryViewModel(
                NullLogger<MemoryViewModel>.Instance,
                memory,
                Substitute.For<IEmbeddingService>(),
                Substitute.For<IDialogService>(),
                Substitute.For<global::Wpf.Ui.ISnackbarService>(),
                localization,
                Substitute.For<IClipboardService>(),
                vaultSources,
                scheduler);

            await vm.AddSourceFilesCommand.ExecuteAsync(new[] { dropped });

            Assert.Equal("existing", File.ReadAllText(Path.Combine(sourcesDir, "note.txt")));   // untouched
            Assert.True(File.Exists(Path.Combine(sourcesDir, "note (1).txt")));                 // uniquified
            Assert.Equal("dropped", File.ReadAllText(Path.Combine(sourcesDir, "note (1).txt")));
            await scheduler.Received(1).RunAsync("sources/note (1).txt", Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LoadSources_marks_only_the_scheduler_running_ref_as_ingesting()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot([], 0));

        var vaultSources = Substitute.For<IVaultSourcesService>();
        vaultSources.ListSourcesAsync().Returns(new[] { Source("running.txt"), Source("idle.txt") });

        var localization = Substitute.For<ILocalizationService>();
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.ArgAt<string>(0));
        localization[Arg.Any<string>()].Returns(ci => ci.ArgAt<string>(0));

        var scheduler = Substitute.For<IIngestScheduler>();
        scheduler.CurrentSourceRef.Returns("sources/running.txt"); // an ingest is already in flight

        var vm = new MemoryViewModel(
            NullLogger<MemoryViewModel>.Instance,
            memory,
            Substitute.For<IEmbeddingService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            localization,
            Substitute.For<IClipboardService>(),
            vaultSources,
            scheduler);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.SourceFiles.Single(r => r.RelativePath == "sources/running.txt").IsIngesting);
        Assert.False(vm.SourceFiles.Single(r => r.RelativePath == "sources/idle.txt").IsIngesting);
    }

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
    public async Task LoadMemories_builds_source_rows_with_ingest_status()
    {
        var sources = new[]
        {
            Source("alpha.txt", bytes: 100, pages: 2),
            Source("beta.txt", bytes: 50),
            // Bytes kept under 1 KB so the expected size strings avoid culture-dependent decimals.
            Source("scan.pdf", bytes: 300, isText: false),
        };
        var (vm, _, _) = Create([], bytes: 0, sources: sources);

        await vm.OnNavigatedToAsync(null);

        Assert.Equal(3, vm.SourceFileCount);
        Assert.Equal(
            new[] { "alpha.txt", "beta.txt", "scan.pdf" },
            vm.SourceFiles.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { true, false, false }, vm.SourceFiles.Select(r => r.IsIngested).ToArray());

        // Status lines are localized in the VM: ingested rows carry the page count, raw text rows the
        // not-ingested key, non-text rows the not-a-text-file key.
        Assert.Equal("Memory_Sources_IngestedPages:2", vm.SourceFiles[0].StatusText);
        Assert.Equal("Memory_Sources_NotIngested", vm.SourceFiles[1].StatusText);
        Assert.Equal("Memory_Sources_NotText", vm.SourceFiles[2].StatusText);

        Assert.Equal("100 B", vm.SourceFiles[0].SizeText);
        // Summary aggregates count and total size of the RAW layer.
        Assert.Equal("Memory_Sources_Summary:3,450 B", vm.SourcesSummaryText);

        vm.Dispose();
    }

    [Fact]
    public async Task Sources_only_vault_shows_overview_not_placeholder()
    {
        var (vm, _, _) = Create([], bytes: 0, sources: [Source("alpha.txt")]);

        await vm.OnNavigatedToAsync(null);

        // No displayable memories, but staged sources: the overview (with its sources section) must
        // show instead of the "select a memory" placeholder.
        Assert.Empty(vm.VaultComposition);
        Assert.Equal(0, vm.TotalObjectCount);
        Assert.True(vm.IsVaultOverviewVisible);
        Assert.False(vm.IsInspectorPlaceholderVisible);

        vm.Dispose();
    }

    [Fact]
    public async Task NavigateToLink_selects_the_topic_page_by_target()
    {
        // A wikilink target `topics/foo` maps to the bare-path reference `memory/topics/foo.md`.
        var foo = Item("memory/topics/foo.md", "memory/topics/foo.md", "topic", "Foo", "foo body");
        var bar = Item("memory/topics/bar.md", "memory/topics/bar.md", "topic", "Bar", "bar body");
        var (vm, _, _) = Create([foo, bar]);

        await vm.OnNavigatedToAsync(null);
        await vm.NavigateToLinkCommand.ExecuteAsync("topics/foo");

        Assert.NotNull(vm.SelectedMemory);
        Assert.Equal("memory/topics/foo.md", vm.SelectedMemory!.Reference);
        Assert.False(vm.IsEditing);

        vm.Dispose();
    }

    [Fact]
    public async Task NavigateToLink_tolerates_extension_and_slashes_in_target()
    {
        var foo = Item("memory/topics/foo.md", "memory/topics/foo.md", "topic", "Foo", "foo body");
        var (vm, _, _) = Create([foo]);

        await vm.OnNavigatedToAsync(null);
        // Obsidian may emit the extension and/or a leading slash; both must still resolve.
        await vm.NavigateToLinkCommand.ExecuteAsync("/topics/foo.md");

        Assert.Equal("memory/topics/foo.md", vm.SelectedMemory!.Reference);

        vm.Dispose();
    }

    [Fact]
    public async Task NavigateToLink_to_structured_topic_selects_first_section()
    {
        // A topic whose synthesized body has `## sections` yields one item per section; the link resolves
        // to the first (alpha-ordered) section rather than dead-ending.
        var alpha = Item("memory/topics/foo.md#Alpha", "memory/topics/foo.md", "topic", "Alpha", "a");
        var beta = Item("memory/topics/foo.md#Beta", "memory/topics/foo.md", "topic", "Beta", "b");
        var (vm, _, _) = Create([beta, alpha]);

        await vm.OnNavigatedToAsync(null);
        await vm.NavigateToLinkCommand.ExecuteAsync("topics/foo");

        Assert.NotNull(vm.SelectedMemory);
        Assert.StartsWith("memory/topics/foo.md#", vm.SelectedMemory!.Reference);

        vm.Dispose();
    }

    [Fact]
    public async Task NavigateToLink_unresolved_target_keeps_selection_null_and_warns()
    {
        var foo = Item("memory/topics/foo.md", "memory/topics/foo.md", "topic", "Foo", "foo body");
        var (vm, _, snackbar) = CreateWithSnackbar([foo]);

        await vm.OnNavigatedToAsync(null);
        await vm.NavigateToLinkCommand.ExecuteAsync("topics/missing");

        Assert.Null(vm.SelectedMemory);
        snackbar.ReceivedWithAnyArgs(1).Show(default!, default!, default, default!, default);

        vm.Dispose();
    }

    [Fact]
    public async Task NavigateToLink_resolves_a_slug_drifted_target_to_the_canonical_file()
    {
        // Topic FILES are named via VaultSlug.Slugify(subject); the LLM-authored inline link is only the
        // informal "lowercase-hyphen form", so `topics/Node.js` must still reach `memory/topics/node-js.md`.
        var nodejs = Item("memory/topics/node-js.md", "memory/topics/node-js.md", "topic", "Node.js", "js runtime");
        var (vm, _, _) = Create([nodejs]);

        await vm.OnNavigatedToAsync(null);
        await vm.NavigateToLinkCommand.ExecuteAsync("topics/Node.js");

        Assert.Equal("memory/topics/node-js.md", vm.SelectedMemory!.Reference);

        vm.Dispose();
    }

    [Fact]
    public async Task NavigateToLink_clears_active_search_to_reach_a_filtered_out_target()
    {
        var foo = Item("memory/topics/foo.md", "memory/topics/foo.md", "topic", "Foo", "foo body");
        var bar = Item("memory/topics/bar.md", "memory/topics/bar.md", "topic", "Bar", "bar body");
        var (vm, memory, _) = Create([foo, bar]);
        memory.RecallAsync("foo", Arg.Any<int>()).Returns([]);

        vm.SearchQuery = "foo";
        await vm.RefreshCommand.ExecuteAsync(null);
        // Only the matching page is shown; the target is filtered out.
        Assert.DoesNotContain(vm.MemoryGroups.SelectMany(g => g.Items), i => i.Reference == "memory/topics/bar.md");

        await vm.NavigateToLinkCommand.ExecuteAsync("topics/bar");

        Assert.Equal(string.Empty, vm.SearchQuery);
        Assert.Equal("memory/topics/bar.md", vm.SelectedMemory!.Reference);

        vm.Dispose();
    }

    [Fact]
    public async Task GoBack_returns_to_the_previous_page_and_does_not_re_record()
    {
        var a = Item("memory/notes/a.md", "memory/notes/a.md", "note", "A");
        var b = Item("memory/notes/b.md", "memory/notes/b.md", "note", "B");
        var (vm, _, _) = Create([a, b]);

        await vm.OnNavigatedToAsync(null);
        Assert.False(vm.GoBackCommand.CanExecute(null));

        vm.SelectedMemory = a; // null -> a: not recorded
        vm.SelectedMemory = b; // a -> b: records a
        Assert.True(vm.GoBackCommand.CanExecute(null));

        await vm.GoBackCommand.ExecuteAsync(null);

        Assert.Equal("memory/notes/a.md", vm.SelectedMemory!.Reference);
        // Back itself must not push 'b' — the stack is now empty.
        Assert.False(vm.GoBackCommand.CanExecute(null));

        vm.Dispose();
    }

    [Fact]
    public async Task History_caps_at_ten_dropping_the_oldest()
    {
        var items = Enumerable.Range(0, 12)
            .Select(i => Item($"memory/notes/p{i}.md", $"memory/notes/p{i}.md", "note", $"P{i:00}"))
            .ToArray();
        var (vm, _, _) = Create(items);

        await vm.OnNavigatedToAsync(null);
        foreach (var item in items)
        {
            vm.SelectedMemory = item; // 11 transitions push p0..p10; cap drops p0
        }

        var backs = 0;
        while (vm.GoBackCommand.CanExecute(null) && backs <= 20)
        {
            await vm.GoBackCommand.ExecuteAsync(null);
            backs++;
        }

        Assert.Equal(10, backs);
        // p0 was evicted by the cap, so the earliest reachable page is p1.
        Assert.Equal("memory/notes/p1.md", vm.SelectedMemory!.Reference);

        vm.Dispose();
    }

    [Fact]
    public async Task OnNavigatedTo_clears_the_back_history()
    {
        var a = Item("memory/notes/a.md", "memory/notes/a.md", "note", "A");
        var b = Item("memory/notes/b.md", "memory/notes/b.md", "note", "B");
        var (vm, _, _) = Create([a, b]);

        await vm.OnNavigatedToAsync(null);
        vm.SelectedMemory = a;
        vm.SelectedMemory = b;
        Assert.True(vm.GoBackCommand.CanExecute(null));

        await vm.OnNavigatedToAsync(null);

        Assert.False(vm.GoBackCommand.CanExecute(null));

        vm.Dispose();
    }

    [Fact]
    public async Task Search_that_hides_the_selection_does_not_record_history()
    {
        var a = Item("memory/notes/a.md", "memory/notes/a.md", "note", "Apple", body: "apple");
        var b = Item("memory/notes/b.md", "memory/notes/b.md", "note", "Banana", body: "banana");
        var (vm, memory, _) = Create([a, b]);
        memory.RecallAsync("banana", Arg.Any<int>()).Returns([]);

        await vm.OnNavigatedToAsync(null);
        vm.SelectedMemory = a; // null -> a: not recorded
        Assert.False(vm.GoBackCommand.CanExecute(null));

        vm.SearchQuery = "banana";
        await vm.RefreshCommand.ExecuteAsync(null); // filter hides 'a' -> silent deselect

        Assert.Null(vm.SelectedMemory);
        // A reload-driven deselection is not a navigation, so Back stays disabled.
        Assert.False(vm.GoBackCommand.CanExecute(null));

        vm.Dispose();
    }

    [Fact]
    public async Task Deleting_a_page_purges_it_from_the_back_history()
    {
        var a = Item("memory/notes/a.md", "memory/notes/a.md", "note", "A");
        var b = Item("memory/notes/b.md", "memory/notes/b.md", "note", "B");
        var (vm, memory, dialog) = Create([a, b]);
        dialog.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await vm.OnNavigatedToAsync(null);
        vm.SelectedMemory = a; // null -> a: not recorded
        vm.SelectedMemory = b; // a -> b: records a, stack [a], current b
        Assert.True(vm.GoBackCommand.CanExecute(null));

        // Delete 'a' — it is in history but is not the currently-viewed page.
        memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot([b], 0));
        await vm.DeleteMemoryCommand.ExecuteAsync(a);

        // 'a' was the only back entry and is now purged, so Back is disabled (no dead target remains).
        Assert.False(vm.GoBackCommand.CanExecute(null));

        vm.Dispose();
    }

    [Fact]
    public async Task GoBack_still_skips_an_unresolvable_entry_and_lands_on_an_earlier_one()
    {
        // Defense-in-depth for the pop-until-resolves loop: even if an entry cannot resolve (e.g. removed
        // by a path the purge did not cover), Back walks past it to the next valid page.
        var a = Item("memory/notes/a.md", "memory/notes/a.md", "note", "A");
        var b = Item("memory/notes/b.md", "memory/notes/b.md", "note", "B");
        var c = Item("memory/notes/c.md", "memory/notes/c.md", "note", "C");
        var (vm, memory, dialog) = Create([a, b, c]);
        dialog.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await vm.OnNavigatedToAsync(null);
        vm.SelectedMemory = a;
        vm.SelectedMemory = b; // records a
        vm.SelectedMemory = c; // records b -> stack [a, b]

        // Remove b from the loaded set WITHOUT going through the purge (simulates an out-of-band removal):
        // reload the groups so b is gone but the back stack still references it.
        memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot([a, c], 0));
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.GoBackCommand.ExecuteAsync(null); // pops b (unresolvable) then a

        Assert.Equal("memory/notes/a.md", vm.SelectedMemory!.Reference);

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
