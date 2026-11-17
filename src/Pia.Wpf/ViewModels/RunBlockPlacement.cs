using System.Collections.ObjectModel;
using Pia.Models;

namespace Pia.ViewModels;

public readonly record struct RunBlockSlot(Guid MessageId, bool Before);

/// <summary>Where a run's block sits among the transcript's messages; the block itself is never a message.</summary>
internal static class RunBlockPlacement
{
    /// <summary>Null means the block has no anchor: the live run renders after the last message, a finished
    /// one is not shown.</summary>
    internal static RunBlockSlot? Place(
        Guid? firstMessageId, Guid? lastMessageId, DateTime? runCreatedAt, bool allowTimeFallback,
        IReadOnlyList<AssistantMessage> messages)
    {
        if (ProposingMessage(firstMessageId, runCreatedAt, messages) is { } proposing)
            return new RunBlockSlot(proposing.Id, Before: false);

        foreach (var id in new[] { firstMessageId, lastMessageId })
        {
            if (id is { } wrote && messages.Any(m => m.Id == wrote))
                return new RunBlockSlot(wrote, Before: true);
        }

        if (!allowTimeFallback || runCreatedAt is not { } createdAt)
            return null;

        var created = createdAt.ToUniversalTime();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Timestamp.ToUniversalTime() < created)
                return new RunBlockSlot(messages[i].Id, Before: false);
        }

        return messages.Count > 0 ? new RunBlockSlot(messages[0].Id, Before: true) : null;
    }

    // A job only writes messages after its run exists, so an older assistant anchor is the message that proposed it.
    private static AssistantMessage? ProposingMessage(
        Guid? firstMessageId, DateTime? runCreatedAt, IReadOnlyList<AssistantMessage> messages)
    {
        if (firstMessageId is not { } id || runCreatedAt is not { } createdAt)
            return null;
        var anchor = messages.FirstOrDefault(m => m.Id == id);
        return anchor is { Role: var role } && role == Microsoft.Extensions.AI.ChatRole.Assistant
            && anchor.Timestamp.ToUniversalTime() < createdAt.ToUniversalTime()
            ? anchor
            : null;
    }

    /// <summary>An unfinished block whose anchor is not loaded moves to the trailing list so its controls stay reachable.</summary>
    internal static (List<(object Block, RunBlockSlot? Slot)> Placed, List<object> Trailing) TrailUnloadedLive(
        IReadOnlyList<(object Block, RunBlockSlot? Slot)> placed, IReadOnlyList<object> trailing,
        IReadOnlySet<Guid> visibleIds, Func<object, bool> isLive)
    {
        var keep = new List<(object Block, RunBlockSlot? Slot)>(placed.Count);
        var moved = new List<object>();
        foreach (var entry in placed)
        {
            if (entry.Slot is { } slot && !visibleIds.Contains(slot.MessageId) && isLive(entry.Block))
                moved.Add(entry.Block);
            else
                keep.Add(entry);
        }
        moved.AddRange(trailing);
        return (keep, moved);
    }

    /// <summary>A block whose anchor is outside the visible window is left out until that message is loaded.</summary>
    internal static List<object> Interleave(
        IReadOnlyList<AssistantMessage> visible, IReadOnlyList<(object Block, RunBlockSlot? Slot)> blocks,
        IReadOnlyList<object> trailing, IReadOnlySet<Guid>? hidden = null)
    {
        var items = new List<object>(visible.Count + blocks.Count + trailing.Count);
        foreach (var message in visible)
        {
            foreach (var (block, slot) in blocks)
                if (slot is { Before: true } s && s.MessageId == message.Id) items.Add(block);
            // A hidden message still anchors its blocks.
            if (hidden is null || !hidden.Contains(message.Id))
                items.Add(message);
            foreach (var (block, slot) in blocks)
                if (slot is { Before: false } s && s.MessageId == message.Id) items.Add(block);
        }
        items.AddRange(trailing);
        return items;
    }

    /// <summary>Edits <paramref name="target"/> in place so unchanged items keep their realized containers.</summary>
    internal static void Apply(ObservableCollection<object> target, IReadOnlyList<object> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;

            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i])) { found = j; break; }
            }

            if (found < 0)
            {
                target.Insert(i, desired[i]);
                continue;
            }

            for (var k = found - 1; k >= i; k--)
                target.RemoveAt(k);
        }

        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }
}
