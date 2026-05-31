using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

public partial class ReminderGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private ReminderBucket _bucketKind;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Reminder> _items = new();

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isOverdueBucket;
}
