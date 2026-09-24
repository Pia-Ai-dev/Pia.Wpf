using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Measurement probe for what one navigation costs to rebuild the view, not an assertion;
/// Explicit, writes probe-render.log to PIA_PERF_DIR.</summary>
[Collection("WpfApplicationStatic")]
public class AssistantViewPerfProbe
{
    private static string Dir => Environment.GetEnvironmentVariable("PIA_PERF_DIR")
        ?? throw new InvalidOperationException("PIA_PERF_DIR not set");

    private static void Log(string line)
    {
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(Dir, "probe-render.log"), line + Environment.NewLine);
    }

    [Fact(Explicit = true)]
    public void MeasureNavigationRebuild()
    {
        Log($"--- navigation rebuild, 900x700, {DateTime.Now:HH:mm:ss}");

        // A warm-up chat first: the very first AssistantView in the process pays for resource
        // dictionaries and static ctors, which is not what a navigation costs.
        MeasureOne(0, rounds: 1, label: null);

        foreach (var count in new[] { 0, 10, 50, 200 })
            MeasureOne(count, rounds: 4, label: $"{count} messages");
    }

    private static void MeasureOne(int messageCount, int rounds, string? label)
    {
        AssistantViewModel? vm = null;
        ContentPresenter? host = null;
        Window? window = null;
        var build = new List<double>();
        var pump = new List<double>();

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                vm.Messages = Transcript(messageCount);
                vm.HasMessages = messageCount > 0;

                // A real ContentPresenter swap inside a real Window, so WPF's Loaded/Unloaded
                // broadcast runs exactly as it does behind NavigationContentPresenter.
                window = new Window { Width = 900, Height = 700, ShowInTaskbar = false };
                host = new ContentPresenter();
                window.Content = host;
                window.Show();
                return 0;
            });
            WpfStaHost.Pump();

            for (var round = 0; round < rounds; round++)
            {
                var sw = Stopwatch.StartNew();
                WpfStaHost.Run(() =>
                {
                    host!.Content = null;
                    host.Content = new AssistantView { DataContext = vm };
                    window!.UpdateLayout();
                    return 0;
                });
                sw.Stop();
                build.Add(sw.Elapsed.TotalMilliseconds);

                // Loaded's work is posted, so a navigation is not finished until the queue drains.
                var pumpSw = Stopwatch.StartNew();
                WpfStaHost.Pump();
                pumpSw.Stop();
                pump.Add(pumpSw.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                window?.Close();
                vm?.Dispose();
                return 0;
            });
        }

        if (label is null) return;

        var heap = GC.GetTotalMemory(forceFullCollection: true) / 1048576.0;
        Log($"{label}: build={string.Join(" / ", build.Select(v => v.ToString("F0") + "ms"))} " +
            $"| pump={string.Join(" / ", pump.Select(v => v.ToString("F0") + "ms"))} " +
            $"| heap after {heap:F0} MB");
    }

    [Fact(Explicit = true)]
    public void MeasureRealTranscript()
    {
        var path = Path.Combine(Dir, "biggest-chat.json");
        if (!File.Exists(path))
        {
            Log($"no biggest-chat.json in {Dir}, skipping");
            return;
        }

        var messages = System.Text.Json.JsonSerializer.Deserialize<List<RawMessage>>(File.ReadAllText(path))!;
        Log($"--- real export transcript: {messages.Count} messages, " +
            $"{messages.Sum(m => m.content.Length)} chars total");

        AssistantViewModel? vm = null;
        ContentPresenter? host = null;
        Window? window = null;
        var build = new List<double>();

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                vm.Messages = [.. messages.Select(m => new AssistantMessage(
                    m.role == "user" ? ChatRole.User : ChatRole.Assistant, m.content))];
                vm.HasMessages = true;
                window = new Window { Width = 900, Height = 700, ShowInTaskbar = false };
                host = new ContentPresenter();
                window.Content = host;
                window.Show();
                return 0;
            });
            WpfStaHost.Pump();

            for (var round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                WpfStaHost.Run(() =>
                {
                    host!.Content = null;
                    host.Content = new AssistantView { DataContext = vm };
                    window!.UpdateLayout();
                    return 0;
                });
                WpfStaHost.Pump();
                sw.Stop();
                build.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                window?.Close();
                vm?.Dispose();
                return 0;
            });
        }

        Log($"real transcript: {string.Join(" / ", build.Select(v => v.ToString("F0") + "ms"))}");
    }

    private sealed record RawMessage(string role, string content);


    // A 1.3 MB OpenWebUI chat can be many small turns or a few pasted documents, and the customer's
    // file was never seen — so bracket both shapes at the same total text.
    [Fact(Explicit = true)]
    public void MeasureLargeChatShapes()
    {
        Log($"--- large-chat shapes, 900x700, {DateTime.Now:HH:mm:ss}");
        foreach (var (count, chars) in new[]
                 {
                     (650, 1_000), (130, 5_000), (20, 32_500),
                     (1_300, 1_000), (40, 32_500),
                 })
        {
            MeasureShape(count, chars);
        }
    }


    // The skewed archive: 10 chats hold 90 % of a 195 MB export, i.e. ~1 570 messages of ~5 KB each.
    // Guarded, because the unvirtualized list realizes every one of them.
    [Fact(Explicit = true)]
    public void MeasureSkewedHeavyChat()
    {
        Log($"--- skewed heavy chat, 900x700, {DateTime.Now:HH:mm:ss}");
        foreach (var (count, chars) in new[] { (400, 5_150), (800, 5_150), (1_573, 5_150) })
        {
            try
            {
                MeasureShape(count, chars);
            }
            catch (OutOfMemoryException)
            {
                Log($"{count} x {chars} chars: OutOfMemoryException - the transcript cannot be realized");
                return;
            }
        }
    }


    // Candidate window sizes for a "load older" transcript, at the export's real ~5 KB message body.
    [Fact(Explicit = true)]
    public void MeasureWindowSizes()
    {
        Log($"--- load-more window sizes at 5 150 chars, 900x700, {DateTime.Now:HH:mm:ss}");
        foreach (var count in new[] { 10, 15, 25, 40, 50, 75 })
            MeasureShape(count, 5_150);
    }

    private static void MeasureShape(int messageCount, int messageChars)
    {
        AssistantViewModel? vm = null;
        ContentPresenter? host = null;
        Window? window = null;
        var build = new List<double>();

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                vm.Messages = [.. Enumerable.Range(1, messageCount).Select(i => new AssistantMessage(
                    i % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
                    Body(i, messageChars)))];
                vm.HasMessages = true;
                window = new Window { Width = 900, Height = 700, ShowInTaskbar = false };
                host = new ContentPresenter();
                window.Content = host;
                window.Show();
                return 0;
            });
            WpfStaHost.Pump();

            for (var round = 0; round < 2; round++)
            {
                var sw = Stopwatch.StartNew();
                WpfStaHost.Run(() =>
                {
                    host!.Content = null;
                    host.Content = new AssistantView { DataContext = vm };
                    window!.UpdateLayout();
                    return 0;
                });
                WpfStaHost.Pump();
                sw.Stop();
                build.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                window?.Close();
                vm?.Dispose();
                return 0;
            });
        }

        var heap = GC.GetTotalMemory(forceFullCollection: true) / 1048576.0;
        Log($"{messageCount} x {messageChars} chars ({messageCount * (long)messageChars / 1024} KB): "
            + $"{string.Join(" / ", build.Select(v => v.ToString("F0") + "ms"))} | heap after {heap:F0} MB");
    }

    /// <summary>Paragraphs, not one run of x: the markdown renderer builds a block per paragraph.</summary>
    private static string Body(int index, int chars)
    {
        var builder = new System.Text.StringBuilder(chars + 64);
        builder.Append("Turn ").Append(index).Append(". ");
        var paragraph = string.Join(' ', Enumerable.Repeat("Lorem ipsum dolor sit amet consectetur.", 12));
        while (builder.Length < chars)
            builder.Append(paragraph).AppendLine().AppendLine();
        return builder.ToString(0, chars);
    }

    private static ObservableCollection<AssistantMessage> Transcript(int turns) =>
        [.. Enumerable.Range(1, turns).Select(i => new AssistantMessage(
            i % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
            $"turn {i} — a message about as long as the median one in the export, which is a "
            + "thousand characters. " + new string('x', 900)))];
}
