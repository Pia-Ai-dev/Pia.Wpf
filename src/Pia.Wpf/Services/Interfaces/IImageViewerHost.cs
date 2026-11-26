using Pia.Views.Controls;

namespace Pia.Services.Interfaces;

public interface IImageViewerHost
{
    void SetHost(ImageViewerOverlay host);
    ImageViewerOverlay GetHost();
}
