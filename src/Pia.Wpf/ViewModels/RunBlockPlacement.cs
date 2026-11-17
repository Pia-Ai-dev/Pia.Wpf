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

    /// <summary>A block whose anchor is outside the visible window is left out until that message is loaded.</summary>
    internal static List<object> Interleave(
        IReadOnlyList<AssistantMessage> visible, IReadOnlyList<(object Block, RunBlockSlot? Slot)> blocks,
        IReadOnlyList<object> trailing)
    {
        var items = new List<object>(visible.Count + blocks.Count + trailing.Count);
        foreach (var message in visible)
        {
            foreach (var (block, slot) in blocks)
                if (slot is { Before: true } s && s.MessageId == message.Id) items.Add(block);
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
