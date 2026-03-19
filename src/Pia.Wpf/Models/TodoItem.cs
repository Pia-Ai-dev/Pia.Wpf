namespace Pia.Models;

public enum TodoPriority { Low, Medium, High }
public enum TodoStatus { Pending, Completed }

public class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public string? Notes { get; set; }
    public TodoPriority Priority { get; set; } = TodoPriority.Medium;
    public TodoStatus Status { get; set; } = TodoStatus.Pending;
    public DateTime? DueDate { get; set; }
    public Guid? LinkedReminderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public int SortOrder { get; set; }
}
