using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Web chips carry the number the citation extractor stamped into the answer text, so they must keep it and
/// their relative order; the vault/chat chips collected during the turn are unnumbered and sort ahead.
/// </summary>
public class AssistantMessageSourcesTests
{
    private static SourceRef Vault(string target, string label = "page") =>
        new(0, label, string.Empty, Kind: SourceRefKind.VaultPage, Target: target);

    private static SourceRef Chat(Guid id, string title = "Past chat") =>
        new(0, title, "2026-08-19", Kind: SourceRefKind.Chat, Target: id.ToString());

    private static SourceRef Web(int number, string url) =>
        new(number, "example.com", "anchor", url);

    [Fact]
    public void AddSource_FlipsHasSources()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        var raised = new List<string>();
        msg.PropertyChanged += (_, e) => { if (e.PropertyName is { } n) raised.Add(n); };

        Assert.False(msg.HasSources);
        msg.AddSource(Vault("topics/coffee"));

        Assert.True(msg.HasSources);
        Assert.Contains(nameof(AssistantMessage.HasSources), raised);
    }

    [Fact]
    public void AddSource_DedupesByTarget_CaseInsensitive()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        msg.AddSource(Vault("topics/coffee", "first"));
        msg.AddSource(Vault("Topics/Coffee", "second"));

        Assert.Single(msg.Sources);
        Assert.Equal("first", msg.Sources[0].Source);
    }

    [Fact]
    public void AddSource_DedupesWebByUrl_AndKeepsItsNumber()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        msg.AddSource(Web(1, "https://example.com/a"));
        msg.AddSource(Web(2, "https://example.com/a"));

        Assert.Single(msg.Sources);
        Assert.Equal(1, msg.Sources[0].Number);
    }

    [Fact]
    public void AddSource_TargetsOfDifferentKinds_DoNotCollide()
    {
        var id = Guid.NewGuid();
        var msg = new AssistantMessage(ChatRole.Assistant);
        msg.AddSource(Vault(id.ToString()));
        msg.AddSource(Chat(id));

        Assert.Equal(2, msg.Sources.Count);
    }

    [Fact]
    public void AddSource_PutsUnnumberedChipsAheadOfWebCitations()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);

        // The real order of events: tools cite during the turn, ApplyWebCitations runs at the end.
        msg.AddSource(Vault("topics/coffee"));
        msg.AddSource(Web(1, "https://example.com/a"));
        msg.AddSource(Web(2, "https://example.com/b"));

        Assert.Equal(
            [SourceRefKind.VaultPage, SourceRefKind.Web, SourceRefKind.Web],
            msg.Sources.Select(s => s.Kind).ToArray());
        Assert.Equal([0, 1, 2], msg.Sources.Select(s => s.Number).ToArray());
    }

    [Fact]
    public void AddSource_LateVaultChip_StillLandsBeforeTheWebBlock()
    {
        var msg = new AssistantMessage(ChatRole.Assistant);
        msg.AddSource(Web(1, "https://example.com/a"));
        msg.AddSource(Vault("topics/coffee"));

        Assert.Equal(
            [SourceRefKind.VaultPage, SourceRefKind.Web],
            msg.Sources.Select(s => s.Kind).ToArray());
    }
}
