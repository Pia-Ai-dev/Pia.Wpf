using System.Text.Json;

namespace Pia.Services.Plugins;

public enum LocalMcpParseError
{
    None,
    InvalidJson,
    NoServerObject,
    MultipleServers,
    MissingCommand,
    RemoteTransport,
    BadArgs,
    BadEnv
}

public sealed record LocalMcpParseResult(LocalMcpParseError Error, LocalMcpDefinition? Definition, string? Detail)
{
    public bool Success => Error == LocalMcpParseError.None && Definition is not null;

    public static LocalMcpParseResult Fail(LocalMcpParseError error, string? detail = null) => new(error, null, detail);
    public static LocalMcpParseResult Ok(LocalMcpDefinition definition) => new(LocalMcpParseError.None, definition, null);
}

/// <summary>Reads the three shapes a user is likely to paste: the Claude Desktop
/// <c>{"mcpServers":{…}}</c> envelope, one named entry, or a bare server object.</summary>
public static class LocalMcpJsonParser
{
    private static readonly string[] RemoteKeys = ["url", "serverUrl", "endpoint"];
    private static readonly string[] RemoteTypes = ["http", "sse", "streamable-http", "streamableHttp", "websocket"];

    public static LocalMcpParseResult Parse(string json, string? fallbackName = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return LocalMcpParseResult.Fail(LocalMcpParseError.InvalidJson);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            return LocalMcpParseResult.Fail(LocalMcpParseError.InvalidJson, ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return LocalMcpParseResult.Fail(LocalMcpParseError.NoServerObject);

            var (server, name, error) = Unwrap(root, fallbackName);
            if (error != LocalMcpParseError.None)
                return LocalMcpParseResult.Fail(error);

            return Build(server, name!);
        }
    }

    private static (JsonElement Server, string? Name, LocalMcpParseError Error) Unwrap(JsonElement root, string? fallbackName)
    {
        if (root.TryGetProperty("mcpServers", out var servers) || root.TryGetProperty("servers", out servers))
        {
            if (servers.ValueKind != JsonValueKind.Object)
                return (default, null, LocalMcpParseError.NoServerObject);

            var entries = servers.EnumerateObject().ToList();
            return entries.Count switch
            {
                0 => (default, null, LocalMcpParseError.NoServerObject),
                > 1 => (default, null, LocalMcpParseError.MultipleServers),
                _ => (entries[0].Value, entries[0].Name, LocalMcpParseError.None)
            };
        }

        if (LooksLikeServer(root))
            return (root, Fallback(fallbackName), LocalMcpParseError.None);

        var named = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).ToList();
        if (named.Count == 1 && LooksLikeServer(named[0].Value))
            return (named[0].Value, named[0].Name, LocalMcpParseError.None);
        if (named.Count > 1 && named.All(p => LooksLikeServer(p.Value)))
            return (default, null, LocalMcpParseError.MultipleServers);

        return (default, null, LocalMcpParseError.NoServerObject);
    }

    private static bool LooksLikeServer(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && (element.TryGetProperty("command", out _)
            || RemoteKeys.Any(k => element.TryGetProperty(k, out _))
            || element.TryGetProperty("type", out _));

    private static string Fallback(string? name) => string.IsNullOrWhiteSpace(name) ? "MCP server" : name.Trim();

    private static LocalMcpParseResult Build(JsonElement server, string name)
    {
        foreach (var key in RemoteKeys)
        {
            if (server.TryGetProperty(key, out var remote) && remote.ValueKind == JsonValueKind.String)
                return LocalMcpParseResult.Fail(LocalMcpParseError.RemoteTransport, remote.GetString());
        }

        if (server.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var type = typeEl.GetString();
            if (RemoteTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                return LocalMcpParseResult.Fail(LocalMcpParseError.RemoteTransport, type);
        }

        var command = server.TryGetProperty("command", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String
            ? cmdEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(command))
            return LocalMcpParseResult.Fail(LocalMcpParseError.MissingCommand);

        var args = new List<string>();
        if (server.TryGetProperty("args", out var argsEl))
        {
            if (argsEl.ValueKind != JsonValueKind.Array)
                return LocalMcpParseResult.Fail(LocalMcpParseError.BadArgs);
            foreach (var arg in argsEl.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.String)
                    return LocalMcpParseResult.Fail(LocalMcpParseError.BadArgs);
                args.Add(arg.GetString() ?? "");
            }
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (server.TryGetProperty("env", out var envEl))
        {
            if (envEl.ValueKind != JsonValueKind.Object)
                return LocalMcpParseResult.Fail(LocalMcpParseError.BadEnv);
            foreach (var entry in envEl.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                    return LocalMcpParseResult.Fail(LocalMcpParseError.BadEnv);
                env[entry.Name] = entry.Value.GetString() ?? "";
            }
        }

        var cwd = ReadString(server, "cwd") ?? ReadString(server, "workingDirectory");
        var description = ReadString(server, "description");
        var trimmedName = name.Trim();

        return LocalMcpParseResult.Ok(new LocalMcpDefinition(
            trimmedName,
            command!.Trim(),
            args,
            env,
            cwd,
            LocalMcpConfig.DeriveToolPrefix(trimmedName),
            AllowedTools: null,
            Description: description));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
