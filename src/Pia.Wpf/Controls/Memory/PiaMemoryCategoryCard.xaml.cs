using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Pia.Helpers;
using Pia.Models.Vault;
using Pia.ViewModels;

namespace Pia.Controls.Memory;

public partial class PiaMemoryCategoryCard : UserControl
{
    private MemoryViewModel? _vm;

    public PiaMemoryCategoryCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var view = this.FindAncestor<Views.MemoryView>();
        _vm = view?.DataContext as MemoryViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
        SyncFromVm();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryViewModel.SelectedMemory))
            SyncFromVm();
    }

    private void SyncFromVm()
    {
        if (_vm is null || DataContext is not MemoryGroupViewModel group) return;

        // Selection identity is the vault reference (path#heading), not object identity — a reload
        // produces fresh VaultMemoryItem instances for the same memory.
        var target = _vm.SelectedMemory;
        var match = target is null ? null : group.Items.FirstOrDefault(i => i.Reference == target.Reference);
        if (match is null)
        {
            if (ItemList.SelectedItem is not null)
                ItemList.SelectedItem = null;
        }
        else if (!ReferenceEquals(ItemList.SelectedItem, match))
        {
            ItemList.SelectedItem = match;
        }
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (ItemList.SelectedItem is VaultMemoryItem mem)
            _vm.SelectedMemory = mem;
    }

    private void ToggleHeader_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MemoryGroupViewModel group)
            group.IsExpanded = !group.IsExpanded;
    }
}
