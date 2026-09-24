using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pia.Services.Plugins;

/// <summary>A user-added MCP server as the UI edits it: env values are plaintext here and only become
/// ciphertext in <see cref="LocalMcpConfig.ToConfigJson"/>.</summary>
public sealed record LocalMcpDefinition(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? WorkingDirectory,
    string ToolPrefix,
    IReadOnlyList<string>? AllowedTools,
    string? Description = null,
    bool DefaultEnabled = true)
{
    public static LocalMcpDefinition Empty { get; } =
        new("", "", [], new Dictionary<string, string>(), null, "", null);
}

public static class LocalMcpConfig
{
    public const string SourceLocal = "local";

    public static bool IsLocal(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.String
                && source.GetString() == SourceLocal;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string ToConfigJson(LocalMcpDefinition definition, Func<string, string> encrypt)
    {
        var root = new JsonObject
        {
            ["source"] = SourceLocal,
            ["transport"] = "stdio",
            ["command"] = definition.Command,
            ["args"] = new JsonArray([.. definition.Args.Select(a => (JsonNode?)JsonValue.Create(a))]),
            ["toolPrefix"] = definition.ToolPrefix,
            ["defaultEnabled"] = definition.DefaultEnabled
        };

        if (definition.Env.Count > 0)
        {
            var env = new JsonObject();
            foreach (var (key, value) in definition.Env)
                env[key] = encrypt(value);
            root["env"] = env;
        }

        if (!string.IsNullOrWhiteSpace(definition.WorkingDirectory))
            root["cwd"] = definition.WorkingDirectory;

        if (definition.AllowedTools is not null)
            root["allowedTools"] = new JsonArray([.. definition.AllowedTools.Select(t => (JsonNode?)JsonValue.Create(t))]);

        return root.ToJsonString();
    }

    public static LocalMcpDefinition? FromConfigJson(string name, string? description, string? configJson, Func<string, string> decrypt)
    {
        if (!IsLocal(configJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(configJson!);
            var root = doc.RootElement;

            var command = root.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : "";

            var args = root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array
                ? argsEl.EnumerateArray().Select(a => a.GetString() ?? "").ToList()
                : [];

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in envEl.EnumerateObject())
                    env[entry.Name] = decrypt(entry.Value.GetString() ?? "");
            }

            var cwd = root.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() : null;
            var prefix = root.TryGetProperty("toolPrefix", out var prefixEl) ? prefixEl.GetString() ?? "" : "";

            List<string>? allowed = null;
            if (root.TryGetProperty("allowedTools", out var allowedEl) && allowedEl.ValueKind == JsonValueKind.Array)
                allowed = [.. allowedEl.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0)];

            var defaultEnabled = !root.TryGetProperty("defaultEnabled", out var enabledEl)
                || enabledEl.ValueKind != JsonValueKind.False;

            return new LocalMcpDefinition(name, command, args, env, cwd,
                prefix.Length > 0 ? prefix : DeriveToolPrefix(name), allowed, description, defaultEnabled);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Tool names must stay a valid function-name token for every provider, so anything outside
    /// [a-z0-9_] folds to an underscore.</summary>
    public static string DeriveToolPrefix(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasSeparator = false;
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('_');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('_');
        if (slug.Length == 0) slug = "mcp";
        if (char.IsAsciiDigit(slug[0])) slug = "mcp_" + slug;
        return slug.Length > 24 ? slug[..24].TrimEnd('_') : slug;
    }
}
