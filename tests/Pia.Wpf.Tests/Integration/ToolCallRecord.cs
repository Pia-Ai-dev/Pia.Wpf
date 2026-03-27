namespace Pia.Tests.Integration;

public record ToolCallRecord(
    string ToolName,
    IDictionary<string, object?>? Arguments,
    object? Result);
