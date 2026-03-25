using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IKanbanColumnService
{
    event EventHandler? ColumnsChanged;
    Task<IReadOnlyList<KanbanColumn>> GetAllAsync();
    Task<KanbanColumn?> GetAsync(Guid id);
    Task<KanbanColumn> GetDefaultViewColumnAsync();
    Task<KanbanColumn> GetClosedColumnAsync();
    Task<KanbanColumn> CreateAsync(string name);
    Task RenameAsync(Guid id, string newName);
    Task DeleteAsync(Guid id);
    Task SetDefaultViewAsync(Guid id);
    Task ImportAsync(KanbanColumn column);
    Task<int> GetTodoCountAsync(Guid columnId);
}
