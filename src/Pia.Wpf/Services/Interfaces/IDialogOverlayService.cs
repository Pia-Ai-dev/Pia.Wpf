using Pia.Views.Controls;

namespace Pia.Services.Interfaces;

public interface IDialogOverlayService
{
    void SetOverlayHost(DialogOverlayHost host);
    DialogOverlayHost GetOverlayHost();
}
