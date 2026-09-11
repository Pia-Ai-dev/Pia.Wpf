using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Controls;
using Pia.Models;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Controls;

/// <summary>
/// A transcript renders one MarkdownMessageControl per message, so an inline context menu costs eight MenuItems
/// per message. One shared menu has to act on the message it was opened over, which only PlacementTarget knows.
/// </summary>
[Collection("WpfApplicationStatic")]
public class MarkdownMessageContextMenuTests
{
    private static readonly string[] DocumentedIds =
    [
        "PiiMenu_Open", "PiiMenu_Person", "PiiMenu_Nickname", "PiiMenu_Email",
        "PiiMenu_Phone", "PiiMenu_Address", "PiiMenu_Date", "PiiMenu_Custom",
    ];

    private readonly List<PiiKeywordRequest> _fromFirst = [];
    private readonly List<PiiKeywordRequest> _fromSecond = [];

    private MarkdownMessageControl _first = null!;
    private MarkdownMessageControl _second = null!;

    [Fact]
    public void TwoMessages_ShareOneContextMenu()
    {
        Assert.True(WpfStaHost.Run(() =>
        {
            Build();
            return ReferenceEquals(Menu(_first), Menu(_second));
        }));
    }

    [Fact]
    public void TheMenu_AddsToPiiOnTheMessageItWasOpenedOverLast()
    {
        WpfStaHost.Run(OpenOverFirstThenSecond);
        WpfStaHost.Pump();

        Assert.Equal("the second message", WpfStaHost.Run(AddToPii));
        Assert.Equal([new PiiKeywordRequest("second body", "Person")], _fromSecond);
        Assert.Empty(_fromFirst);
    }

    /// <summary>The menu outlives every message, so a PlacementTarget left set roots a whole discarded view.</summary>
    [Fact]
    public void ClosingTheMenu_ReleasesTheMessageItWasOpenedOver()
    {
        WpfStaHost.Run(OpenOverFirstThenClose);
        WpfStaHost.Pump();

        Assert.True(WpfStaHost.Run(() => Menu(_first).PlacementTarget is null));
    }

    /// <summary>ViewAutomationIdTests inspects no MenuItem, so these ids have no other guard.</summary>
    [Fact]
    public void TheSharedMenu_KeepsTheIdsTheUiScriptsAddress()
    {
        Assert.Equal(DocumentedIds, WpfStaHost.Run(() =>
        {
            Build();
            return Ids(Menu(_first)).ToArray();
        }));
    }

    [Fact]
    public void MarkdownAssignedAfterConstruction_StillRenders()
    {
        Assert.Equal("hello", WpfStaHost.Run(() =>
        {
            var viewer = Viewer(new MarkdownMessageControl { MarkdownText = "hello" });
            viewer.SelectAll();
            return viewer.Selection.Text.Trim();
        }));
    }

    private bool OpenOverFirstThenSecond()
    {
        Build();
        Viewer(_second).SelectAll();

        var menu = Menu(_second);
        menu.PlacementTarget = Viewer(_first);
        menu.PlacementTarget = Viewer(_second);
        return true;
    }

    private string AddToPii()
    {
        var menu = Menu(_second);
        var item = Item(menu, "PiiMenu_Person")
            ?? throw new InvalidOperationException("the menu no longer carries a PiiMenu_Person item");

        if (item.Command is not RoutedCommand command) return "no command at all";
        if (!ReferenceEquals(item.CommandTarget, Viewer(_second))) return Describe(item.CommandTarget);

        command.Execute(item.CommandParameter, item.CommandTarget);
        menu.PlacementTarget = null;
        return "the second message";
    }

    private bool OpenOverFirstThenClose()
    {
        Build();
        var menu = Menu(_first);
        menu.PlacementTarget = Viewer(_first);
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, menu));
        return true;
    }

    private void Build()
    {
        _first = Create("first body", _fromFirst);
        _second = Create("second body", _fromSecond);
    }

    private string Describe(IInputElement? target) => target switch
    {
        null => "no target",
        _ when ReferenceEquals(target, Viewer(_first)) => "the first message",
        _ => target.GetType().Name,
    };

    private static MarkdownMessageControl Create(string markdown, List<PiiKeywordRequest> sink)
    {
        var control = new MarkdownMessageControl { MarkdownText = markdown };
        control.AddToPiiRequested += (_, request) => sink.Add(request);
        control.Measure(new Size(400, 400));
        control.Arrange(new Rect(0, 0, 400, 400));
        control.UpdateLayout();
        return control;
    }

    private static RichTextBox Viewer(MarkdownMessageControl control) =>
        (RichTextBox)control.FindName("MarkdownViewer")!;

    private static ContextMenu Menu(MarkdownMessageControl control) => Viewer(control).ContextMenu!;

    private static MenuItem? Item(ItemsControl menu, string id) =>
        Items(menu).FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == id);

    private static string[] Ids(ItemsControl menu) =>
        [.. Items(menu).Select(AutomationProperties.GetAutomationId).Where(id => id.Length > 0)];

    private static IEnumerable<MenuItem> Items(ItemsControl menu) =>
        menu.Items.OfType<MenuItem>().SelectMany(item => new[] { item }.Concat(Items(item)));
}
