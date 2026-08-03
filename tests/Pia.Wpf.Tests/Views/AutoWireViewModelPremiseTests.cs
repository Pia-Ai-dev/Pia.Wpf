using Pia.Navigation;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The premise every view-parse fact in this folder silently depends on:
/// <c>nav:ViewModelLocator.AutoWireViewModel="True"</c> — carried by eight views — assigns NO
/// <c>DataContext</c> under the test host. If it ever did, these walks would be examining a tree with a
/// ViewModel nobody intended, and a re-root that only exists in the test process would make every path
/// resolve for the wrong reason.
/// <para>
/// <b>When this fact was written, two candidate mechanisms produced that same null and it deliberately did not
/// claim which one was operating.</b> (i) Resolution defers to a <c>Loaded</c> that never fires here.
/// (ii) <c>GetViewModelType</c>'s second <c>Replace</c> hit EVERY occurrence, so
/// <c>Pia.Views.HistoryView</c> came out as <c>Pia.ViewModelModels.HistoryViewModel</c> — a namespace that
/// does not exist — making the attached property inert for every view whatever the provider did.
/// <b>Both are now settled.</b> (ii) was real, and is FIXED: the convention resolves, and the resolution is
/// deferred to <c>Loaded</c> and skipped when a <c>DataContext</c> is already there, so the eight views keep
/// the one their <c>App.xaml</c> <c>DataTemplate</c> supplies. <see cref="ViewModelLocatorAutoWireTests"/>
/// measured every step of that, and is the file to read for the mapping and the guard.
/// </para>
/// <para>
/// Which leaves (i) as the mechanism behind THIS null, and that is exactly what this fact is still for: no
/// view-parse test in this folder loads its view, so no auto-wired <c>DataContext</c> can arrive during a
/// walk. It no longer depends on the static provider being null either — the deferral holds whatever
/// <c>Initialize</c> was handed, which is what lets the sibling file install a probe provider and restore it.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class AutoWireViewModelPremiseTests
{
    [Fact]
    public void SettingAutoWireViewModel_LeavesTheDataContextNull_UnderTheTestHost()
    {
        // Set on a CONSTRUCTED view, which is what the markup does — the attached property's change callback
        // is the whole mechanism, so setting it in code exercises the same path App.xaml's attribute does.
        var dataContext = WpfStaHost.Run(() =>
        {
            var view = new Pia.Views.HistoryView();
            ViewModelLocator.SetAutoWireViewModel(view, true);
            return view.DataContext?.GetType().FullName;
        });

        Assert.Null(dataContext);
    }
}
