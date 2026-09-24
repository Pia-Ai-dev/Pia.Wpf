using System.Windows.Media.Imaging;

namespace Pia.Services.Screen;

public enum CaptureFailureReason
{
    None = 0,
    TargetGone,
    Minimized,
    Cloaked,
    EmptyBounds,
    SelfTarget,
    SelfExclusionFailed,
    UniformFrame,
    Timeout,
    NativeError,

    /// <summary>Pia had to hide itself by painting black, and it covered the display that was captured.</summary>
    SelfBlackout,
}

public sealed record CaptureResult(
    CaptureTarget Target,
    BitmapSource? Bitmap,
    int Width,
    int Height,
    CaptureFailureReason Reason)
{
    public bool IsSuccess => Bitmap is not null;

    public static CaptureResult Success(CaptureTarget target, BitmapSource frozen)
    {
        if (!frozen.IsFrozen)
        {
            throw new ArgumentException("A capture must be frozen before it leaves the capture thread.", nameof(frozen));
        }

        return new CaptureResult(target, frozen, frozen.PixelWidth, frozen.PixelHeight, CaptureFailureReason.None);
    }

    public static CaptureResult Failed(CaptureTarget target, CaptureFailureReason reason) =>
        new(target, null, 0, 0, reason);
}
