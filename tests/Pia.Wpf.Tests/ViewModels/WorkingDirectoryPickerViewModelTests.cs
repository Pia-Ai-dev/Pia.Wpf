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
}
