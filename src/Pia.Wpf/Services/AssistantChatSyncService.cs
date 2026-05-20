using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Pushes local assistant chats to the Pia cloud and pulls remote updates on
/// startup. See docs/server/assistant-chat-history.md §4 and
/// docs/plans/assistant-chat-history.md §Sync. Best-effort: any failure logs
/// and is swallowed; the local store remains authoritative.
/// </summary>
public sealed class AssistantChatSyncService : BackgroundService
{
    private enum OpKind { Upsert, Delete }
    private readonly record struct SyncOp(Guid ChatId, OpKind Kind);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAssistantChatService _chatService;
    private readonly ICloudCapabilityService _capabilities;
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AssistantChatSyncService> _logger;

    // Channel is a wakeup signal only; the actual per-chat coalescing lives in
    // _desired so a stale Upsert is overwritten by a later Delete for the same ID.
    private readonly Channel<byte> _signal = Channel.CreateUnbounded<byte>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, OpKind> _desired = new();

    public AssistantChatSyncService(
        IAssistantChatService chatService,
        ICloudCapabilityService capabilities,
        IAuthService authService,
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILogger<AssistantChatSyncService> logger)
    {
        _chatService = chatService;
        _capabilities = capabilities;
        _authService = authService;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _chatService.ChatsChanged += OnChatsChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _chatService.ChatsChanged -= OnChatsChanged;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Used by AssistantChatRetentionService to enqueue a cloud delete for an
    /// evicted chat (the local DELETE bypasses the normal ChatsChanged path
    /// for batch eviction).
    /// </summary>
    public void EnqueueDelete(Guid chatId)
    {
        EnqueueOp(chatId, OpKind.Delete);
    }

    private void OnChatsChanged(object? sender, AssistantChatChangedEventArgs e)
    {
        var kind = e.Kind == AssistantChatChangeKind.Deleted ? OpKind.Delete : OpKind.Upsert;
        EnqueueOp(e.Id, kind);
    }

    private void EnqueueOp(Guid id, OpKind kind)
    {
        lock (_stateLock)
        {
            // Per-chat coalescing: keep only the latest desired state.
            _desired[id] = kind;
        }
        _signal.Writer.TryWrite(0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AssistantChatSyncService started");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var supported = await _capabilities.ChatsSupportedAsync(stoppingToken);
        if (!supported)
        {
            _logger.LogInformation("Assistant chat cloud sync disabled (capability off)");
            return;
        }

        await RunStartupPullAsync(stoppingToken);

        try
        {
            await foreach (var _ in _signal.Reader.ReadAllAsync(stoppingToken))
            {
                await DrainAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (true)
        {
            SyncOp next;
            lock (_stateLock)
            {
                if (_desired.Count == 0) return;
                var kvp = _desired.First();
                _desired.Remove(kvp.Key);
                next = new SyncOp(kvp.Key, kvp.Value);
            }

            try
            {
                await ProcessOpAsync(next, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Sync op {Kind} for chat {ChatId} failed", next.Kind, next.ChatId);
            }
        }
    }

    private async Task ProcessOpAsync(SyncOp op, CancellationToken ct)
    {
        if (op.Kind == OpKind.Delete)
        {
            await SendDeleteAsync(op.ChatId, ct);
            return;
        }

        var chat = await _chatService.GetAsync(op.ChatId, ct);
        if (chat is null)
        {
            // Was deleted locally between enqueue and processing; treat as delete.
            await SendDeleteAsync(op.ChatId, ct);
            return;
        }

        await SendUpsertAsync(chat, retried: false, ct);
    }

    private async Task SendUpsertAsync(SyncAssistantChat chat, bool retried, CancellationToken ct)
    {
        var (client, baseUrl) = await BuildClientAsync(ct);
        if (client is null || baseUrl is null) return;

        var url = $"{baseUrl}/api/v1/chats/{chat.Id}";
        using (client)
        {
            using var content = JsonContent.Create(chat, options: JsonOptions);
            using var response = await client.PutAsync(url, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Pushed chat {ChatId} to cloud (status {Status})",
                    chat.Id, (int)response.StatusCode);
                return;
            }

            if (response.StatusCode == HttpStatusCode.Conflict && !retried)
            {
                var serverChat = await TryReadAsync(response, ct);
                if (serverChat is not null)
                {
                    var merged = MergeForConflict(serverChat, chat);
                    _logger.LogInformation(
                        "Cloud upsert 409 for chat {ChatId}; merging and retrying", chat.Id);
                    await SendUpsertAsync(merged, retried: true, ct);
                    return;
                }
            }

            _logger.LogInformation(
                "Cloud upsert for chat {ChatId} returned status {Status}",
                chat.Id, (int)response.StatusCode);
        }
    }

    private async Task SendDeleteAsync(Guid chatId, CancellationToken ct)
    {
        var (client, baseUrl) = await BuildClientAsync(ct);
        if (client is null || baseUrl is null) return;

        var url = $"{baseUrl}/api/v1/chats/{chatId}";
        using (client)
        {
            using var response = await client.DeleteAsync(url, ct);
            _logger.LogInformation(
                "Cloud delete for chat {ChatId} returned status {Status}",
                chatId, (int)response.StatusCode);
        }
    }

    private async Task RunStartupPullAsync(CancellationToken ct)
    {
        try
        {
            var (client, baseUrl) = await BuildClientAsync(ct);
            if (client is null || baseUrl is null) return;

            using (client)
            {
                // `since` is the inclusive lower bound per server contract §4.1,
                // so the first paged response will normally re-include the local
                // newest chat. SaveAsync is an upsert, so that's harmless.
                var since = await _chatService.GetMaxUpdatedAtAsync(ct);
                var sinceParam = since is null
                    ? null
                    : Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("O"));

                string? cursor = null;
                var totalMerged = 0;
                var pages = 0;
                const int maxPages = 100; // safety stop in case the server lies about hasMore

                while (pages < maxPages)
                {
                    var url = BuildPullUrl(baseUrl, sinceParam, cursor);

                    using var response = await client.GetAsync(url, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation(
                            "Startup pull returned status {Status} (page {Page})",
                            (int)response.StatusCode, pages + 1);
                        return;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (!doc.RootElement.TryGetProperty("chats", out var chatsArr) ||
                        chatsArr.ValueKind != JsonValueKind.Array)
                    {
                        return;
                    }

                    foreach (var element in chatsArr.EnumerateArray())
                    {
                        SyncAssistantChat? incoming;
                        try
                        {
                            incoming = element.Deserialize<SyncAssistantChat>(JsonOptions);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to deserialize chat from startup pull");
                            continue;
                        }

                        if (incoming is null) continue;

                        try
                        {
                            await _chatService.SaveAsync(incoming, ct);
                            totalMerged++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to merge incoming chat {ChatId}", incoming.Id);
                        }
                    }

                    pages++;

                    var hasMore = doc.RootElement.TryGetProperty("hasMore", out var hasMoreProp) &&
                        hasMoreProp.ValueKind == JsonValueKind.True;
                    if (!hasMore) break;

                    cursor = doc.RootElement.TryGetProperty("nextCursor", out var nextProp) &&
                        nextProp.ValueKind == JsonValueKind.String
                        ? nextProp.GetString()
                        : null;
                    if (string.IsNullOrEmpty(cursor)) break;
                }

                _logger.LogInformation(
                    "Startup pull merged {Count} chats across {Pages} page(s)",
                    totalMerged, pages);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup pull failed");
        }
    }

    private static string BuildPullUrl(string baseUrl, string? sinceParam, string? cursor)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(sinceParam)) parts.Add($"since={sinceParam}");
        if (!string.IsNullOrEmpty(cursor)) parts.Add($"cursor={Uri.EscapeDataString(cursor)}");
        return parts.Count == 0
            ? $"{baseUrl}/api/v1/chats"
            : $"{baseUrl}/api/v1/chats?{string.Join('&', parts)}";
    }

    private async Task<(HttpClient? Client, string? BaseUrl)> BuildClientAsync(CancellationToken ct)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return (null, null);

        var token = await _authService.GetAccessTokenAsync();

        var client = _httpClientFactory.CreateClient();
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
        client.Timeout = TimeSpan.FromSeconds(60);
        return (client, serverUrl);
    }

    private static async Task<SyncAssistantChat?> TryReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<SyncAssistantChat>(JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Conflict merge strategy per server contract §6: per-message merge is not
    /// guaranteed by the server, so we conservatively rebuild the chat by
    /// taking the server's document as the base and appending any local-only
    /// message IDs in their original order, then bumping UpdatedAt.
    /// </summary>
    private static SyncAssistantChat MergeForConflict(SyncAssistantChat server, SyncAssistantChat local)
    {
        var serverIds = server.Messages.Select(m => m.Id).ToHashSet();
        var appended = local.Messages.Where(m => !serverIds.Contains(m.Id)).ToList();

        var merged = new SyncAssistantChat
        {
            Id = server.Id,
            SchemaVersion = Math.Max(server.SchemaVersion, local.SchemaVersion),
            Title = local.Title ?? server.Title,
            CreatedAt = server.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = local.LastAccessedAt > server.LastAccessedAt
                ? local.LastAccessedAt : server.LastAccessedAt,
            WindowMode = local.WindowMode,
            ProviderId = local.ProviderId ?? server.ProviderId,
            Messages = [.. server.Messages, .. appended],
            ExtensionData = server.ExtensionData,
        };
        return merged;
    }
}
