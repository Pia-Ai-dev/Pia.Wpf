using Pia.Navigation;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>AutoWireViewModel defers to <c>Loaded</c>, which never fires in a view-parse test, so no DataContext arrives.</summary>
[Collection("WpfApplicationStatic")]
public class AutoWireViewModelPremiseTests
{
    [Fact]
    public void SettingAutoWireViewModel_LeavesTheDataContextNull_UnderTheTestHost()
    {
        var dataContext = WpfStaHost.Run(() =>
        {
            var view = new Pia.Views.HistoryView();
            ViewModelLocator.SetAutoWireViewModel(view, true);
            return view.DataContext?.GetType().FullName;
        });

        Assert.Null(dataContext);
    }
}
