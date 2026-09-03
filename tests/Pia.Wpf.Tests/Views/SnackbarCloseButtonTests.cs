using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Pia replaces the whole Snackbar template (Resources/Styles/Snackbar.xaml), so its close button is
/// only as good as that template's own wiring — nothing upstream covers it.
/// </summary>
public sealed class SnackbarCloseButtonTests
{
    [Fact]
    public void TheCloseButtonHidesTheSnackbar()
    {
        var shownThenHidden = WpfStaHost.Run(() =>
        {
            var presenter = new SnackbarPresenter();
            var snackbar = new Snackbar(presenter)
            {
                Title = "title",
                Content = "body",
                Timeout = TimeSpan.FromMinutes(10),
            };

            snackbar.Show();
            var wasShown = snackbar.IsShown;

            snackbar.TemplateButtonCommand.Execute(null);
            return wasShown && !snackbar.IsShown;
        });

        Assert.True(shownThenHidden);
    }

    /// <summary>
    /// FlowView's collapse scrim is a full-window transparent Border, so anything the user must be
    /// able to click has to sit above it — a snackbar that outranks it is dismissable while the
    /// flow rail is open.
    /// </summary>
    [Fact]
    public void TheSnackbarPresenterOutranksTheFlowScrim()
    {
        var xaml = File.ReadAllText(RepoPath("src/Pia.Wpf/MainWindow.xaml"));
        Assert.True(ZIndexOf(xaml, "RootSnackbarPresenter") > ZIndexOf(xaml, "RootFlowView"));
    }

    private static int ZIndexOf(string xaml, string name)
    {
        var at = xaml.IndexOf("x:Name=\"" + name + "\"", StringComparison.Ordinal);
        Assert.True(at >= 0, name + " is gone from MainWindow.xaml");

        var match = Regex.Match(xaml[at..], @"Panel\.ZIndex=""(\d+)""");
        Assert.True(match.Success, name + " has no Panel.ZIndex");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, relative);
    }
}
