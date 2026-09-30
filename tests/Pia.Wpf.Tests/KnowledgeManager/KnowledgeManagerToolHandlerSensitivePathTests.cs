using System.IO;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Paths;
using Pia.Services.KnowledgeManager;
using Pia.Shared.Knowledge;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

/// <summary>A files folder inside a directory the guard protects must not feed a knowledge base or receive a download.</summary>
[Collection("PiaPathsStatic")]
public sealed class KnowledgeManagerToolHandlerSensitivePathTests : KnowledgeManagerToolHandlerTestBase
{
    private static string NewProfile() =>
        Path.Combine(Path.GetTempPath(), $"pia-kbm-profile-{Guid.NewGuid():N}");

    [Fact]
    public async Task Upload_FromAFilesFolderInsideAProtectedDirectory_IsRefusedAndCallsNoApi()
    {
        var profile = NewProfile();
        var root = Directory.CreateDirectory(Path.Combine(profile, "notes")).FullName;
        File.WriteAllText(Path.Combine(root, "a.md"), "secret");
        Files.ResolveToolRoot().Returns(root);
        try
        {
            using (PiaPaths.OverrideForTests(null, profile))
            {
                Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(root, "a.md"), out _));

                var (result, pending) = await CreateSut().HandleToolCallAsync(
                    Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

                Assert.Null(pending);
                Assert.StartsWith("Refusing to use that path", (string)result!, StringComparison.Ordinal);
            }

            await Api.DidNotReceiveWithAnyArgs().ListKnowledgeBasesAsync(Ct);
            await Api.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, Ct);
        }
        finally
        {
            TempPath.Remove(profile);
        }
    }

    [Fact]
    public async Task Download_ToAFilesFolderInsideAProtectedDirectory_WritesNothing()
    {
        var profile = NewProfile();
        var root = Directory.CreateDirectory(Path.Combine(profile, "notes")).FullName;
        Files.ResolveToolRoot().Returns(root);
        Documents(Onboarding());
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerDocumentContent>(
            KbManagerCallStatus.Ok, new KbManagerDocumentContent("theirs", KbManagerLimits.Markdown)));
        try
        {
            using (PiaPaths.OverrideForTests(null, profile))
            {
                var (result, _) = await CreateSut().HandleToolCallAsync(
                    Call("download_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

                Assert.StartsWith("Refusing to use that path", (string)result!, StringComparison.Ordinal);
            }

            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            TempPath.Remove(profile);
        }
    }
}
