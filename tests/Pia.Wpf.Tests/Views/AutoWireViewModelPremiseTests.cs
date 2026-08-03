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
/// <b>Two candidate mechanisms produce that same null, and this fact deliberately does not claim which one is
/// operating.</b> (i) The documented one: <c>GetScopedProvider</c> finds no <c>Window</c> and no initialised
/// static provider, so resolution defers to a <c>Loaded</c> that never fires — nothing in <c>tests/</c> calls
/// <c>ViewModelLocator.Initialize</c> or <c>SetScopedServiceProvider</c>, verified, so the static provider is
/// null for the whole suite regardless of collection order. (ii) An UNPROVEN reading of the source:
/// <c>GetViewModelType</c> is <c>viewName.Replace(".Views.", ".ViewModels.").Replace("View", "ViewModel")</c>,
/// and the second <c>Replace</c> hits EVERY occurrence — so <c>Pia.Views.HistoryView</c> would become
/// <c>Pia.ViewModelModels.HistoryViewModel</c>, a namespace that does not exist, making the attached property
/// inert for every view whatever the provider does.
/// </para>
/// <para>
/// <b>Discriminating between them is deliberately NOT attempted here.</b> It would need a non-null static
/// provider, i.e. mutating process-wide state inside a shared-host collection — a larger hazard than the
/// finding. Reading (ii) is recorded as a code-reading finding in the Batch 15 record and is left UNFIXED on
/// purpose: fixing it would give eight views a <c>DataContext</c> they do not have today, which is a
/// behavioural change and not this batch's business.
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
