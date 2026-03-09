using System.Text.Json.Serialization;

namespace Pia.Models;

public class OptimizationTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Prompt { get; set; }
    public string? Description { get; set; }

    [JsonPropertyName("ExampleText")]
    public string? StyleDescription { get; set; }

    public bool IsBuiltIn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
}
