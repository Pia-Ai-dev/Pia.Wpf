using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class ScreenCaptureToolHandlerTests
{
    [Fact]
    public async Task ListTargets_ReturnsMonitorsThenWindows_WithSnakeCaseFields()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Monitor(@"\\.\DISPLAY1", primary: true));
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Q3 payroll - Excel"));

        using var _ = fixture.OnPiaCloud();
        var result = await fixture.CallAsync(ScreenCaptureToolHandler.ListToolName);

        var json = JsonSerializer.Serialize(result.Result);
        Assert.Contains("\"targets\"", json);
        Assert.Contains("\"kind\":\"monitor\"", json);
        Assert.Contains("\"kind\":\"window\"", json);
        Assert.Contains("\"program\":\"excel\"", json);
        Assert.Contains("\"title\":\"Q3 payroll - Excel\"", json);
        Assert.Contains("\"display\":\"DISPLAY1\"", json);
        Assert.Contains("\"primary\":true", json);
        Assert.Contains("\"minimized\":false", json);
        Assert.Contains("\"index\":1", json);
        Assert.True(json.IndexOf("monitor", StringComparison.Ordinal) < json.IndexOf("window", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListTargets_NotesWhenCaptureIsUnavailable()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Q3 payroll - Excel"));

        var withoutChannel = JsonSerializer.Serialize(
            (await fixture.CallAsync(ScreenCaptureToolHandler.ListToolName)).Result);
        Assert.Contains("\"targets\":[]", withoutChannel);
        Assert.Contains("not available on this turn", withoutChannel);
        Assert.DoesNotContain("payroll", withoutChannel);

        using var _ = fixture.On(AiProviderType.OpenAI);
        var onOpenAi = JsonSerializer.Serialize(
            (await fixture.CallAsync(ScreenCaptureToolHandler.ListToolName)).Result);
        Assert.Contains("\"targets\":[]", onOpenAi);
        Assert.Contains("Pia Cloud", onOpenAi);
        Assert.Equal(0, fixture.Capture.EnumerateCalls);
    }

    [Fact]
    public async Task ListTargets_Unattended_ShowsOnlyAllowlistedWindowsAndNoDisplay()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Monitor(@"\\.\DISPLAY1", primary: true));
        fixture.Capture.Targets.Add(Fixture.Window("outlook", "Inbox - Outlook"));
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Q3 payroll - Excel"));
        fixture.Entries.Add(new ScreenCaptureAllowlistEntry(
            Guid.NewGuid(), "outlook", string.Empty, DateTimeOffset.UtcNow));

        using var _ = fixture.OnPiaCloud();
        using var __ = Fixture.InTask(Guid.NewGuid(), granter: "background:run");
        var json = JsonSerializer.Serialize((await fixture.CallAsync(ScreenCaptureToolHandler.ListToolName)).Result);

        Assert.Contains("Inbox - Outlook", json);
        Assert.DoesNotContain("payroll", json);
        Assert.DoesNotContain("\"kind\":\"monitor\"", json);
        Assert.Contains("Tool access", json);
    }

    [Fact]
    public async Task ListTargets_UnattendedWithAnEmptyAllowlist_ShowsNothing()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Monitor(@"\\.\DISPLAY1", primary: true));
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Q3 payroll - Excel"));

        using var _ = fixture.OnPiaCloud();
        using var __ = Fixture.InTask(Guid.NewGuid(), granter: "background:run");
        var json = JsonSerializer.Serialize((await fixture.CallAsync(ScreenCaptureToolHandler.ListToolName)).Result);

        Assert.Contains("\"targets\":[]", json);
        Assert.DoesNotContain("payroll", json);
    }

    /// <summary>The granter, not the task id, is what says nobody is watching.</summary>
    [Fact]
    public async Task ListTargets_InAnInteractiveTask_IsNotFiltered()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Monitor(@"\\.\DISPLAY1", primary: true));
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Q3 payroll - Excel"));

        using var _ = fixture.OnPiaCloud();
        using var __ = Fixture.InTask(Guid.NewGuid(), granter: null);
        var json = JsonSerializer.Serialize((await fixture.CallAsync(ScreenCaptureToolHandler.ListToolName)).Result);

        Assert.Contains("payroll", json);
        Assert.Contains("\"kind\":\"monitor\"", json);
    }

    [Fact]
    public async Task Capture_WithoutAChannel_RefusesBeforeEnumerating()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));

        var result = await fixture.CaptureAsync("window", "excel");

        Assert.Equal(ScreenCaptureToolHandler.NoDeliveryChannel, result.Result);
        Assert.Null(result.PendingAction);
        Assert.Equal(0, fixture.Capture.EnumerateCalls);
    }

    /// <summary>Every provider that is not Pia Cloud, from the enum itself, so a new one is covered the day it
    /// is added.</summary>
    public static TheoryData<AiProviderType> NonPiaCloudProviders()
    {
        var data = new TheoryData<AiProviderType>();
        foreach (var type in Enum.GetValues<AiProviderType>())
            if (type != AiProviderType.PiaCloud) data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(NonPiaCloudProviders))]
    public async Task Capture_OnANonPiaCloudProvider_RefusesBeforeEnumerating(AiProviderType providerType)
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));

        using var _ = fixture.On(providerType);
        var result = await fixture.CaptureAsync("window", "excel");

        Assert.Equal(ScreenCaptureToolHandler.ProviderUnsupported, result.Result);
        Assert.Null(result.PendingAction);
        Assert.Equal(0, fixture.Capture.EnumerateCalls);
    }

    /// <summary>Provider argument JSON arrives as JsonElement, whose ToString would quote the value.</summary>
    [Fact]
    public async Task Capture_AcceptsJsonElementArguments()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """{"target":"window","match":"excel"}""")!;

        using var _ = fixture.OnPiaCloud();
        var result = await fixture.CallAsync(ScreenCaptureToolHandler.CaptureToolName, args);

        Assert.Null(result.Result);
        Assert.NotNull(result.PendingAction);
    }

    [Fact]
    public async Task Capture_OnPiaCloud_ProceedsToResolution()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));

        using var _ = fixture.OnPiaCloud();
        var result = await fixture.CaptureAsync("window", "excel");

        Assert.Null(result.Result);
        Assert.NotNull(result.PendingAction);
        Assert.Equal(1, fixture.Capture.EnumerateCalls);
    }

    [Fact]
    public async Task Capture_UnknownKind_Refuses()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));

        using var _ = fixture.OnPiaCloud();
        Assert.Equal(ScreenCaptureToolHandler.UnknownKind, (await fixture.CaptureAsync("screen", "excel")).Result);
    }

    [Fact]
    public async Task Capture_MissingMatch_Refuses()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));

        using var _ = fixture.OnPiaCloud();
        Assert.Equal(ScreenCaptureToolHandler.MissingMatch, (await fixture.CaptureAsync("window", null)).Result);
    }

    [Fact]
    public async Task Capture_NoMatch_NamesWhatWasAskedFor()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));

        using var _ = fixture.OnPiaCloud();
        var result = await fixture.CaptureAsync("window", "outlook");

        Assert.Equal(string.Format(ScreenCaptureToolHandler.NoMatch, "outlook"), result.Result);
    }

    [Fact]
    public async Task Capture_Ambiguous_NamesTheProgramsButNotTheTitles()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("outlook", "Inbox - Outlook"));
        fixture.Capture.Targets.Add(Fixture.Window("outlook", "Q3 payroll - Outlook"));

        using var _ = fixture.OnPiaCloud();
        var result = await fixture.CaptureAsync("window", "outlook");

        Assert.Equal(string.Format(ScreenCaptureToolHandler.Ambiguous, 2, "outlook", "outlook"), result.Result);
        Assert.DoesNotContain("payroll", (string)result.Result!);
    }

    [Fact]
    public async Task Capture_MinimizedWindow_RefusesBeforeCapturing()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("outlook", "Inbox", minimized: true));

        using var _ = fixture.OnPiaCloud();
        var result = await fixture.CaptureAsync("window", "outlook");

        Assert.Equal(string.Format(ScreenCaptureToolHandler.Minimized, "outlook"), result.Result);
        Assert.Equal(0, fixture.Capture.CaptureCalls);
    }

    [Fact]
    public async Task Capture_Interactive_MintsAPendingActionWhoseDetailsAndDescriptionNeverCarryTheTitle()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Q3 payroll - Excel"));

        using var _ = fixture.OnPiaCloud();
        using var __ = Fixture.InTask(Guid.NewGuid(), granter: null);
        var result = await fixture.CaptureAsync("window", "excel");

        Assert.NotNull(result.PendingAction);
        var pending = result.PendingAction!;
        Assert.Equal("Tool_Screen_Desc_CaptureWindow:excel", pending.Description);
        Assert.Equal(
            "Tool_Screen_Detail_Target: Tool_Screen_Target_Window\n"
            + "Tool_Screen_Detail_Program: excel\n"
            + "Tool_Screen_Detail_Size: 800x600",
            pending.Details);
        Assert.DoesNotContain("payroll", pending.Details!);
        Assert.DoesNotContain("payroll", pending.Description);
    }

    [Fact]
    public async Task Capture_Monitor_DescribesTheDisplay()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Monitor(@"\\.\DISPLAY1", primary: true));

        using var _ = fixture.OnPiaCloud();
        var result = await fixture.CaptureAsync("monitor", null);

        Assert.NotNull(result.PendingAction);
        var pending = result.PendingAction!;
        Assert.Equal("Tool_Screen_Desc_CaptureMonitor:DISPLAY1", pending.Description);
        Assert.Contains("Tool_Screen_Detail_Display: DISPLAY1", pending.Details!);
        Assert.Contains("Tool_Screen_Target_Monitor", pending.Details!);
    }

    [Fact]
    public async Task Execute_Success_ParksTheImage_RecordsTheAudit_NotifiesTheIndicator_ReturnsTheMarker()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Q3 payroll - Excel"));
        var taskId = Guid.NewGuid();

        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);
        using var _ = fixture.On(channel);
        using var __ = Fixture.InTask(taskId, granter: null);
        var prepared = await fixture.CaptureAsync("window", "excel");
        var marker = await prepared.PendingAction!.Execute();

        Assert.Equal(1, channel.Count);
        var image = Assert.Single(channel.Drain());
        Assert.Equal("call-1", image.CallId);
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.DoesNotContain("payroll", image.Caption);

        Assert.Equal(
            "Captured window excel at 4x4. The picture is attached as the next message; read it from there.",
            marker);

        var evt = Assert.Single(fixture.Audited);
        Assert.Equal(ScreenCaptureSurfaces.Interactive, evt.Surface);
        Assert.Equal(taskId, evt.TaskId);
        Assert.Null(evt.Granter);
        Assert.Equal(ScreenCaptureTargetKinds.Window, evt.TargetKind);
        Assert.Equal("excel", evt.ProcessName);
        Assert.Equal("Q3 payroll - Excel", evt.WindowTitle);
        Assert.Equal(4, evt.Width);

        fixture.Indicator.Received(1).NotifyCapture(evt);
    }

    [Fact]
    public async Task Execute_MonitorSuccess_AuditsTheKindWithoutATitle()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Monitor(@"\\.\DISPLAY1", primary: true));

        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);
        using var _ = fixture.On(channel);
        var prepared = await fixture.CaptureAsync("monitor", null);
        var marker = await prepared.PendingAction!.Execute();

        Assert.Equal("Captured display DISPLAY1 at 4x4. The picture is attached as the next message; read it from there.", marker);
        var evt = Assert.Single(fixture.Audited);
        Assert.Equal(ScreenCaptureTargetKinds.Monitor, evt.TargetKind);
        Assert.Null(evt.WindowTitle);
        Assert.Equal(ScreenCaptureSurfaces.Unknown, evt.Surface);
    }

    [Theory]
    [InlineData(CaptureFailureReason.TargetGone)]
    [InlineData(CaptureFailureReason.Cloaked)]
    [InlineData(CaptureFailureReason.SelfExclusionFailed)]
    [InlineData(CaptureFailureReason.UniformFrame)]
    [InlineData(CaptureFailureReason.Timeout)]
    [InlineData(CaptureFailureReason.NativeError)]
    public async Task Execute_CaptureFailure_ReturnsTheReasonText_RecordsNothing(CaptureFailureReason reason)
    {
        var fixture = new Fixture();
        var window = Fixture.Window("excel", "Book1");
        fixture.Capture.Targets.Add(window);
        fixture.Capture.NextResult = CaptureResult.Failed(window, reason);

        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);
        using var _ = fixture.On(channel);
        var prepared = await fixture.CaptureAsync("window", "excel");
        var answer = await prepared.PendingAction!.Execute();

        Assert.StartsWith("Nothing was captured: ", (string)answer!);
        Assert.Empty(fixture.Audited);
        fixture.Indicator.DidNotReceive().NotifyCapture(Arg.Any<ScreenCaptureAuditEvent>());
        Assert.Equal(0, channel.Count);
    }

    [Fact]
    public async Task Execute_PrepareReturnsNull_ReturnsTooLarge_ParksNothing()
    {
        var fixture = new Fixture { Prepare = _ => null };
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));

        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);
        using var _ = fixture.On(channel);
        var prepared = await fixture.CaptureAsync("window", "excel");

        Assert.Equal(ScreenCaptureToolHandler.TooLarge, await prepared.PendingAction!.Execute());
        Assert.Equal(0, channel.Count);
        Assert.Empty(fixture.Audited);
    }

    /// <summary>The card is confirmed long after the turn's ambients are restored, so both facts have to be
    /// read at prepare time and carried in the closure.</summary>
    [Fact]
    public async Task Execute_ReadsTheAmbientsAtPrepareTime()
    {
        var fixture = new Fixture();
        fixture.Capture.Targets.Add(Fixture.Window("excel", "Book1"));
        var taskId = Guid.NewGuid();

        var channel = new ToolLoopImageChannel(AiProviderType.PiaCloud);
        ScreenCaptureToolCall pending;
        using (fixture.On(channel))
        using (Fixture.InTask(taskId, granter: null))
        {
            pending = (await fixture.CaptureAsync("window", "excel")).PendingAction!;
        }

        Assert.Null(ToolLoopImageChannel.Current);
        Assert.Null(TaskAmbient.Current);

        await pending.Execute();

        Assert.Equal(1, channel.Count);
        var evt = Assert.Single(fixture.Audited);
        Assert.Equal(ScreenCaptureSurfaces.Interactive, evt.Surface);
        Assert.Equal(taskId, evt.TaskId);
    }

    [Fact]
    public async Task ExecutePendingActionAsync_TurnsAThrowIntoAnAnswer()
    {
        var fixture = new Fixture();
        var pending = new ScreenCaptureToolCall(
            ScreenCaptureToolHandler.CaptureToolName, "d", null,
            () => throw new InvalidOperationException("boom"));

        var answer = await fixture.Handler.ExecutePendingActionAsync(pending);

        Assert.Equal("Error executing screen_capture: boom", answer);
    }

    [Fact]
    public void GetTools_OffersBothToolsUnderTheirWireNames()
    {
        var names = new Fixture().Handler.GetTools().Select(t => t.Name).ToList();

        Assert.Equal([ScreenCaptureToolHandler.ListToolName, ScreenCaptureToolHandler.CaptureToolName], names);
    }

    [Fact]
    public async Task UnknownTool_IsAnswered_NotThrown()
    {
        var fixture = new Fixture();

        var result = await fixture.CallAsync("screen_teleport");

        Assert.Equal("Unknown tool: screen_teleport", result.Result);
    }

    internal sealed class Fixture
    {
        public FakeScreenCaptureService Capture { get; } = new();

        public IScreenCaptureAllowlistStore Allowlist { get; } = Substitute.For<IScreenCaptureAllowlistStore>();

        public IScreenCaptureIndicator Indicator { get; } = Substitute.For<IScreenCaptureIndicator>();

        public List<ScreenCaptureAuditEvent> Audited { get; } = [];

        public Func<BitmapSource, ImageAttachment?>? Prepare { get; init; }

        public List<ScreenCaptureAllowlistEntry> Entries { get; } = [];

        private ScreenCaptureToolHandler? _handler;

        public ScreenCaptureToolHandler Handler
        {
            get
            {
                if (_handler is not null) return _handler;

                Allowlist.ListAsync().Returns(_ => Task.FromResult<IReadOnlyList<ScreenCaptureAllowlistEntry>>(Entries));
                var audit = Substitute.For<IScreenCaptureAuditLog>();
                audit.When(a => a.Record(Arg.Any<ScreenCaptureAuditEvent>()))
                    .Do(ci => Audited.Add(ci.Arg<ScreenCaptureAuditEvent>()));

                return _handler = new ScreenCaptureToolHandler(
                    Capture, Allowlist, audit, Indicator, KeyEchoLocalizer(),
                    NullLogger<ScreenCaptureToolHandler>.Instance,
                    Prepare ?? (_ => Attachment()));
            }
        }

        public Task<(object? Result, ScreenCaptureToolCall? PendingAction)> CallAsync(
            string toolName, IDictionary<string, object?>? args = null) =>
            Handler.HandleToolCallAsync(new FunctionCallContent("call-1", toolName, args), TestContext.Current.CancellationToken);

        public Task<(object? Result, ScreenCaptureToolCall? PendingAction)> CaptureAsync(string? target, string? match)
        {
            var args = new Dictionary<string, object?>();
            if (target is not null) args["target"] = target;
            if (match is not null) args["match"] = match;
            return CallAsync(ScreenCaptureToolHandler.CaptureToolName, args);
        }

        public IDisposable OnPiaCloud() => On(AiProviderType.PiaCloud);

        public IDisposable On(AiProviderType providerType) => On(new ToolLoopImageChannel(providerType));

        public IDisposable On(ToolLoopImageChannel channel)
        {
            ToolLoopImageChannel.Current = channel;
            return new Restore(() => ToolLoopImageChannel.Current = null);
        }

        public static IDisposable InTask(Guid? taskId, string? granter)
        {
            TaskAmbient.Current = new TaskContext(taskId, null, UnattendedGranter: granter);
            return new Restore(() => TaskAmbient.Current = null);
        }

        public static CaptureTarget Window(string process, string title, bool minimized = false) =>
            new(CaptureTargetKind.Window, 1, string.Empty, new PixelRect(0, 0, 800, 600),
                process, title, false, minimized);

        public static CaptureTarget Monitor(string deviceId, bool primary) =>
            new(CaptureTargetKind.Monitor, 0, deviceId, new PixelRect(0, 0, 1920, 1080),
                string.Empty, string.Empty, primary, false);

        internal static ILocalizationService KeyEchoLocalizer()
        {
            var loc = Substitute.For<ILocalizationService>();
            loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
            loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
                .Returns(ci => $"{ci[0]}:{string.Join(",", (object[])ci[1])}");
            return loc;
        }

        internal static ImageAttachment Attachment() => new()
        {
            JpegBytes = [1, 2, 3],
            MimeType = "image/jpeg",
            Width = 4,
            Height = 4,
            Thumbnail = Frame(),
        };

        internal static BitmapSource Frame()
        {
            var frame = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
            frame.Freeze();
            return frame;
        }

        private sealed class Restore(Action undo) : IDisposable
        {
            public void Dispose() => undo();
        }
    }

    internal sealed class FakeScreenCaptureService : IScreenCaptureService
    {
        public List<CaptureTarget> Targets { get; } = [];

        public CaptureResult? NextResult { get; set; }

        public int EnumerateCalls { get; private set; }

        public int CaptureCalls { get; private set; }

        public Task<IReadOnlyList<CaptureTarget>> EnumerateTargetsAsync(CancellationToken cancellationToken = default)
        {
            EnumerateCalls++;
            return Task.FromResult<IReadOnlyList<CaptureTarget>>(Targets);
        }

        public Task<CaptureResult> CaptureAsync(CaptureTarget target, CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            return Task.FromResult(NextResult ?? CaptureResult.Success(target, Fixture.Frame()));
        }
    }
}
