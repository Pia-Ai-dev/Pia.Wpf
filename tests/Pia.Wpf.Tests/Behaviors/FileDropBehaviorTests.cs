using System.Windows.Controls;
using Pia.Behaviors;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Behaviors;

/// <summary>
/// The nearest-target rule. The handlers themselves cannot be driven from a test — <c>DragEventArgs</c>
/// has no public constructor and UIA cannot synthesize an OLE transfer — so the predicate they all
/// consult is what gets locked here.
/// </summary>
public sealed class FileDropBehaviorTests
{
    [Fact]
    public void HasNearerTarget_IsTrue_WhenADescendantIsAlsoADropTarget()
    {
        Assert.True(WpfStaHost.Run(() =>
        {
            var (outer, inner, leaf) = BuildTree();
            FileDropBehavior.SetIsEnabled(outer, true);
            FileDropBehavior.SetIsEnabled(inner, true);
            return FileDropBehavior.HasNearerTarget(outer, leaf);
        }));
    }

    [Fact]
    public void HasNearerTarget_IsFalse_WhenTheDescendantIsNotADropTarget()
    {
        Assert.False(WpfStaHost.Run(() =>
        {
            var (outer, _, leaf) = BuildTree();
            FileDropBehavior.SetIsEnabled(outer, true);
            return FileDropBehavior.HasNearerTarget(outer, leaf);
        }));
    }

    [Fact]
    public void HasNearerTarget_IsFalse_WhenTheDragLandedOnTheTargetItself()
    {
        Assert.False(WpfStaHost.Run(() =>
        {
            var (outer, _, _) = BuildTree();
            FileDropBehavior.SetIsEnabled(outer, true);
            return FileDropBehavior.HasNearerTarget(outer, outer);
        }));
    }

    [Fact]
    public void HasNearerTarget_IsFalse_ForASourceOutsideTheTree()
    {
        // The walk runs off the top of an unrelated tree rather than looping or throwing.
        Assert.False(WpfStaHost.Run(() =>
        {
            var (outer, _, _) = BuildTree();
            FileDropBehavior.SetIsEnabled(outer, true);
            return FileDropBehavior.HasNearerTarget(outer, new Border());
        }));
    }

    [Fact]
    public void HasNearerTarget_IsFalse_ForNoSource()
    {
        Assert.False(WpfStaHost.Run(() => FileDropBehavior.HasNearerTarget(new Grid(), null)));
    }

    private static (Grid Outer, Grid Inner, Border Leaf) BuildTree()
    {
        var outer = new Grid();
        var inner = new Grid();
        var leaf = new Border();
        inner.Children.Add(leaf);
        outer.Children.Add(inner);
        return (outer, inner, leaf);
    }
}
