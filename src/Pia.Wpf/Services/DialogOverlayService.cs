using Pia.Services.Interfaces;
using Pia.Views.Controls;

namespace Pia.Services;

public class DialogOverlayService : IDialogOverlayService
{
    private DialogOverlayHost? _host;

    public void SetOverlayHost(DialogOverlayHost host)
    {
        _host = host;
    }

    public DialogOverlayHost GetOverlayHost()
    {
        return _host ?? throw new InvalidOperationException("No dialog overlay host available");
    }
}
