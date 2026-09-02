using System.Reflection;
using System.Windows;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Reflecting an overlay's root off the hosting ViewModel's property catches a RENAME and a RETYPE but not a
/// RE-HOST; the last fact closes that by reading the declared <c>DataContext</c> path off a parsed <c>AssistantView</c>.</summary>
[Collection("WpfApplicationStatic")]
public class AssistantHostedOverlayParseTests
{
    /// <summary>Floors, not counts: measured 9 and 20 on 2026-08-02; the attendee overlay measured 28 on
    /// 2026-09-02, once the invite-drop bindings landed.</summary>
    private const int MinimumVoiceModePaths = 6;
    private const int MinimumMeetingAttendeePaths = 18;

    /// <summary>Floor, not a count: <see cref="BindingPathWalker"/> walks only the logical tree, so DataTemplate bindings do not count toward it.</summary>
    private const int MinimumDirectTranscriptionPaths = 15;

    /// <summary>A <c>?</c> annotation needs no unwrapping: it is metadata, and <c>PropertyType</c> is the reference type itself.</summary>
    private static Type RootOf(string property) =>
        typeof(AssistantViewModel).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;

    [Fact]
    public void VoiceModeOverlay_EveryBindingPath_ResolvesOnVoiceModeViewModel()
    {
        var root = RootOf(nameof(AssistantViewModel.VoiceMode));
        Assert.Equal(typeof(VoiceModeViewModel), root);

        AssertWalks(() => new Pia.Views.VoiceModeOverlay(), root, MinimumVoiceModePaths);
    }

    [Fact]
    public void MeetingAttendeeOverlay_EveryBindingPath_ResolvesOnMeetingAttendeeViewModel()
    {
        var root = RootOf(nameof(AssistantViewModel.MeetingAttendee));
        Assert.Equal(typeof(MeetingAttendeeViewModel), root);

        AssertWalks(() => new Pia.Views.MeetingAttendeeOverlay(), root, MinimumMeetingAttendeePaths);
    }

    [Fact]
    public void DirectTranscriptionOverlay_EveryBindingPath_ResolvesOnDirectTranscriptionViewModel()
    {
        var root = RootOf(nameof(AssistantViewModel.DirectTranscription));
        Assert.Equal(typeof(DirectTranscriptionViewModel), root);

        AssertWalks(() => new Pia.Views.DirectTranscriptionOverlay(), root, MinimumDirectTranscriptionPaths);
    }

    [Fact]
    public void AllOverlays_AreHostedOnTheDataContextPathTheirWalksReflectTheirRootsOff()
    {
        // Repoint AssistantView.xaml's {Binding VoiceMode} at {Binding MeetingAttendee} and every path in
        // VoiceModeOverlay is dead at runtime while the facts above stay green: neither opens the host markup.
        var observed = WpfStaHost.Run(() =>
        {
            var assistant = new Pia.Views.AssistantView();
            return BindingPathWalker.FindLogical<FrameworkElement>(assistant)
                .Where(e => e is Pia.Views.VoiceModeOverlay or Pia.Views.MeetingAttendeeOverlay
                    or Pia.Views.DirectTranscriptionOverlay)
                .Select(e => $"{e.GetType().Name}=" +
                    (BindingPathWalker.BoundPath(e, FrameworkElement.DataContextProperty)
                        ?? "<no DataContext binding>"))
                .ToArray();
        });

        // NON-VACUITY first: a per-site check over an empty walk passes over nothing, and the logical walk is
        // exactly the thing that could stop reaching them.
        Assert.Equal(3, observed.Length);
        Assert.Contains($"{nameof(Pia.Views.VoiceModeOverlay)}={nameof(AssistantViewModel.VoiceMode)}", observed);
        Assert.Contains(
            $"{nameof(Pia.Views.MeetingAttendeeOverlay)}={nameof(AssistantViewModel.MeetingAttendee)}", observed);
        Assert.Contains(
            $"{nameof(Pia.Views.DirectTranscriptionOverlay)}={nameof(AssistantViewModel.DirectTranscription)}",
            observed);
    }

    private static void AssertWalks(Func<DependencyObject> construct, Type root, int floor)
    {
        var bindings = WpfStaHost.Run(() => BindingPathWalker.Describe(construct(), root));

        Assert.True(bindings.Length >= floor,
            $"only {bindings.Length} bound paths were found in the parsed overlay rooted at {root.Name}, " +
            $"which is below the non-vacuity floor of {floor}. The walk is LOGICAL, so suspect a container " +
            "that no longer reports logical children rather than a genuine removal.");

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            $"these Binding paths do not resolve to a public property on {root.Name}, so they bind to nothing " +
            $"and fail silently at runtime: {string.Join(", ", unresolved)}");
    }
}
