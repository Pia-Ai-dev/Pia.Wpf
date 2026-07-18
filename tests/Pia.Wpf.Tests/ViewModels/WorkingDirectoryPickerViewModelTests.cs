using NSubstitute;
using Pia.Services;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Covers <see cref="WorkingDirectoryPickerViewModel"/> drill-down/ascend navigation: Entry
/// listing per level, breadcrumb construction, the <c>WorkingDirectoryChosen</c> event on
/// selection, and that <c>InitializeFrom</c> (an external change) does NOT raise the event.
/// </summary>
public class WorkingDirectoryPickerViewModelTests
{
    private readonly IWorkingDirectoryService _service = Substitute.For<IWorkingDirectoryService>();

    private WorkingDirectoryPickerViewModel CreateSut()
    {
        // Default: root has "projects" and "docs"; "projects" has "app" and "lib"; deeper empty.
        _service.ListSubfolders("").Returns(new[] { "docs", "projects" });
        _service.ListSubfolders("projects").Returns(new[] { "app", "lib" });
        _service.ListSubfolders(Arg.Is<string>(p => p != "" && p != "projects")).Returns([]);
        return new WorkingDirectoryPickerViewModel(_service);
    }

    [Fact]
    public void Ctor_StartsAtRoot_WithRootCrumb_DefersEnumeration()
    {
        // The ctor must NOT enumerate (it would synchronously block ISettingsService on the
        // UI thread). Only the offline root crumb is seeded; Entries stay empty until opened.
        var sut = CreateSut();

        Assert.Equal(string.Empty, sut.CurrentRelativePath);
        Assert.Empty(sut.Entries);
        var crumb = Assert.Single(sut.Crumbs);
        Assert.True(crumb.IsRoot);
    }

    [Fact]
    public void Refresh_AtRoot_ListsRootChildren()
    {
        var sut = CreateSut();

        sut.Refresh();

        Assert.Equal(new[] { "docs", "projects" }, sut.Entries);
    }

    [Fact]
    public void Enter_DrillsDown_ListsThatLevelOnly()
    {
        var sut = CreateSut();

        sut.EnterCommand.Execute("projects");

        Assert.Equal("projects", sut.CurrentRelativePath);
        Assert.Equal(new[] { "app", "lib" }, sut.Entries);
    }

    [Fact]
    public void Enter_AppendsRelativeSegment_WithForwardSlash()
    {
        var sut = CreateSut();

        sut.EnterCommand.Execute("projects");
        sut.EnterCommand.Execute("app");

        Assert.Equal("projects/app", sut.CurrentRelativePath);
    }

    [Fact]
    public void Enter_RaisesWorkingDirectoryChosen_WithNewRelativePath()
    {
        var sut = CreateSut();
        string? chosen = null;
        sut.WorkingDirectoryChosen += (_, path) => chosen = path;

        sut.EnterCommand.Execute("projects");

        Assert.Equal("projects", chosen);
    }

    [Fact]
    public void Crumbs_ReflectCurrentPath_RootPlusSegments()
    {
        var sut = CreateSut();

        sut.EnterCommand.Execute("projects");
        sut.EnterCommand.Execute("app");

        // root, projects, app
        Assert.Equal(3, sut.Crumbs.Count);
        Assert.True(sut.Crumbs[0].IsRoot);
        Assert.Equal("projects", sut.Crumbs[1].Name);
        Assert.Equal(1, sut.Crumbs[1].Index);
        Assert.Equal("app", sut.Crumbs[2].Name);
        Assert.Equal(2, sut.Crumbs[2].Index);
    }

    [Fact]
    public void JumpToCrumb_Root_GoesToRoot_AndRaisesEvent()
    {
        var sut = CreateSut();
        sut.EnterCommand.Execute("projects");
        sut.EnterCommand.Execute("app");

        string? chosen = "sentinel";
        sut.WorkingDirectoryChosen += (_, path) => chosen = path;

        sut.JumpToCrumbCommand.Execute(0);

        Assert.Equal(string.Empty, sut.CurrentRelativePath);
        Assert.Equal(string.Empty, chosen);
        Assert.Equal(new[] { "docs", "projects" }, sut.Entries);
    }

    [Fact]
    public void JumpToCrumb_IntermediateSegment_TruncatesPath()
    {
        var sut = CreateSut();
        sut.EnterCommand.Execute("projects");
        sut.EnterCommand.Execute("app");

        sut.JumpToCrumbCommand.Execute(1); // jump to "projects"

        Assert.Equal("projects", sut.CurrentRelativePath);
        Assert.Equal(new[] { "app", "lib" }, sut.Entries);
    }

    [Fact]
    public void InitializeFrom_SetsPath_WithoutRaisingEvent()
    {
        var sut = CreateSut();
        var raised = false;
        sut.WorkingDirectoryChosen += (_, _) => raised = true;

        sut.InitializeFrom("projects");

        Assert.Equal("projects", sut.CurrentRelativePath);
        Assert.Equal(new[] { "app", "lib" }, sut.Entries);
        Assert.False(raised);
    }

    [Fact]
    public void InitializeFrom_NormalizesBackslashesAndSlashes()
    {
        var sut = CreateSut();

        sut.InitializeFrom("\\projects\\");

        Assert.Equal("projects", sut.CurrentRelativePath);
    }

    [Fact]
    public void Enter_EmptyFolderName_IsIgnored()
    {
        var sut = CreateSut();
        var raised = false;
        sut.WorkingDirectoryChosen += (_, _) => raised = true;

        sut.EnterCommand.Execute("");
        sut.EnterCommand.Execute("   ");

        Assert.Equal(string.Empty, sut.CurrentRelativePath);
        Assert.False(raised);
    }

    [Fact]
    public void IsEmpty_TrueWhenNoChildren()
    {
        var sut = CreateSut();

        sut.EnterCommand.Execute("projects");
        sut.EnterCommand.Execute("app"); // deeper => empty

        Assert.True(sut.IsEmpty);
        Assert.Empty(sut.Entries);
    }

    [Fact]
    public void BeginCreateFolder_ShowsInput_WithEmptyName()
    {
        var sut = CreateSut();

        sut.BeginCreateFolderCommand.Execute(null);

        Assert.True(sut.IsCreatingFolder);
        Assert.Equal(string.Empty, sut.NewFolderName);
    }

    [Fact]
    public void ConfirmCreateFolder_AtRoot_CreatesUnderRoot_RefreshesAndSelects()
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "Reports";
        _service.EnsureSubfolder("Reports").Returns("Reports");
        // The post-create refresh should now surface the new folder.
        _service.ListSubfolders("").Returns(new[] { "docs", "projects", "Reports" });

        sut.ConfirmCreateFolderCommand.Execute(null);

        _service.Received().EnsureSubfolder("Reports");
        Assert.False(sut.IsCreatingFolder);
        Assert.Equal("Reports", sut.LastCreatedFolder);
        Assert.Contains("Reports", sut.Entries);
    }

    [Fact]
    public void ConfirmCreateFolder_Nested_CreatesUnderCurrentPath()
    {
        var sut = CreateSut();
        sut.EnterCommand.Execute("projects");
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "Sub";
        _service.EnsureSubfolder("projects/Sub").Returns("projects/Sub");

        sut.ConfirmCreateFolderCommand.Execute(null);

        _service.Received().EnsureSubfolder("projects/Sub");
        Assert.Equal("Sub", sut.LastCreatedFolder);
        Assert.False(sut.IsCreatingFolder);
    }

    [Fact]
    public void ConfirmCreateFolder_TrimsName_BeforeCreating()
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "  Spaced  ";
        _service.EnsureSubfolder("Spaced").Returns("Spaced");

        sut.ConfirmCreateFolderCommand.Execute(null);

        _service.Received().EnsureSubfolder("Spaced");
        Assert.Equal("Spaced", sut.LastCreatedFolder);
    }

    [Fact]
    public void ConfirmCreateFolder_UsesOnDiskCasing_ForLastCreatedFolder()
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "reports"; // user types lowercase
        // The service echoes the typed (lexical) casing, but the folder already exists on disk as
        // "Reports"; the post-create refresh surfaces the on-disk name.
        _service.EnsureSubfolder("reports").Returns("reports");
        _service.ListSubfolders("").Returns(new[] { "docs", "projects", "Reports" });

        sut.ConfirmCreateFolderCommand.Execute(null);

        // LastCreatedFolder must match the on-disk entry so the view's ordinal select/scroll hits.
        Assert.Equal("Reports", sut.LastCreatedFolder);
        Assert.Contains("Reports", sut.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    public void ConfirmCreateFolder_InvalidName_CannotExecute_AndDoesNothing(string name)
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = name;

        Assert.False(sut.ConfirmCreateFolderCommand.CanExecute(null));

        // Even if invoked directly, the internal guard prevents any create + keeps the input open.
        sut.ConfirmCreateFolderCommand.Execute(null);
        _service.DidNotReceive().EnsureSubfolder(Arg.Any<string>());
        Assert.True(sut.IsCreatingFolder);
    }

    [Fact]
    public void ConfirmCreateFolder_ServiceRejects_StaysInCreateMode()
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "Blocked";
        _service.EnsureSubfolder("Blocked").Returns((string?)null);

        sut.ConfirmCreateFolderCommand.Execute(null);

        Assert.True(sut.IsCreatingFolder);
        Assert.Null(sut.LastCreatedFolder);
    }

    [Fact]
    public void ConfirmCreateFolder_DoesNotRepointWorkingDirectory()
    {
        var sut = CreateSut();
        var raised = false;
        sut.WorkingDirectoryChosen += (_, _) => raised = true;
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "New";
        _service.EnsureSubfolder("New").Returns("New");

        sut.ConfirmCreateFolderCommand.Execute(null);

        Assert.False(raised);
    }

    [Fact]
    public void CancelCreateFolder_ClearsInput()
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "temp";

        sut.CancelCreateFolderCommand.Execute(null);

        Assert.False(sut.IsCreatingFolder);
        Assert.Equal(string.Empty, sut.NewFolderName);
    }

    [Fact]
    public void Navigating_CancelsInProgressCreation()
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);
        sut.NewFolderName = "temp";

        sut.EnterCommand.Execute("projects");

        Assert.False(sut.IsCreatingFolder);
        Assert.Equal(string.Empty, sut.NewFolderName);
    }

    [Fact]
    public void ConfirmCreateFolder_CanExecute_TracksNameValidity()
    {
        var sut = CreateSut();
        sut.BeginCreateFolderCommand.Execute(null);

        Assert.False(sut.ConfirmCreateFolderCommand.CanExecute(null)); // empty
        sut.NewFolderName = "ok";
        Assert.True(sut.ConfirmCreateFolderCommand.CanExecute(null));
        sut.NewFolderName = "bad/name";
        Assert.False(sut.ConfirmCreateFolderCommand.CanExecute(null));
    }
}
