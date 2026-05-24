using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;

namespace Pia.ViewModels;

public enum ReminderBucket
{
    Overdue,
    Today,
    Tomorrow,
    ThisWeek,
    Later,
}

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
