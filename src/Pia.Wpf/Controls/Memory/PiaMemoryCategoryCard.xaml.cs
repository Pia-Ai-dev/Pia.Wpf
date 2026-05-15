using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Pia.Models;
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
        var view = FindAncestor<Views.MemoryView>(this);
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
        var target = _vm.SelectedMemory;
        if (target is null || !group.Items.Contains(target))
        {
            if (ItemList.SelectedItem is not null)
                ItemList.SelectedItem = null;
        }
        else if (!ReferenceEquals(ItemList.SelectedItem, target))
        {
            ItemList.SelectedItem = target;
        }
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (ItemList.SelectedItem is MemoryObject mem)
            _vm.SelectedMemory = mem;
    }

    private void ToggleHeader_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MemoryGroupViewModel group)
            group.IsExpanded = !group.IsExpanded;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
