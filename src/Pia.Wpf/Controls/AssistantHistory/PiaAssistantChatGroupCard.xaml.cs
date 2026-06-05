using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Pia.Helpers;
using Pia.Shared.Models;
using Pia.ViewModels;

namespace Pia.Controls.AssistantHistory;

public partial class PiaAssistantChatGroupCard : UserControl
{
    private AssistantHistoryViewModel? _vm;

    public PiaAssistantChatGroupCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var view = this.FindAncestor<Views.AssistantHistoryView>();
        _vm = view?.DataContext as AssistantHistoryViewModel;
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
        if (e.PropertyName == nameof(AssistantHistoryViewModel.SelectedChat))
            SyncFromVm();
    }

    private void SyncFromVm()
    {
        if (_vm is null || DataContext is not AssistantChatGroupViewModel group) return;
        var target = _vm.SelectedChat;
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
        if (ItemList.SelectedItem is SyncAssistantChat chat)
            _vm.SelectedChat = chat;
    }

    private void ToggleHeader_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AssistantChatGroupViewModel group)
            group.IsExpanded = !group.IsExpanded;
    }
}
