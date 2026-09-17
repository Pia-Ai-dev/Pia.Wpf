using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A binding failure inside a <c>DataTemplate</c> is invisible to every other test here: the template is out
/// of <see cref="BindingPathWalker"/>'s reach, WPF swallows the fault, and the bubble just renders empty. So
/// this listens to WPF's own data-binding trace across a real layout pass.
/// </summary>
[Collection("WpfApplicationStatic")]
public class UserBubbleAttachmentBindingTests
{
    private sealed class CapturingListener : TraceListener
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public override void Write(string? message) => Append(message);

        public override void WriteLine(string? message) => Append(message);

        private void Append(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _lines.Add(message);
        }
    }

    /// <summary>The regression this exists for: an indexer evaluated against an empty collection faults on
    /// every plain user turn, which is most of them.</summary>
    [Fact]
    public void AUserTurnWithNoAttachment_RaisesNoAttachmentBindingError()
    {
        var lines = RenderAndCaptureBindingTrace(attachments: 0);

        Assert.Empty(AttachmentFaults(lines));
    }

    [Fact]
    public void AUserTurnWithAnAttachment_RaisesNoAttachmentBindingError()
    {
        var lines = RenderAndCaptureBindingTrace(attachments: 1);

        Assert.Empty(AttachmentFaults(lines));
    }

    [Fact]
    public void AUserTurnWithFourAttachments_RaisesNoAttachmentBindingError()
    {
        var lines = RenderAndCaptureBindingTrace(attachments: 4);

        Assert.Empty(AttachmentFaults(lines));
    }

    // Scoped to the attachment path rather than asserting silence: PiaCollapsibleMessageText binds null into
    // AutomationId while its template applies, in both arms, and that noise is not this template's to answer for.
    private static IReadOnlyList<string> AttachmentFaults(IReadOnlyList<string> lines) =>
        [.. lines.Where(l =>
            l.Contains("Attachment", StringComparison.OrdinalIgnoreCase)
            || l.Contains("Thumbnail", StringComparison.OrdinalIgnoreCase))];

    private static IReadOnlyList<string> RenderAndCaptureBindingTrace(int attachments)
    {
        var listener = new CapturingListener();
        var source = PresentationTraceSources.DataBindingSource;
        var originalLevel = source.Switch.Level;

        PresentationTraceSources.Refresh();
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Error | SourceLevels.Warning;

        AssistantViewModel? vm = null;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                var view = new Pia.Views.AssistantView { DataContext = vm };

                var message = new AssistantMessage(ChatRole.User, "a turn");
                for (var i = 0; i < attachments; i++) message.Attachments.Add(NewAttachment());
                vm.Messages.Add(message);

                // A collapsed ScrollViewer measures nothing, so no container would be generated.
                vm.HasMessages = true;
                view.Measure(new Size(1000, 900));
                view.Arrange(new Rect(0, 0, 1000, 900));
                view.UpdateLayout();

                _ = view.MessageItemsControl.ItemContainerGenerator.ContainerFromIndex(0)
                    ?? throw new InvalidOperationException("the message list generated no container");
                return 0;
            });
            WpfStaHost.Pump();
            return listener.Lines;
        }
        finally
        {
            source.Listeners.Remove(listener);
            source.Switch.Level = originalLevel;
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }
    }

    private static ImageAttachment NewAttachment()
    {
        var thumb = System.Windows.Media.Imaging.BitmapSource.Create(
            1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
        return new ImageAttachment
        {
            JpegBytes = [1, 2, 3, 4],
            MimeType = "image/jpeg",
            Width = 1,
            Height = 1,
            Thumbnail = thumb,
        };
    }
}
