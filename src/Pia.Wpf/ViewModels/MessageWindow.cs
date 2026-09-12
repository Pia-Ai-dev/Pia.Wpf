using System.Collections.ObjectModel;
using Pia.Models;

namespace Pia.ViewModels;

/// <summary>Building the WPF tree over a transcript costs ~24 ms per message, so a message list renders
/// its newest slice and the reader asks for the rest.</summary>
internal static class MessageWindow
{
    internal const int Size = 50;

    internal static void Reset(
        ObservableCollection<AssistantMessage> window, IList<AssistantMessage> transcript)
    {
        window.Clear();
        for (var i = Math.Max(0, transcript.Count - Size); i < transcript.Count; i++)
            window.Add(transcript[i]);
    }

    internal static void TopUp(
        ObservableCollection<AssistantMessage> window, IList<AssistantMessage> transcript)
    {
        var missing = Math.Min(Size, transcript.Count) - window.Count;
        if (missing <= 0)
            return;

        var first = transcript.Count - window.Count - missing;
        for (var i = 0; i < missing; i++)
            window.Insert(i, transcript[first + i]);
    }

    internal static void PrependOlder(
        ObservableCollection<AssistantMessage> window, IList<AssistantMessage> transcript)
    {
        var older = transcript.Count - window.Count;
        if (older <= 0)
            return;

        var batch = Math.Min(Size, older);
        for (var i = 0; i < batch; i++)
            window.Insert(i, transcript[older - batch + i]);
    }
}
