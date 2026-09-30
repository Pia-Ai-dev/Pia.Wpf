using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Knowledge;

namespace Pia.Services.KnowledgeManager;

public enum KbManagerCallStatus
{
    Ok,
    Unchanged,
    NotConnected,
    Forbidden,
    NotFound,
    Conflict,
    TooLarge,
    Invalid,
    Unavailable,
}

public sealed record KbManagerError(
    string Code, string? Message, string? Resource, long? Limit, long? Current, Guid? DocumentId);

public sealed record KbManagerResult<T>(KbManagerCallStatus Status, T? Value = default, KbManagerError? Error = null)
{
    public bool IsOk => Status is KbManagerCallStatus.Ok or KbManagerCallStatus.Unchanged;
}

public sealed record KbManagerDocumentContent(string Content, string ContentType);

/// <summary>Content sent through this client is stored unencrypted and is searchable by every group the knowledge base is granted to.</summary>
public interface IKnowledgeManagerApiClient
{
    Task<KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>> ListKnowledgeBasesAsync(CancellationToken ct = default);

    Task<KbManagerResult<KbManagerStats>> GetStatsAsync(Guid kbId, CancellationToken ct = default);

    Task<KbManagerResult<IReadOnlyList<KbManagerDocument>>> ListDocumentsAsync(Guid kbId, CancellationToken ct = default);

    Task<KbManagerResult<KbManagerDocumentContent>> GetContentAsync(
        Guid kbId, Guid documentId, CancellationToken ct = default);

    Task<KbManagerResult<KbManagerWriteResult>> UploadAsync(
        Guid kbId, KbManagerUploadRequest request, CancellationToken ct = default);

    /// <summary><see cref="KbManagerCallStatus.Unchanged"/> when the server found the content identical.</summary>
    Task<KbManagerResult<KbManagerWriteResult>> UpdateContentAsync(
        Guid kbId, Guid documentId, KbManagerUpdateContentRequest request, CancellationToken ct = default);

    Task<KbManagerResult<bool>> DeleteAsync(Guid kbId, Guid documentId, CancellationToken ct = default);

    Task<KbManagerResult<KbManagerPrompt>> GetPromptAsync(Guid kbId, CancellationToken ct = default);

    Task<KbManagerResult<bool>> SetPromptAsync(Guid kbId, string prompt, CancellationToken ct = default);
}

public sealed partial class KnowledgeManagerApiClient : IKnowledgeManagerApiClient
{
    private const string Root = "/api/kb-manager/knowledge-bases";

    // Unescaped non-ASCII keeps the body at its UTF-8 size, which is what the server's size limit counts.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<KnowledgeManagerApiClient> _logger;

    public KnowledgeManagerApiClient(
        ISettingsService settings,
        IAuthService auth,
        IHttpClientFactory httpClientFactory,
        ILogger<KnowledgeManagerApiClient> logger)
    {
        _settings = settings;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>> ListKnowledgeBasesAsync(
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, Root, null, ReadJsonAsync<IReadOnlyList<KbManagerKnowledgeBase>>, ct);

    public Task<KbManagerResult<KbManagerStats>> GetStatsAsync(Guid kbId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"{Root}/{kbId}/stats", null, ReadJsonAsync<KbManagerStats>, ct);

    public Task<KbManagerResult<IReadOnlyList<KbManagerDocument>>> ListDocumentsAsync(
        Guid kbId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"{Root}/{kbId}/documents", null, ReadJsonAsync<IReadOnlyList<KbManagerDocument>>, ct);

    public Task<KbManagerResult<KbManagerDocumentContent>> GetContentAsync(
        Guid kbId, Guid documentId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"{Root}/{kbId}/documents/{documentId}/content", null, async (response, token) =>
        {
            var text = await response.Content.ReadAsStringAsync(token);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? KbManagerLimits.PlainText;
            return new KbManagerResult<KbManagerDocumentContent>(
                KbManagerCallStatus.Ok, new KbManagerDocumentContent(text, mediaType));
        }, ct);

    public Task<KbManagerResult<KbManagerWriteResult>> UploadAsync(
        Guid kbId, KbManagerUploadRequest request, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"{Root}/{kbId}/documents", request, ReadJsonAsync<KbManagerWriteResult>, ct);

    public Task<KbManagerResult<KbManagerWriteResult>> UpdateContentAsync(
        Guid kbId, Guid documentId, KbManagerUpdateContentRequest request, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, $"{Root}/{kbId}/documents/{documentId}/content", request, async (response, token) =>
        {
            var read = await ReadJsonAsync<KbManagerWriteResult>(response, token);
            return read.Status == KbManagerCallStatus.Ok && response.StatusCode == HttpStatusCode.OK
                ? read with { Status = KbManagerCallStatus.Unchanged }
                : read;
        }, ct);

    public Task<KbManagerResult<bool>> DeleteAsync(Guid kbId, Guid documentId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"{Root}/{kbId}/documents/{documentId}", null, Done, ct);

    public Task<KbManagerResult<KbManagerPrompt>> GetPromptAsync(Guid kbId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"{Root}/{kbId}/prompt", null, ReadJsonAsync<KbManagerPrompt>, ct);

    public Task<KbManagerResult<bool>> SetPromptAsync(Guid kbId, string prompt, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, $"{Root}/{kbId}/prompt", new KbManagerPrompt(prompt), Done, ct);

    private async Task<KbManagerResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        Func<HttpResponseMessage, CancellationToken, Task<KbManagerResult<T>>> onSuccess,
        CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return new(KbManagerCallStatus.NotConnected);

        var token = await _auth.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return new(KbManagerCallStatus.NotConnected);

        try
        {
            var http = _httpClientFactory.CreateClient();
            var response = await http.SendAsync(Request(method, serverUrl + path, body, token), ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                var refreshed = await _auth.GetAccessTokenAsync(forceRefresh: true, staleAccessToken: token);
                if (string.IsNullOrEmpty(refreshed)) return new(KbManagerCallStatus.NotConnected);
                response = await http.SendAsync(Request(method, serverUrl + path, body, refreshed), ct);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode) return await onSuccess(response, ct);

                _logger.LogInformation("KB manager {Method} {Route} returned {Status}.",
                    method.Method, RouteShape(path), (int)response.StatusCode);
                return await RefusalAsync<T>(response, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "KB manager {Method} {Route} could not reach the server.",
                method.Method, RouteShape(path));
            return new(KbManagerCallStatus.Unavailable);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        return request;
    }

    private static async Task<KbManagerResult<T>> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return value is null ? new(KbManagerCallStatus.Unavailable) : new(KbManagerCallStatus.Ok, value);
    }

    private static Task<KbManagerResult<bool>> Done(HttpResponseMessage response, CancellationToken ct)
        => Task.FromResult(new KbManagerResult<bool>(KbManagerCallStatus.Ok, true));

    private static async Task<KbManagerResult<T>> RefusalAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var status = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => KbManagerCallStatus.NotConnected,
            HttpStatusCode.Forbidden => KbManagerCallStatus.Forbidden,
            HttpStatusCode.NotFound => KbManagerCallStatus.NotFound,
            HttpStatusCode.Conflict => KbManagerCallStatus.Conflict,
            HttpStatusCode.RequestEntityTooLarge => KbManagerCallStatus.TooLarge,
            HttpStatusCode.BadRequest => KbManagerCallStatus.Invalid,
            _ => KbManagerCallStatus.Unavailable,
        };
        return new(status, default, await ReadErrorAsync(response, ct));
    }

    private static async Task<KbManagerError?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var code)
                || code.ValueKind != JsonValueKind.String)
                return null;

            return new KbManagerError(
                code.GetString()!,
                OptionalString(root, "message"),
                OptionalString(root, "resource"),
                OptionalLong(root, "limit"),
                OptionalLong(root, "current"),
                root.TryGetProperty("documentId", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.TryGetGuid(out var guid)
                        ? guid
                        : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? OptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? OptionalLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string RouteShape(string path) => GuidPattern().Replace(path, "{id}");

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
