using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Pia.Helpers;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Controls.History;

public partial class PiaHistoryGroupCard : UserControl
{
    private HistoryViewModel? _vm;

    public PiaHistoryGroupCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var view = this.FindAncestor<Views.HistoryView>();
        _vm = view?.DataContext as HistoryViewModel;
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
        if (e.PropertyName == nameof(HistoryViewModel.SelectedSession))
            SyncFromVm();
    }

    private void SyncFromVm()
    {
        if (_vm is null || DataContext is not SessionGroupViewModel group) return;
        var target = _vm.SelectedSession;
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
        if (ItemList.SelectedItem is OptimizationSession session)
            _vm.SelectedSession = session;
    }

    private void ToggleHeader_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionGroupViewModel group)
            group.IsExpanded = !group.IsExpanded;
    }

}
