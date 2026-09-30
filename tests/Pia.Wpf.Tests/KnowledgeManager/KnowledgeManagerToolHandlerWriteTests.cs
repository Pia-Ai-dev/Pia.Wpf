using System.IO;
using NSubstitute;
using Pia.Services.KnowledgeManager;
using Pia.Shared.Knowledge;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public sealed class KnowledgeManagerToolHandlerWriteTests : KnowledgeManagerToolHandlerTestBase
{
    private void WriteFile(string relative, string text)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public async Task Upload_ProposesAPendingActionWithTheUnencryptedWarning()
    {
        KnowledgeBases(Handbook());
        WriteFile("notes/onboarding.md", "# Day one");

        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "notes/onboarding.md")), Ct);

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("upload_kb_document", pending.ToolName);
        Assert.Equal("Msg_KbManager_Summary_Upload(onboarding|Handbook)", pending.Description);
        Assert.Contains("Msg_KbManager_Detail_KnowledgeBase: Handbook", pending.Details, StringComparison.Ordinal);
        Assert.Equal("Msg_KbManager_UnencryptedWarning", pending.Warning);
        await Api.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Upload_OnASharedKb_AddsTheSharedWarning()
    {
        KnowledgeBases(Handbook(shared: true));
        WriteFile("a.md", "x");

        var (_, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

        Assert.Equal("Msg_KbManager_UnencryptedWarning\nMsg_KbManager_SharedWarning", pending!.Warning);
    }

    [Fact]
    public async Task Upload_CardNamesTheLocalFile_EvenWhenTheTitleSaysSomethingElse()
    {
        KnowledgeBases(Handbook());
        WriteFile("private/salary.md", "secret");

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("upload_kb_document",
            ("kb_id", Kb.ToString()), ("path", "private/salary.md"), ("title", "Onboarding guide")), Ct);

        Assert.Contains("Msg_KbManager_Detail_Document: Onboarding guide", pending!.Details, StringComparison.Ordinal);
        Assert.Contains("Msg_KbManager_Detail_File: private/salary.md", pending.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_FromAPath_CardNamesTheLocalFile()
    {
        KnowledgeBases(Handbook());
        Documents(Onboarding());
        WriteFile("drafts/diary.md", "private");

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("update_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("path", "drafts/diary.md")), Ct);

        Assert.Contains("Msg_KbManager_Detail_File: drafts/diary.md", pending!.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithInlineContent_HasNoFileRow()
    {
        KnowledgeBases(Handbook());
        Documents(Onboarding());

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("update_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("content", "New text")), Ct);

        Assert.DoesNotContain("Msg_KbManager_Detail_File", pending!.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_Execute_SendsTheFileAndReportsTheStatus()
    {
        KnowledgeBases(Handbook());
        WriteFile("a.md", "# Text");
        Api.UploadAsync(Kb, Arg.Any<KbManagerUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.Ok, new KbManagerWriteResult(Doc, "Pending")));

        var (_, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md"), ("title", "Custom")), Ct);
        var executed = await pending!.Execute();

        await Api.Received(1).UploadAsync(Kb,
            Arg.Is<KbManagerUploadRequest>(r => r.Title == "Custom" && r.Content == "# Text" && r.ContentType == KbManagerLimits.Markdown),
            Arg.Any<CancellationToken>());
        Assert.Equal("Pending", Json(executed).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("bin/notes.md")]
    [InlineData("report.docx")]
    public async Task Upload_PathOutsideTheSandbox_ProposesNothingAndCallsNoApi(string path)
    {
        WriteFile("bin/notes.md", "x");
        WriteFile("report.docx", "x");

        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", path)), Ct);

        Assert.Null(pending);
        Assert.IsType<string>(result);
        await Api.DidNotReceiveWithAnyArgs().ListKnowledgeBasesAsync(Ct);
        await Api.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Upload_WithoutAPath_AsksForOne()
    {
        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString())), Ct);

        Assert.Null(pending);
        Assert.Equal("path is required: a .txt or .md file inside the assistant files folder.", result);
    }

    [Fact]
    public async Task Upload_ToAKbTheUserDoesNotManage_IsRefused()
    {
        KnowledgeBases();
        WriteFile("a.md", "x");

        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

        Assert.Null(pending);
        Assert.Equal("That knowledge base is not one you can manage. Call list_knowledge_bases for the ids.", result);
    }

    [Fact]
    public async Task Upload_Execute_QuotaRefusal_IsOnePlainSentence()
    {
        KnowledgeBases(Handbook());
        WriteFile("a.md", "x");
        Api.UploadAsync(Kb, Arg.Any<KbManagerUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.Conflict, null,
                new KbManagerError(KbManagerErrorCodes.QuotaExceeded, null, "KnowledgeDocuments", 100, 101, null)));

        var (_, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

        Assert.Equal(
            "Quota exceeded: the document limit is 100, and this would make 101. Nothing was changed.",
            await pending!.Execute());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(100L, null)]
    [InlineData(null, 101L)]
    public async Task Upload_Execute_QuotaRefusalWithoutBothNumbers_IsOneSentenceWithoutQuestionMarks(
        long? limit, long? current)
    {
        KnowledgeBases(Handbook());
        WriteFile("a.md", "x");
        Api.UploadAsync(Kb, Arg.Any<KbManagerUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.Conflict, null,
                new KbManagerError(KbManagerErrorCodes.QuotaExceeded, null, "MonthlyEmbeddingTokens", limit, current, null)));

        var (_, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

        Assert.Equal(
            "Quota exceeded: this month's indexing-token limit was reached. Nothing was changed.",
            await pending!.Execute());
    }

    [Fact]
    public async Task Upload_Execute_TooLargeWithNoErrorBody_IsStillOnePlainSentence()
    {
        KnowledgeBases(Handbook());
        WriteFile("a.md", "x");
        Api.UploadAsync(Kb, Arg.Any<KbManagerUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.TooLarge));

        var (_, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

        Assert.Equal(
            "The server refused that content as too large (one document may hold at most 10 MB), so nothing was sent.",
            await pending!.Execute());
    }

    [Fact]
    public async Task Update_WithBothPathAndContent_IsRefused()
    {
        var (result, pending) = await CreateSut().HandleToolCallAsync(Call("update_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("path", "a.md"), ("content", "x")), Ct);

        Assert.Null(pending);
        Assert.StartsWith("Pass exactly one of path", (string)result!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithNeither_IsRefused()
    {
        var (result, pending) = await CreateSut().HandleToolCallAsync(Call("update_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Null(pending);
        Assert.StartsWith("Pass exactly one of path", (string)result!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_InlineContentOver32Kb_IsRefused()
    {
        var (result, pending) = await CreateSut().HandleToolCallAsync(Call("update_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()),
            ("content", new string('x', KbManagerLimits.MaxInlineContentBytes + 1))), Ct);

        Assert.Null(pending);
        Assert.StartsWith("Inline content is limited to 32 KB", (string)result!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_Inline_ProposesAndExecutes_AndSaysWhenNothingChanged()
    {
        KnowledgeBases(Handbook());
        Documents(Onboarding());
        Api.UpdateContentAsync(Kb, Doc, Arg.Any<KbManagerUpdateContentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.Unchanged, new KbManagerWriteResult(Doc, "Ready")));

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("update_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("content", "same text")), Ct);

        Assert.Equal("Msg_KbManager_Summary_Update(Onboarding|Handbook)", pending!.Description);
        Assert.Equal("The new content is identical to the stored document, so nothing changed.", await pending.Execute());
        await Api.Received(1).UpdateContentAsync(Kb, Doc,
            Arg.Is<KbManagerUpdateContentRequest>(r => r.Content == "same text" && r.ContentType == null && r.Title == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_FromAFile_SendsItsContentType()
    {
        KnowledgeBases(Handbook());
        Documents(Onboarding());
        WriteFile("new.txt", "plain");
        Api.UpdateContentAsync(Kb, Doc, Arg.Any<KbManagerUpdateContentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.Ok, new KbManagerWriteResult(Doc, "Pending")));

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("update_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("path", "new.txt")), Ct);
        await pending!.Execute();

        await Api.Received(1).UpdateContentAsync(Kb, Doc,
            Arg.Is<KbManagerUpdateContentRequest>(r => r.ContentType == KbManagerLimits.PlainText),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPrompt_OverTheLimit_IsRefused()
    {
        var (result, pending) = await CreateSut().HandleToolCallAsync(Call("set_kb_prompt",
            ("kb_id", Kb.ToString()), ("prompt", new string('p', KbManagerLimits.MaxPromptChars + 1))), Ct);

        Assert.Null(pending);
        Assert.Equal("The prompt may be at most 8,000 characters.", result);
    }

    [Fact]
    public async Task SetPrompt_AnEmptyString_IsAClear_NotAMissingArgument()
    {
        KnowledgeBases(Handbook());
        Api.SetPromptAsync(Kb, "", Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<bool>(KbManagerCallStatus.Ok, true));

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("set_kb_prompt",
            ("kb_id", Kb.ToString()), ("prompt", "")), Ct);
        await pending!.Execute();

        await Api.Received(1).SetPromptAsync(Kb, "", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPrompt_ALongPrompt_IsPreviewedWithoutSplittingASurrogatePair()
    {
        KnowledgeBases(Handbook());

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("set_kb_prompt",
            ("kb_id", Kb.ToString()), ("prompt", new string('a', 199) + "😀" + "tail")), Ct);

        var preview = PromptPreview(pending!.Details!);
        Assert.Equal(new string('a', 199) + "…", preview);
        AssertNoLoneSurrogate(preview);
    }

    [Fact]
    public async Task SetPrompt_ALongPrompt_KeepsAPairThatEndsExactlyAtTheCut()
    {
        KnowledgeBases(Handbook());

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("set_kb_prompt",
            ("kb_id", Kb.ToString()), ("prompt", new string('a', 198) + "😀" + "tail")), Ct);

        var preview = PromptPreview(pending!.Details!);
        Assert.Equal(new string('a', 198) + "😀" + "…", preview);
        AssertNoLoneSurrogate(preview);
    }

    private static string PromptPreview(string details)
    {
        const string label = "Msg_KbManager_Detail_Prompt: ";
        return details[(details.IndexOf(label, StringComparison.Ordinal) + label.Length)..];
    }

    private static void AssertNoLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
            if (char.IsLowSurrogate(text[i])) Assert.True(i > 0 && char.IsHighSurrogate(text[i - 1]));
        }
    }

    [Fact]
    public async Task SetPrompt_OnAnUnsharedKb_CarriesNoWarning()
    {
        KnowledgeBases(Handbook(shared: false));

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("set_kb_prompt",
            ("kb_id", Kb.ToString()), ("prompt", "Search for HR questions.")), Ct);

        Assert.Null(pending!.Warning);
        Assert.Contains("Msg_KbManager_Detail_Prompt: Search for HR questions.", pending.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_ProposesAPendingActionAndIsDestructiveByName()
    {
        KnowledgeBases(Handbook(shared: true));
        Documents(Onboarding());
        Api.DeleteAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<bool>(KbManagerCallStatus.Ok, true));

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("delete_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Equal("delete_kb_document", pending!.ToolName);
        Assert.True(Pia.Services.ToolPermissionService.IsDeleteLike(pending.ToolName));
        Assert.Equal("Msg_KbManager_SharedWarning", pending.Warning);
        Assert.Equal("The document was removed from the knowledge base.", await pending.Execute());
    }

    [Fact]
    public async Task Delete_OfADocumentThatIsNotThere_IsRefusedBeforeACard()
    {
        KnowledgeBases(Handbook());
        Documents();

        var (result, pending) = await CreateSut().HandleToolCallAsync(Call("delete_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Null(pending);
        Assert.Equal("That document is not in this knowledge base. Call list_kb_documents for the ids.", result);
    }

    [Fact]
    public async Task Upload_Execute_SendsTheBytesTheCardShowed_EvenIfTheFileChangedAfterwards()
    {
        KnowledgeBases(Handbook());
        WriteFile("a.md", "approved");
        Api.UploadAsync(Kb, Arg.Any<KbManagerUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.Ok, new KbManagerWriteResult(Doc, "Pending")));

        var (_, pending) = await CreateSut().HandleToolCallAsync(
            Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);
        WriteFile("a.md", "swapped after the card was shown");
        await pending!.Execute();

        await Api.Received(1).UploadAsync(Kb, Arg.Is<KbManagerUploadRequest>(r => r.Content == "approved"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("upload_kb_document")]
    [InlineData("set_kb_prompt")]
    [InlineData("delete_kb_document")]
    public async Task AnyWrite_ForbiddenWhenExecuted_HidesTheSurfaceAndSaysSoOnce(string tool)
    {
        KnowledgeBases(Handbook());
        Documents(Onboarding());
        WriteFile("a.md", "x");
        var forbidden = new KbManagerError(KbManagerErrorCodes.NotAKbManager, null, null, null, null, null);
        Api.UploadAsync(Arg.Any<Guid>(), Arg.Any<KbManagerUploadRequest>(), Arg.Any<CancellationToken>()).Returns(
            new KbManagerResult<KbManagerWriteResult>(KbManagerCallStatus.Forbidden, null, forbidden));
        Api.SetPromptAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new KbManagerResult<bool>(KbManagerCallStatus.Forbidden, false, forbidden));
        Api.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            new KbManagerResult<bool>(KbManagerCallStatus.Forbidden, false, forbidden));

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call(tool,
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("path", "a.md"), ("prompt", "p")), Ct);
        var executed = await pending!.Execute();

        Surface.Received(1).Hide();
        Assert.Equal("The server no longer lets you manage knowledge bases, so nothing was read or changed.", executed);
    }

    [Fact]
    public async Task AHostileTitle_CannotAddALineToTheCard()
    {
        KnowledgeBases(new KbManagerKnowledgeBase(Kb, "Handbook\nSize: 0 B", 1, false, false));
        Documents(Onboarding() with { Title = "Plan\r\nKnowledge base: Somewhere else" });

        var (_, pending) = await CreateSut().HandleToolCallAsync(Call("delete_kb_document",
            ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Equal(3, pending!.Details!.Split('\n').Length);
    }
}
