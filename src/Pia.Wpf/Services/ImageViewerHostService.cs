using Pia.Services.Interfaces;
using Pia.Views.Controls;

namespace Pia.Services;

public class ImageViewerHostService : IImageViewerHost
{
    private ImageViewerOverlay? _host;

    public void SetHost(ImageViewerOverlay host)
    {
        _host = host;
    }

    public ImageViewerOverlay GetHost()
    {
        return _host ?? throw new InvalidOperationException("No image viewer host available");
    }
}
