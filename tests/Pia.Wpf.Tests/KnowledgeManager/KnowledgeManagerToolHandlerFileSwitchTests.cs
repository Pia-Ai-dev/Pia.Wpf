using System.IO;
using NSubstitute;
using Pia.Shared.Knowledge;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public sealed class KnowledgeManagerToolHandlerFileSwitchTests : KnowledgeManagerToolHandlerTestBase
{
    private const string Sentence =
        "File access is switched off for the assistant, so no file was read or written. "
        + "The user can switch it on under Settings → Assistant.";

    public KnowledgeManagerToolHandlerFileSwitchTests() => Files.IsAvailable.Returns(false);

    private void AssertNoApiCall()
    {
        Api.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, Ct);
        Api.DidNotReceiveWithAnyArgs().UpdateContentAsync(default, default, default!, Ct);
        Api.DidNotReceiveWithAnyArgs().GetContentAsync(default, default, Ct);
    }

    [Fact]
    public async Task Upload_WithFileToolsOff_RefusesBeforeReadingTheFile()
    {
        await File.WriteAllTextAsync(Path.Combine(Root, "a.md"), "x", Ct);

        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

        Assert.Null(pending);
        Assert.Equal(Sentence, result);
        Files.DidNotReceive().ResolveToolRoot();
        AssertNoApiCall();
    }

    [Fact]
    public async Task UpdateFromAPath_WithFileToolsOff_RefusesBeforeReadingTheFile()
    {
        await File.WriteAllTextAsync(Path.Combine(Root, "a.md"), "x", Ct);

        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("update_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("path", "a.md")), Ct);

        Assert.Null(pending);
        Assert.Equal(Sentence, result);
        Files.DidNotReceive().ResolveToolRoot();
        AssertNoApiCall();
    }

    [Fact]
    public async Task Download_WithFileToolsOff_RefusesAndWritesNothing()
    {
        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("download_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Null(pending);
        Assert.Equal(Sentence, result);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
        Files.DidNotReceive().ResolveToolRoot();
        AssertNoApiCall();
    }

    [Fact]
    public async Task UpdateWithInlineContent_WithFileToolsOff_StillProposesTheChange()
    {
        KnowledgeBases(Handbook());
        Documents(Onboarding());

        var (_, pending) = await CreateSut().HandleToolCallAsync(
            Call("update_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("content", "New text")), Ct);

        Assert.NotNull(pending);
    }
}
