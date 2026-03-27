using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;

namespace Pia.ViewModels.Models;

public partial class KanbanColumnViewModel : ObservableObject
{
    public KanbanColumn Column { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isDefaultView;

    [ObservableProperty]
    private bool _isClosedColumn;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private ObservableCollection<TodoItem> _todos = new();

    public Guid Id => Column.Id;

    public KanbanColumnViewModel(KanbanColumn column)
    {
        Column = column;
        _name = column.Name;
        _isDefaultView = column.IsDefaultView;
        _isClosedColumn = column.IsClosedColumn;
        _isExpanded = !column.IsClosedColumn; // Closed starts collapsed
    }
}
