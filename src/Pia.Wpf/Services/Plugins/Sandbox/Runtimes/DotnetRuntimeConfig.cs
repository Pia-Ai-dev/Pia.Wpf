using System.IO;
using System.Text.Json;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

public static class DotnetRuntimeConfig
{
    public sealed record Framework(string Name, string Version);

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <returns>The frameworks in <c>&lt;dll name&gt;.runtimeconfig.json</c>; null when that file is missing or unreadable.</returns>
    public static IReadOnlyList<Framework>? Read(IRuntimeHostService host, string dllPath)
    {
        var text = host.ReadAllText(Path.ChangeExtension(dllPath, ".runtimeconfig.json"));
        if (text is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(text, Options);
            var frameworks = new List<Framework>();
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
                || runtimeOptions.ValueKind != JsonValueKind.Object)
                return frameworks;

            if (runtimeOptions.TryGetProperty("framework", out var framework))
                Add(framework, frameworks);
            if (runtimeOptions.TryGetProperty("frameworks", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                    Add(item, frameworks);
            }

            return frameworks;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Add(JsonElement element, List<Framework> frameworks)
    {
        if (element.ValueKind == JsonValueKind.Object
            && StringProperty(element, "name") is { } name
            && StringProperty(element, "version") is { } version)
            frameworks.Add(new Framework(name, version));
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
