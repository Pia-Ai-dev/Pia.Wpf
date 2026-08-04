using System.Reflection;
using System.Windows;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The two overlays <c>AssistantView.xaml</c> hosts on a bound <c>DataContext</c> — <c>VoiceModeOverlay</c>
/// (<c>:574</c>, <c>{Binding VoiceMode}</c>) and <c>MeetingAttendeeOverlay</c> (<c>:582</c>,
/// <c>{Binding MeetingAttendee}</c>). Neither had ever been parsed.
/// <para>
/// These are the ordinary Batch 14 shape — a root reflected off the hosting ViewModel's property — so they
/// carry the hole D1 named: reflection sees a RENAME (<c>nameof</c> stops compiling) and a RETYPE (the
/// <c>Assert.Equal</c>), but not a RE-HOST. The second fact below closes it the way
/// <see cref="ViewHostDataContextTests"/> does, by reading the declared <c>DataContext</c> path out of a
/// parsed <c>AssistantView</c> — the same tree that file already builds for the run panel.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class AssistantHostedOverlayParseTests
{
    /// <summary>Floors, not counts: measured 9 and 20 on 2026-08-02.</summary>
    private const int MinimumVoiceModePaths = 6;
    private const int MinimumMeetingAttendeePaths = 13;

    /// <summary>
    /// Floor, not a count: this project cannot execute a markup-compile pass (macOS), so this is a manual
    /// lower bound counted from the non-templated bindings in DirectTranscriptionOverlay.xaml (header,
    /// disclaimer panel, footer) — deliberately conservative since <see cref="BindingPathWalker"/> only
    /// walks the logical tree, not DataTemplate content (the consent-chip and bubble templates' bindings
    /// do not count toward this floor).
    /// </summary>
    private const int MinimumDirectTranscriptionPaths = 15;

    /// <summary>
    /// The root type for an overlay, read off the hosting ViewModel's property. <c>VoiceMode</c> is declared
    /// <c>VoiceModeViewModel?</c>; there is nothing to unwrap, because the annotation is metadata and
    /// <c>PropertyType</c> is the reference type itself.
    /// </summary>
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
        // Batch 14 review D1, applied to two more sites: repoint AssistantView.xaml:574 from
        // {Binding VoiceMode} to {Binding MeetingAttendee} and every path in VoiceModeOverlay is dead at
        // runtime while both facts above stay green, because neither of them opens the host markup.
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
