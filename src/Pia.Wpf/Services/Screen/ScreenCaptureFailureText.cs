namespace Pia.Services.Screen;

public static class ScreenCaptureFailureText
{
    /// <summary>The MessageStrings key for a refusal; null for <see cref="CaptureFailureReason.None"/>.</summary>
    public static string? KeyFor(CaptureFailureReason reason) => reason switch
    {
        CaptureFailureReason.None => null,
        CaptureFailureReason.TargetGone => "Msg_Screen_TargetGone",
        CaptureFailureReason.Minimized => "Msg_Screen_Minimized",
        CaptureFailureReason.Cloaked => "Msg_Screen_Cloaked",
        CaptureFailureReason.EmptyBounds => "Msg_Screen_EmptyBounds",
        CaptureFailureReason.SelfTarget => "Msg_Screen_SelfTarget",
        CaptureFailureReason.SelfExclusionFailed => "Msg_Screen_SelfExclusionFailed",
        CaptureFailureReason.UniformFrame => "Msg_Screen_UniformFrame",
        CaptureFailureReason.Timeout => "Msg_Screen_Timeout",
        CaptureFailureReason.SelfBlackout => "Msg_Screen_SelfBlackout",
        _ => "Msg_Screen_NativeError",
    };
}
