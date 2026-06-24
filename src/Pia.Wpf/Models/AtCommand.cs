namespace Pia.Models;

public enum AtCommandDomain
{
    Memory,
    Todo,
    Reminder,
    Research,
    Files
}

public class AtCommand
{
    public AtCommandDomain Domain { get; init; }
    public string? ItemTitle { get; init; }
    public Guid? ItemId { get; init; }
    public int StartIndex { get; init; }
    public int Length { get; init; }
}
