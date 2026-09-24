using Microsoft.Extensions.AI;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// The message shape a parked tool image rides in, and the swap that keeps it from being re-sent.
/// </summary>
internal static class ToolLoopImageMessages
{
    internal static ChatMessage Build(ToolLoopImage image)
    {
        // Text and image FUSED into one message: the compactor's image pin was measured on that shape and
        // withholds a whole turn, so a bare DataContent message would lose its caption when it is re-attached.
        return new ChatMessage(
            ChatRole.User,
            [new TextContent(image.Caption), new DataContent(image.Bytes, image.MediaType)])
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ToolLoopImageChannel.MessageTagKey] =
                    new ToolLoopImageTag(image.CallId, image.Width, image.Height, image.Source),
            },
        };
    }

    internal static bool IsTagged(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ToolLoopImageChannel.MessageTagKey) == true;

    internal static bool CarriesImage(ChatMessage message) =>
        message.Contents.OfType<DataContent>().Any(d => d.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Swaps every tagged message that still carries its picture for a placeholder, found by tag and
    /// never by index or text: compaction reassigns the list and re-orders a pinned image.</summary>
    internal static int Consume(List<ChatMessage> messages)
    {
        var consumed = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!IsTagged(message) || !CarriesImage(message)) continue;

            var tag = message.AdditionalProperties?[ToolLoopImageChannel.MessageTagKey] as ToolLoopImageTag;
            messages[i] = new ChatMessage(
                ChatRole.User,
                Placeholder(tag?.Source ?? ToolLoopImageSource.ScreenCapture, tag?.Width ?? 0, tag?.Height ?? 0))
            {
                AdditionalProperties = message.AdditionalProperties,
            };
            consumed++;
        }

        return consumed;
    }

    internal static string Placeholder(ToolLoopImageSource source, int width, int height) =>
        $"[{(source == ToolLoopImageSource.ImageFile ? "image file" : "screen capture")}, {width}x{height}, consumed]";

    internal static int CountImageMessages(IReadOnlyList<ChatMessage> messages)
    {
        var count = 0;
        for (var i = 0; i < messages.Count; i++)
            if (CarriesImage(messages[i])) count++;
        return count;
    }
}
