using System.ComponentModel;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Imaging;
using Pia.Services.Interfaces;
using Pia.Services.Screen;

namespace Pia.Services;

/// <summary>Lets the model look at one window or one display: the picture cannot travel as a tool result, so it
/// is parked on the round's image channel and the result is only a marker.</summary>
public class ScreenCaptureToolHandler : IScreenCaptureToolHandler
{
    public const string CaptureToolName = "screen_capture";
    public const string ListToolName = "screen_list_targets";

    internal const string NoDeliveryChannel =
        "Not run: this call reached the tool outside a live model turn, so there is no way to hand you the "
        + "picture. Call screen_capture again from your next reply and the picture will be attached to it.";

    internal const string ProviderUnsupported =
        "Refused: pictures can only be sent to the Pia Cloud provider, and this turn runs on a different "
        + "provider. Do not retry; tell the user Pia can only see the screen when Pia Cloud is the assistant "
        + "provider.";

    internal const string UnknownKind = "Error: target must be \"window\" or \"monitor\".";

    internal const string MissingMatch =
        "Error: name the window with match — a program name such as outlook, or a fragment of the window "
        + "title. Call screen_list_targets to see what is open.";

    internal const string NoMatch =
        "No open window matches '{0}'. Call screen_list_targets to see what is open; do not guess. Nothing "
        + "was captured.";

    internal const string Ambiguous =
        "{0} open windows match '{1}' ({2}); narrow match with a fragment of the window title. Nothing was "
        + "captured.";

    internal const string Minimized =
        "The matching {0} window is minimized. Ask the user to restore it — Pia does not move windows on its "
        + "own. Nothing was captured.";

    internal const string UnattendedMonitor =
        "Refused: a run nobody is watching may not capture a whole display, only a window the user listed "
        + "under Settings → Assistant → Tool access. Nothing was captured.";

    internal const string UnattendedNotListed =
        "Refused: '{0}' is not on the screen-capture allowlist, so a run nobody is watching may not capture "
        + "it (Settings → Assistant → Tool access). Nothing was captured.";

    internal const string UnattendedAmbiguous =
        "Refused: the allowlist entry for '{0}' matches {1} open windows, so none of them is the window named "
        + "in advance. Nothing was captured; the user can narrow the entry with a title fragment.";

    internal const string TooLarge =
        "Nothing was captured: the picture could not be encoded within the size limit.";

    internal const string ListNote =
        "Pass match to screen_capture as the program name or a title fragment for a window, or the display "
        + "name/index for a monitor.";

    internal const string ListNoteUnattended =
        "This run has nobody watching, so it sees only the windows the user listed under Settings → Assistant "
        + "→ Tool access, and no displays. Pass match to screen_capture as the program name or a title "
        + "fragment.";

    private readonly IScreenCaptureService _capture;
    private readonly IScreenCaptureAllowlistStore _allowlist;
    private readonly IScreenCaptureAuditLog _audit;
    private readonly IScreenCaptureIndicator _indicator;
    private readonly ILocalizationService _localization;
    private readonly ILogger<ScreenCaptureToolHandler> _logger;
    private readonly Func<BitmapSource, ImageAttachment?> _prepare;

    public ScreenCaptureToolHandler(
        IScreenCaptureService capture,
        IScreenCaptureAllowlistStore allowlist,
        IScreenCaptureAuditLog audit,
        IScreenCaptureIndicator indicator,
        ILocalizationService localization,
        ILogger<ScreenCaptureToolHandler> logger)
        : this(capture, allowlist, audit, indicator, localization, logger, prepare: null)
    {
    }

    internal ScreenCaptureToolHandler(
        IScreenCaptureService capture,
        IScreenCaptureAllowlistStore allowlist,
        IScreenCaptureAuditLog audit,
        IScreenCaptureIndicator indicator,
        ILocalizationService localization,
        ILogger<ScreenCaptureToolHandler> logger,
        Func<BitmapSource, ImageAttachment?>? prepare)
    {
        _capture = capture;
        _allowlist = allowlist;
        _audit = audit;
        _indicator = indicator;
        _localization = localization;
        _logger = logger;
        _prepare = prepare ?? (bitmap => ImageAttachmentProcessor.TryPrepare(bitmap, logger));
    }

    /// <summary>No settings toggle: the plugin's own enable switch is the off switch, and a turn that cannot
    /// deliver a picture refuses legibly instead of hiding the tool.</summary>
    public bool IsAvailable => true;

    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(ListTargetsSchema, ListToolName),
        AIFunctionFactory.Create(CaptureSchema, CaptureToolName),
    ];

    public async Task<(object? Result, ScreenCaptureToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ScreenCaptureToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        return toolCall.Name switch
        {
            ListToolName => (await ListTargetsAsync(cancellationToken), null),
            CaptureToolName => await PrepareCaptureAsync(toolCall, args, cancellationToken),
            _ => ($"Unknown tool: {toolCall.Name}", (ScreenCaptureToolCall?)null),
        };
    }

    public async Task<object?> ExecutePendingActionAsync(ScreenCaptureToolCall pendingAction)
    {
        _logger.LogDebug("Executing screen action: {ToolName}", pendingAction.ToolName);
        try
        {
            return await pendingAction.Execute();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute screen tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    private async Task<object?> ListTargetsAsync(CancellationToken cancellationToken)
    {
        var unavailable = UnavailableReason(ToolLoopImageChannel.Current);
        if (unavailable is not null)
        {
            // Window titles are the user's content and a turn that can never receive the picture has no use
            // for them.
            _logger.LogInformation("screen_list_targets withheld the target list: {Reason}", unavailable);
            return new TargetList([], $"{ListNote} Capture is not available on this turn: {unavailable}.");
        }

        var targets = await _capture.EnumerateTargetsAsync(cancellationToken);
        var unattended = TaskAmbient.Current?.UnattendedGranter is not null;
        List<CaptureTarget> monitors = unattended
            ? []
            : targets.Where(t => t.Kind == CaptureTargetKind.Monitor).ToList();
        var windows = targets.Where(t => t.Kind == CaptureTargetKind.Window).ToList();

        if (unattended)
        {
            // Told about no window it could not capture anyway: a title is the user's content too.
            var entries = await _allowlist.ListAsync();
            windows = windows
                .Where(w => entries.Any(e => ScreenCaptureAllowlistMatcher.Matches(e, w.ProcessName, w.Title)))
                .ToList();
        }

        var rows = new List<TargetRow>(monitors.Count + windows.Count);
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            rows.Add(new TargetRow(
                ScreenCaptureTargetKinds.Monitor, i + 1, null, null,
                ScreenTargetResolver.DeviceTail(m.MonitorDeviceId),
                m.Bounds.Width, m.Bounds.Height, m.IsPrimary, false));
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            rows.Add(new TargetRow(
                ScreenCaptureTargetKinds.Window, i + 1, w.ProcessName, w.Title, null,
                w.Bounds.Width, w.Bounds.Height, false, w.IsMinimized));
        }

        _logger.LogInformation(
            "screen_list_targets returned {Monitors} monitor(s), {Windows} window(s) (unattended: {Unattended})",
            monitors.Count, windows.Count, unattended);

        return new TargetList(rows, unattended ? ListNoteUnattended : ListNote);
    }

    private async Task<(object? Result, ScreenCaptureToolCall? PendingAction)> PrepareCaptureAsync(
        FunctionCallContent toolCall, IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        // Read here, never inside Execute: the card is confirmed long after the turn's ambients are restored.
        var channel = ToolLoopImageChannel.Current;
        if (channel is null)
        {
            _logger.LogInformation("screen_capture refused: no tool loop is carrying this call");
            return (NoDeliveryChannel, null);
        }

        if (channel.ProviderType != AiProviderType.PiaCloud)
        {
            _logger.LogInformation(
                "screen_capture refused: {ProviderType} cannot receive a picture", channel.ProviderType);
            return (ProviderUnsupported, null);
        }

        var ctx = TaskAmbient.Current;
        var granter = ctx?.UnattendedGranter;
        var taskId = ctx?.TaskId;
        var surface = granter is not null
            ? ScreenCaptureSurfaces.Unattended
            : taskId is not null ? ScreenCaptureSurfaces.Interactive : ScreenCaptureSurfaces.Unknown;

        var kind = GetOptionalStringArg(args, "target");
        var match = GetOptionalStringArg(args, "match");

        var targets = await _capture.EnumerateTargetsAsync(cancellationToken);
        var resolved = ScreenTargetResolver.Resolve(targets, kind, match);
        switch (resolved.Outcome)
        {
            case ScreenTargetResolution.UnknownKind:
                return (UnknownKind, null);
            case ScreenTargetResolution.MissingMatch:
                return (MissingMatch, null);
            case ScreenTargetResolution.NoMatch:
                return (string.Format(NoMatch, match ?? string.Empty), null);
            case ScreenTargetResolution.Ambiguous:
                var programs = string.Join(
                    ", ",
                    resolved.Candidates.Select(c => c.ProcessName).Where(p => p.Length > 0).Distinct());
                return (string.Format(Ambiguous, resolved.Candidates.Count, match ?? string.Empty, programs), null);
        }

        var target = resolved.Target!;
        if (target.IsMinimized)
            return (string.Format(Minimized, target.ProcessName), null);

        if (granter is not null)
        {
            // Never listing a display is what keeps an unattended run to windows the user named in advance,
            // so the refusal has to come before the store is even read.
            if (target.Kind == CaptureTargetKind.Monitor)
            {
                _logger.LogInformation("screen_capture refused a display to an unattended run");
                return (UnattendedMonitor, null);
            }

            var entries = await _allowlist.ListAsync();
            var visible = targets
                .Where(t => t.Kind == CaptureTargetKind.Window)
                .Select(t => new AllowlistWindow(t.ProcessName, t.Title))
                .ToList();
            var verdict = ScreenCaptureAllowlistMatcher.Resolve(
                entries, new AllowlistWindow(target.ProcessName, target.Title), visible);

            if (verdict.Verdict == AllowlistVerdict.NotListed)
            {
                _logger.LogInformation(
                    "screen_capture refused an unlisted window of {Process} to an unattended run", target.ProcessName);
                return (string.Format(UnattendedNotListed, target.ProcessName), null);
            }

            if (verdict.Verdict == AllowlistVerdict.Ambiguous)
            {
                _logger.LogInformation(
                    "screen_capture refused an ambiguous allowlist entry for {Process} ({Count} windows)",
                    target.ProcessName, verdict.MatchCount);
                return (string.Format(UnattendedAmbiguous, target.ProcessName, verdict.MatchCount), null);
            }
        }

        var isWindow = target.Kind == CaptureTargetKind.Window;
        var displayLabel = ScreenTargetResolver.DeviceTail(target.MonitorDeviceId);
        var kindLabel = isWindow
            ? _localization["Tool_Screen_Target_Window"]
            : _localization["Tool_Screen_Target_Monitor"];
        var subject = isWindow ? target.ProcessName : displayLabel;

        var description = isWindow
            ? _localization.Format("Tool_Screen_Desc_CaptureWindow", target.ProcessName)
            : _localization.Format("Tool_Screen_Desc_CaptureMonitor", displayLabel);

        // "Label: value" lines, never JSON, and never the window title: the card renders these verbatim.
        var details = $"{_localization["Tool_Screen_Detail_Target"]}: {kindLabel}\n"
            + (isWindow
                ? $"{_localization["Tool_Screen_Detail_Program"]}: {target.ProcessName}\n"
                : $"{_localization["Tool_Screen_Detail_Display"]}: {displayLabel}\n")
            + $"{_localization["Tool_Screen_Detail_Size"]}: {target.Bounds.Width}x{target.Bounds.Height}";

        _logger.LogInformation(
            "screen_capture proposing {Target} (surface: {Surface})", target.ToString(), surface);
        _logger.SensitiveDebug("screen_capture proposed target title: {Title}", target.Title);

        var callId = toolCall.CallId;
        var pending = new ScreenCaptureToolCall(
            CaptureToolName,
            description,
            details,
            () => CaptureAsync(channel, callId, target, surface, taskId, granter, subject));

        return (null, pending);
    }

    private async Task<object?> CaptureAsync(
        ToolLoopImageChannel channel, string callId, CaptureTarget target,
        string surface, Guid? taskId, string? granter, string subject)
    {
        var result = await _capture.CaptureAsync(target);
        if (!result.IsSuccess)
        {
            _logger.LogInformation("screen_capture produced no frame: {Reason}", result.Reason);
            return $"Nothing was captured: {Describe(result.Reason)}";
        }

        // JPEG encoding on the loop's thread would block the interactive turn it belongs to.
        var bitmap = result.Bitmap!;
        var image = await Task.Run(() => _prepare(bitmap));
        if (image is null) return TooLarge;

        var isWindow = target.Kind == CaptureTargetKind.Window;
        var kindWord = isWindow ? "window" : "display";
        var caption = $"Screen capture from your {CaptureToolName} call: {kindWord} {subject}, "
            + $"{image.Width}x{image.Height} px.";

        channel.Park(new ToolLoopImage(callId, image.JpegBytes, image.MimeType, image.Width, image.Height, caption));

        var evt = new ScreenCaptureAuditEvent(
            surface, taskId, granter,
            isWindow ? ScreenCaptureTargetKinds.Window : ScreenCaptureTargetKinds.Monitor,
            target.ProcessName,
            isWindow ? target.Title : null,
            image.Width, image.Height);
        _audit.Record(evt);
        _indicator.NotifyCapture(evt);

        _logger.LogInformation(
            "screen_capture delivered a {Kind} of {Process} at {Width}x{Height}",
            kindWord, target.ProcessName, image.Width, image.Height);
        _logger.SensitiveDebug("screen_capture delivered target title: {Title}", target.Title);

        return $"Captured {kindWord} {subject} at {image.Width}x{image.Height}. The picture is attached as "
            + "the next message; read it from there.";
    }

    private static string? UnavailableReason(ToolLoopImageChannel? channel) => channel switch
    {
        null => "this call reached the tool outside a live model turn",
        { ProviderType: not AiProviderType.PiaCloud } => "pictures can only be sent to the Pia Cloud provider",
        _ => null,
    };

    /// <summary>English by design, like every other model-facing refusal: this text is not shown to a user.</summary>
    private static string Describe(CaptureFailureReason reason) => reason switch
    {
        CaptureFailureReason.TargetGone => "that window or display is no longer available",
        CaptureFailureReason.Minimized => "the window is minimized",
        CaptureFailureReason.Cloaked => "Windows is hiding that window (another virtual desktop or a suspended app)",
        CaptureFailureReason.EmptyBounds => "the window has no visible area",
        CaptureFailureReason.SelfTarget => "Pia does not capture its own windows",
        CaptureFailureReason.SelfExclusionFailed => "Pia could not hide its own window from the picture",
        CaptureFailureReason.UniformFrame => "the frame came back blank; the app may block screen capture",
        CaptureFailureReason.SelfBlackout =>
            "Pia's own window covers that display and this version of Windows can only hide it by painting it "
            + "black. Ask the user to move or minimize Pia, or capture the window itself instead",
        CaptureFailureReason.Timeout => "the window did not respond in time",
        _ => "Windows refused the capture",
    };

    private static string? GetOptionalStringArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        var str = value.ToString();
        return string.IsNullOrEmpty(str) ? null : str;
    }

    // Schema methods — the parameter signature and [Description] attributes ARE the tool metadata for
    // AIFunctionFactory. The body is never invoked (dispatch is by tool name in HandleToolCallAsync).
    [Description("List the open windows and the displays the assistant could capture. Titles are the windows' own titles.")]
    private static string ListTargetsSchema() => "";

    [Description("Take one picture of the user's screen for you to look at. Asks the user first.")]
    private static string CaptureSchema(
        [Description("\"window\" or \"monitor\"")] string target,
        [Description("Window: the program name (e.g. outlook) or a fragment of the window title; must match exactly one open window. Monitor: the display name (DISPLAY1) or index; omit for the main display.")] string? match = null) => "";

    /// <summary>snake_case: these serialize straight to the provider.</summary>
    private sealed record TargetRow(
        string kind, int index, string? program, string? title, string? display,
        int width, int height, bool primary, bool minimized);

    private sealed record TargetList(IReadOnlyList<TargetRow> targets, string note);
}
