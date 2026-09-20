using System.IO;
using System.IO.Compression;
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
/// startup. See docs/server/assistant-chat-history.md §4. Best-effort: any
/// failure logs and is swallowed; the local store remains authoritative.
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
    private readonly SyncMapper _mapper;
    private readonly ISyncClientService _syncClient;
    private readonly ILogger<AssistantChatSyncService> _logger;

    // Channel is a wakeup signal only; the actual per-chat coalescing lives in
    // _desired so a stale Upsert is overwritten by a later Delete for the same ID. One pending
    // wakeup is therefore enough, and dropping the rest keeps a session that never signs in — where
    // nothing reads this — from buffering a token per chat change.
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, OpKind> _desired = new();

    /// <summary>Set by the upsert path on a 429 so the backfill can abandon a pass the server has already
    /// closed the window on, instead of spending the rest of the catalogue on rejections. Unsynchronized
    /// because every upsert — backfill or ordinary — runs on the worker's single loop thread.</summary>
    private bool _rateLimited;

    /// <summary>True while the rate limiter has cut a backfill pass short and chats are still owed.</summary>
    private volatile bool _backfillPaced;

    private readonly TimeSpan _startupDelay;

    /// <summary>The server's sync policy refills over a one-minute window, so nudging faster than this only
    /// buys more rejections.</summary>
    private readonly TimeSpan _backfillRetryInterval;

    /// <summary>The two overrides exist so a test can drive the whole worker loop in milliseconds; nothing
    /// in the app passes them.</summary>
    public AssistantChatSyncService(
        IAssistantChatService chatService,
        ICloudCapabilityService capabilities,
        IAuthService authService,
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        SyncMapper mapper,
        ISyncClientService syncClient,
        ILogger<AssistantChatSyncService> logger,
        TimeSpan? startupDelayOverride = null,
        TimeSpan? backfillRetryIntervalOverride = null)
    {
        _chatService = chatService;
        _capabilities = capabilities;
        _authService = authService;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _mapper = mapper;
        _syncClient = syncClient;
        _logger = logger;
        _startupDelay = startupDelayOverride ?? TimeSpan.FromSeconds(5);
        _backfillRetryInterval = backfillRetryIntervalOverride ?? TimeSpan.FromMinutes(1);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _chatService.ChatsChanged += OnChatsChanged;
        _chatService.ChatAccessed += OnChatAccessed;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _chatService.ChatsChanged -= OnChatsChanged;
        _chatService.ChatAccessed -= OnChatAccessed;
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

    // Retention deletes globally (an evicted chat is deleted from the server, and that tombstone reaches
    // every device), so a chat someone still reads here must keep the server's access date alive or another
    // device evicts it out from under them. The store only raises this on a day change, and PUT is the only
    // write the API has.
    private void OnChatAccessed(object? sender, Guid chatId) => EnqueueOp(chatId, OpKind.Upsert);

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
            await Task.Delay(_startupDelay, stoppingToken);

            if (!await RunStartupCycleAsync(stoppingToken)) return;

            // A pass gets one rate-limit window, so without a nudge the remainder waits for the next launch
            // — weeks, on a catalogue of any size. The callback does nothing but wake the loop below, which
            // keeps every push on this one thread.
            using Timer? backfillNudge = _backfillPaced
                ? new Timer(
                    _ => { if (_backfillPaced) _signal.Writer.TryWrite(0); },
                    null, _backfillRetryInterval, _backfillRetryInterval)
                : null;

            await foreach (var _ in _signal.Reader.ReadAllAsync(stoppingToken))
            {
                await DrainAsync(stoppingToken);

                if (_backfillPaced)
                    _backfillPaced = await RunStartupPushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>False when the capability is off, i.e. there is nothing to drain this session.</summary>
    private async Task<bool> RunStartupCycleAsync(CancellationToken ct)
    {
        // Probing before sign-in would beacon /api/capabilities from every signed-out install, and
        // nothing can be pushed without a token anyway.
        if (!await SyncPermittedAsync())
        {
            _logger.LogInformation("Assistant chat cloud sync idle until sign-in");
            await WaitForSignInAsync(ct);
        }

        if (!await _capabilities.ChatsSupportedAsync(ct))
        {
            _logger.LogInformation("Assistant chat cloud sync disabled (capability off)");
            return false;
        }

        await RunStartupPullAsync(ct);
        _backfillPaced = await RunStartupPushAsync(ct);
        return true;
    }

    private async Task<bool> SyncPermittedAsync() =>
        _authService.IsLoggedIn && (await _settingsService.GetSettingsAsync()).SyncEnabled;

    private async Task WaitForSignInAsync(CancellationToken ct)
    {
        var signedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoginStateChanged(object? sender, bool loggedIn)
        {
            if (loggedIn) signedIn.TrySetResult();
        }

        _authService.LoginStateChanged += OnLoginStateChanged;
        try
        {
            // Re-check after subscribing, or a sign-in racing the check above parks the worker
            // until the next launch.
            if (await SyncPermittedAsync()) return;

            using var cancellation = ct.Register(() => signedIn.TrySetCanceled(ct));
            await signedIn.Task;
        }
        finally
        {
            _authService.LoginStateChanged -= OnLoginStateChanged;
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

    /// <summary>False when the op did not reach the server, which is what holds the backfill gate open.</summary>
    private async Task<bool> ProcessOpAsync(SyncOp op, CancellationToken ct)
    {
        if (op.Kind == OpKind.Delete)
            return await SendDeleteAsync(op.ChatId, ct);

        var chat = await _chatService.GetAsync(op.ChatId, ct);
        if (chat is null)
        {
            // Was deleted locally between enqueue and processing; treat as delete.
            return await SendDeleteAsync(op.ChatId, ct);
        }

        return await SendUpsertAsync(chat, retried: false, ct);
    }

    private async Task<bool> SendUpsertAsync(SyncAssistantChat chat, bool retried, CancellationToken ct)
    {
        var (client, baseUrl, userId) = await BuildClientAsync(ct);
        if (client is null || baseUrl is null) return false;

        var url = $"{baseUrl}/api/v1/chats/{chat.Id}";
        using (client)
        {
            var wire = _mapper.ToSyncAssistantChat(chat, userId);
            using var content = CreateGzipJsonContent(wire);
            using var response = await client.PutAsync(url, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Pushed chat {ChatId} to cloud (status {Status})",
                    chat.Id, (int)response.StatusCode);
                return true;
            }

            if (response.StatusCode == HttpStatusCode.Conflict && !retried)
            {
                var serverWire = await TryReadAsync(response, ct);
                if (serverWire is not null)
                {
                    // Decrypt server response into plaintext space so the merge can
                    // reason about Title / Messages / ProviderId.
                    var serverChat = _mapper.FromSyncAssistantChat(serverWire, userId);
                    var merged = MergeForConflict(serverChat, chat);
                    _logger.LogInformation(
                        "Cloud upsert 409 for chat {ChatId}; merging and retrying", chat.Id);
                    return await SendUpsertAsync(merged, retried: true, ct);
                }
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Endpoint disappeared mid-session (feature flag flipped or server downgrade).
                // Drop the cached capability so the next session re-probes /api/capabilities.
                _capabilities.Invalidate();
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                if (errorBody.Contains("e2ee_required"))
                {
                    // The account is E2EE-enabled server-side but this chat went out in
                    // plaintext (no local UMK yet). Dropping silently is fine for the op —
                    // the local store is authoritative — but the user needs onboarding.
                    _logger.LogWarning(
                        "Server requires E2EE for this account; chat {ChatId} not pushed, onboarding required",
                        chat.Id);
                    _syncClient.NotifyE2EEOnboardingRequired();
                    return false;
                }
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                _rateLimited = true;

            _logger.LogInformation(
                "Cloud upsert for chat {ChatId} returned status {Status}",
                chat.Id, (int)response.StatusCode);
            return false;
        }
    }

    /// <summary>
    /// Confirms each chat retention is about to delete against the server's access date, raising the local
    /// one where the server is ahead. Returns false when a candidate could not be checked — eviction deletes
    /// account-wide, so a device that cannot reach the server has no standing to delete for the account.
    /// Call only when the account syncs; with sync off the local dates are already authoritative.
    /// </summary>
    public async Task<bool> RefreshAccessDatesAsync(IReadOnlyList<Guid> chatIds, CancellationToken ct)
    {
        if (chatIds.Count == 0) return true;

        var (client, baseUrl, _) = await BuildClientAsync(ct);
        if (client is null || baseUrl is null)
        {
            // Sync is on but there is no token yet — at five seconds after launch that is the normal state.
            _logger.LogInformation("Cannot confirm chat access dates: no signed-in client yet");
            return false;
        }

        using (client)
        {
            foreach (var chatId in chatIds)
            {
                ct.ThrowIfCancellationRequested();

                string body;
                try
                {
                    using var response = await client.GetAsync($"{baseUrl}/api/v1/chats/{chatId}", ct);

                    // Never pushed, or already a tombstone — either way the server holds nothing that should
                    // keep this chat, so the local date stands.
                    if (response.StatusCode == HttpStatusCode.NotFound) continue;

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation(
                            "Cannot confirm chat {ChatId} before eviction: status {Status}",
                            chatId, (int)response.StatusCode);
                        return false;
                    }

                    body = await response.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreachable candidate aborts the pass: continuing would evict the rest on exactly
                    // the unconfirmed local dates this check exists to distrust.
                    _logger.LogWarning(ex, "Cannot confirm chat {ChatId} before eviction", chatId);
                    return false;
                }

                // A 200 whose date is missing or unparseable is not a confirmation: proceeding would evict on
                // the local date this whole check exists to distrust.
                if (!TryReadLastAccessed(body, out var remote))
                {
                    _logger.LogInformation(
                        "Cannot confirm chat {ChatId} before eviction: the server's answer carried no access date",
                        chatId);
                    return false;
                }

                await _chatService.ApplyRemoteAccessDateAsync(chatId, remote, ct);
            }
        }

        _logger.LogInformation(
            "Confirmed {Count} chat(s) against the server before eviction", chatIds.Count);
        return true;
    }

    /// <summary>`lastAccessedAt` is top-level plaintext even under E2EE, so this never needs the payload.</summary>
    private static bool TryReadLastAccessed(string body, out DateTime lastAccessedUtc)
    {
        lastAccessedUtc = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("lastAccessedAt", out var value)) return false;
            if (!value.TryGetDateTime(out var parsed)) return false;
            lastAccessedUtc = parsed.ToUniversalTime();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<bool> SendDeleteAsync(Guid chatId, CancellationToken ct)
    {
        var (client, baseUrl, _) = await BuildClientAsync(ct);
        if (client is null || baseUrl is null) return false;

        var url = $"{baseUrl}/api/v1/chats/{chatId}";
        using (client)
        {
            using var response = await client.DeleteAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                _capabilities.Invalidate();
            _logger.LogInformation(
                "Cloud delete for chat {ChatId} returned status {Status}",
                chatId, (int)response.StatusCode);
            // Already gone server-side is the desired end state, so it does not hold the gate open.
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
    }

    /// <summary>
    /// One-time backfill: pushes every locally stored chat to the cloud. Chats
    /// created before cloud sign-in never raised <c>ChatsChanged</c>, so the
    /// event-driven push path alone would never upload them. Runs once per
    /// connection (gated by <c>AssistantChatsBackfilledAt</c>, cleared on logout)
    /// and only after the startup pull, so freshly pulled chats are reconciled by
    /// the upsert path's normal 409-merge rather than overwriting remote state.
    /// <br/>
    /// Returns true when the rate limiter is the only thing standing between this pass and a finished
    /// backfill, i.e. when another pass a minute from now is worth running. Every other unfinished outcome
    /// returns false: retrying a rejected sign-in or a missing E2EE onboarding once a minute fixes nothing.
    /// </summary>
    private async Task<bool> RunStartupPushAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            if (settings.AssistantChatsBackfilledAt is not null) return false;

            // Only what the server has not already accepted. A pass the rate limiter closes down banks its
            // successes, so the next launch resumes from the remainder instead of replaying the catalogue.
            var ids = await _chatService.GetUnbackfilledIdsAsync(ct);
            var allPushed = true;
            var pushed = 0;
            _rateLimited = false;
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                // Reuses the normal op path: fetches the full chat (with messages)
                // and handles 409 conflicts via merge-and-retry.
                if (await ProcessOpAsync(new SyncOp(id, OpKind.Upsert), ct))
                {
                    await _chatService.MarkBackfilledAsync(id, ct);
                    pushed++;
                    continue;
                }

                allPushed = false;

                // Every remaining push in this pass would be rejected too, and the handler does not wait out
                // a Retry-After this long — so firing them spends the catalogue on nothing.
                if (_rateLimited)
                {
                    _logger.LogInformation(
                        "Startup backfill pass stopped by the server's rate limit: pushed {Pushed}, {Remaining} chat(s) still owed",
                        pushed, ids.Count - pushed);
                    return true;
                }
            }

            // If any push hit 403 e2ee_required, the account is E2EE-enabled server-side but
            // this device hasn't onboarded — the chats went out plaintext and were rejected.
            // Do NOT mark the backfill complete: leaving the gate unset re-runs the outstanding
            // chats on the next launch (after onboarding, the pushes succeed encrypted).
            // Marking it done here would strand those chats in the cloud until logout/login.
            if (_syncClient.IsE2EEOnboardingRequired)
            {
                _logger.LogWarning(
                    "Startup backfill deferred: E2EE onboarding required; {Count} chat(s) not yet uploaded, will retry on next launch after onboarding",
                    ids.Count);
                return false;
            }

            // Same reasoning for every other failure: marking a backfill done that the server never
            // accepted strands those chats local-only for good, because only a logout reopens the gate.
            if (!allPushed)
            {
                _logger.LogWarning(
                    "Startup backfill incomplete: pushed {Pushed}, {Remaining} chat(s) still owed; the gate stays unset so the next launch resumes",
                    pushed, ids.Count - pushed);
                return false;
            }

            // Re-read so we don't clobber any settings written meanwhile.
            var toSave = await _settingsService.GetSettingsAsync();
            toSave.AssistantChatsBackfilledAt = DateTime.UtcNow;
            await _settingsService.SaveSettingsAsync(toSave);

            _logger.LogInformation(
                "Startup backfill pushed {Count} pre-existing chat(s) to cloud", ids.Count);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Leave AssistantChatsBackfilledAt unset so the next launch retries the
            // outstanding chats; they stay local-only until then.
            _logger.LogWarning(ex, "Startup backfill push failed; will retry next launch");
            return false;
        }
    }

    private async Task RunStartupPullAsync(CancellationToken ct)
    {
        try
        {
            var (client, baseUrl, userId) = await BuildClientAsync(ct);
            if (client is null || baseUrl is null) return;

            using (client)
            {
                // `since` is the inclusive lower bound per server contract §4.1,
                // so the first paged response will normally re-include the local
                // newest chat. SaveFromRemoteAsync is an upsert, so that's harmless.
                var since = await _chatService.GetMaxUpdatedAtAsync(ct);
                var sinceParam = since is null
                    ? null
                    : Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("O"));

                // Conditional GET: the stored ETag represents the whole chat set (it rides the
                // user's server-side DataVersion, not a page cursor), so it is only echoed on the
                // first page. A 304 there means nothing changed since the last successful full
                // pull. Old servers that don't emit a chat ETag simply return 200 and this stays
                // inert. See docs plan Sec 5.5 (server-side emission is srv-1a).
                // KNOWN LIMITATION: the ETag is decoupled from `since` (ETag lives in settings,
                // `since` is derived from the local chat DB max UpdatedAt) — if the local chat DB
                // is restored/rolled back while settings survive, a DataVersion-only 304 could
                // skip chats that the older `since` should re-fetch. The main sync pull avoids
                // this by folding since.Ticks into its ETag (plan Sec 3.4); flagged for srv-1a to
                // do the same for the chat ETag, or for a future client fix to persist `since`
                // alongside LastChatPullETag and skip If-None-Match when they disagree.
                var chatETag = (await _settingsService.GetSettingsAsync()).LastChatPullETag;
                string? newETag = null;

                string? cursor = null;
                var totalMerged = 0;
                var totalDeleted = 0;
                var pages = 0;
                const int maxPages = 100; // safety stop in case the server lies about hasMore

                while (pages < maxPages)
                {
                    var url = BuildPullUrl(baseUrl, sinceParam, cursor);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    // TryParse (not the EntityTagHeaderValue ctor) because it accepts both strong
                    // and weak (W/"...") tags — the ctor throws FormatException on weak tags, which
                    // would otherwise abort the whole startup pull on every launch after the server
                    // starts emitting a weak DataVersion-backed chat ETag.
                    if (cursor is null && !string.IsNullOrEmpty(chatETag) && EntityTagHeaderValue.TryParse(chatETag, out var ifNoneMatchTag))
                        request.Headers.IfNoneMatch.Add(ifNoneMatchTag);

                    using var response = await client.SendAsync(request, ct);

                    // 304 is not a success status, so handle it before the failure branch and do
                    // NOT invalidate the capability — it is the healthy "no changes" answer.
                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        _logger.LogInformation("Startup pull: 304 Not Modified — no chat changes since last pull");
                        return;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                            _capabilities.Invalidate();
                        _logger.LogInformation(
                            "Startup pull returned status {Status} (page {Page})",
                            (int)response.StatusCode, pages + 1);
                        return;
                    }

                    // Capture the first page's ETag; persist it only after the full pull completes
                    // (below), so a mid-pagination failure never strands an ETag that would
                    // 304-skip an incomplete set next launch.
                    if (cursor is null && response.Headers.ETag is not null)
                        newETag = response.Headers.ETag.ToString();

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

                        SyncAssistantChat plaintext;
                        try
                        {
                            plaintext = _mapper.FromSyncAssistantChat(incoming, userId);
                        }
                        catch (Exception ex)
                        {
                            // Decrypt failure (wrong UMK, AAD mismatch, corrupted ciphertext).
                            // Skip the row rather than polluting the local store / FTS.
                            _logger.LogWarning(ex,
                                "Failed to decrypt incoming chat {ChatId}; skipping", incoming.Id);
                            continue;
                        }

                        try
                        {
                            await _chatService.SaveFromRemoteAsync(plaintext, ct);
                            totalMerged++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to merge incoming chat {ChatId}", incoming.Id);
                        }
                    }

                    if (doc.RootElement.TryGetProperty("deleted", out var deletedArr) &&
                        deletedArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in deletedArr.EnumerateArray())
                        {
                            if (element.ValueKind != JsonValueKind.String) continue;
                            if (!Guid.TryParse(element.GetString(), out var deletedId)) continue;

                            try
                            {
                                await _chatService.DeleteFromRemoteAsync(deletedId, ct);
                                totalDeleted++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "Failed to apply remote delete for chat {ChatId}", deletedId);
                            }
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
                    "Startup pull merged {Merged} chats and applied {Deleted} delete(s) across {Pages} page(s)",
                    totalMerged, totalDeleted, pages);

                // Persist the chat ETag only after the full pull completed (mirrors LastPullETag),
                // so the next launch can 304-skip when nothing changed.
                if (newETag is not null && newETag != chatETag)
                {
                    var toSave = await _settingsService.GetSettingsAsync();
                    toSave.LastChatPullETag = newETag;
                    await _settingsService.SaveSettingsAsync(toSave);
                }
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
        // includeDeleted=true is the opt-in that asks the server to surface
        // tombstone IDs alongside live chats (see assistant-chat-history.md §4.1).
        // Old servers ignore the unknown query param and return the legacy shape;
        // the client just sees no `deleted[]` and falls through to upsert-only.
        var parts = new List<string>(3) { "includeDeleted=true" };
        if (!string.IsNullOrEmpty(sinceParam)) parts.Add($"since={sinceParam}");
        if (!string.IsNullOrEmpty(cursor)) parts.Add($"cursor={Uri.EscapeDataString(cursor)}");
        return $"{baseUrl}/api/v1/chats?{string.Join('&', parts)}";
    }

    private async Task<(HttpClient? Client, string? BaseUrl, string? UserId)> BuildClientAsync(CancellationToken ct)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return (null, null, null);

        // Release writes a ServerUrl on every launch, so this is the only thing keeping a signed-out
        // client off the network — and with no SyncUserId the mapper cannot even encrypt the payload.
        var token = await _authService.GetAccessTokenAsync();
        if (!settings.SyncEnabled || string.IsNullOrEmpty(token)) return (null, null, null);

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        client.Timeout = TimeSpan.FromSeconds(60);
        return (client, serverUrl, settings.SyncUserId);
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to JSON, gzip-compresses it, and wraps it in a
    /// StreamContent tagged Content-Encoding: gzip. The server runs UseRequestDecompression
    /// globally (Program.cs, before endpoint routing), so it transparently covers the
    /// /api/v1/chats/* route. The returned content owns the compressed stream and disposes it.
    /// </summary>
    private static StreamContent CreateGzipJsonContent<T>(T value)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(jsonBytes);
        }
        compressed.Position = 0;
        var content = new StreamContent(compressed);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
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
            // Local wins outright, like WindowMode above: null here means the user re-opened the offer, so
            // coalescing to the server's value would answer it behind their back.
            AgentContextMode = local.AgentContextMode,
            // Local wins too, and local is the STORED value here (the op re-reads the chat), not a stale
            // session snapshot — so this carries the star the user just set instead of dropping it.
            IsFavorite = local.IsFavorite,
            Messages = [.. server.Messages, .. appended],
            ExtensionData = server.ExtensionData,
        };
        return merged;
    }
}
