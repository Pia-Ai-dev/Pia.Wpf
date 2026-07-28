using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// B1: the destructive-name heuristic shared by the card builder, the interactive gate and the unattended
/// grant gate. Covers the broadened stem set (a "delete" substring alone let remove_/purge_/drop_/wipe_/
/// erase_/destroy_/truncate_ MCP tools be granted as a class and then auto-execute forever) and the
/// create-time "presumed external" split that keeps our own delete tools grantable.
/// </summary>
public class ToolPermissionServiceDeleteLikeTests
{
    [Theory]
    [InlineData("delete_file")]
    [InlineData("delete_issue")]
    [InlineData("remove_page")]
    [InlineData("purge_records")]
    [InlineData("drop_table")]
    [InlineData("wipe_index")]
    [InlineData("erase_document")]
    [InlineData("destroy_environment")]
    [InlineData("truncate_table")]
    [InlineData("forget")]
    [InlineData("FORGET")]                 // literal match is case-insensitive
    [InlineData("Notion_DeletePage")]       // stems match anywhere, in any casing
    [InlineData("linear.issue.remove")]
    public void IsDeleteLike_TrueForEveryDestructiveStem(string toolName)
    {
        Assert.True(ToolPermissionService.IsDeleteLike(toolName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("write_file")]      // overwrite-class, deliberately NOT delete-like (design §3/§5)
    [InlineData("create_object")]
    [InlineData("update_todo")]
    [InlineData("complete_todo")]
    [InlineData("search_files")]
    [InlineData("read_file")]
    [InlineData("recall")]
    [InlineData("remember")]        // must not trip on "remove"/"erase"
    [InlineData("move_todo")]
    [InlineData("git_restore")]     // destructive, but by its own rule in the card builder — not delete-like
    [InlineData("create_issue")]
    public void IsDeleteLike_FalseForEverythingElse(string? toolName)
    {
        Assert.False(ToolPermissionService.IsDeleteLike(toolName));
    }

    [Theory]
    [InlineData("delete_file")]
    [InlineData("delete_todo")]
    [InlineData("delete_reminder")]
    [InlineData("delete_scheduled_research")]
    [InlineData("forget")]
    [InlineData("DELETE_FILE")]
    public void IsPresumedExternalDeleteLike_FalseForOurOwnDestructiveTools(string toolName)
    {
        // Granting a built-in delete stays possible: it is the user's own auditable decision and the
        // execution gate can (and does) re-derive real MCP-ness for it.
        Assert.False(ToolPermissionService.IsPresumedExternalDeleteLike(toolName));
    }

    [Theory]
    [InlineData("delete_issue")]
    [InlineData("remove_page")]
    [InlineData("purge_records")]
    [InlineData("drop_table")]
    public void IsPresumedExternalDeleteLike_TrueForDestructiveNamesWeDoNotShip(string toolName)
    {
        Assert.True(ToolPermissionService.IsPresumedExternalDeleteLike(toolName));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("create_issue")]
    [InlineData(null)]
    public void IsPresumedExternalDeleteLike_FalseForNonDestructiveNames(string? toolName)
    {
        Assert.False(ToolPermissionService.IsPresumedExternalDeleteLike(toolName));
    }
}
