using System;
using System.Runtime.InteropServices;

namespace Pia.Emoji;

/// <summary>
/// Hand-rolled interop for the in-box OS color-text stack — Direct2D (<c>d2d1.dll</c>),
/// DirectWrite (<c>dwrite.dll</c>) and the Windows Imaging Component (<c>windowscodecs.dll</c> via
/// <c>ole32.dll</c>'s <c>CoCreateInstance</c>). Only the handful of COM methods needed to render a
/// single emoji into a premultiplied-BGRA WIC bitmap are bound, by calling the vtable slot directly
/// through <c>delegate* unmanaged</c> pointers — so we never declare the ~dozens of preceding
/// methods that <c>[GeneratedComInterface]</c> would force.
/// </summary>
/// <remarks>
/// Every vtable offset, GUID, struct layout and enum value below was cross-checked against TerraFX
/// <c>[VtblIndex]</c> annotations, the Windows SDK headers and Microsoft Learn. COM methods use the
/// <c>STDMETHODCALLTYPE</c> (<c>__stdcall</c>) convention, hence <c>delegate* unmanaged[Stdcall]</c>.
/// </remarks>
internal static unsafe partial class EmojiInterop
{
    // ---- GUIDs (verified against TerraFX / Windows SDK headers) ----
    internal static readonly Guid IID_ID2D1Factory = new("06152247-6F50-465A-9245-118BFD3B6007");
    internal static readonly Guid IID_ID2D1DeviceContext = new("E8F7FE7A-191C-466D-AD95-975678BDA998");
    internal static readonly Guid IID_IDWriteFactory = new("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48");
    internal static readonly Guid CLSID_WICImagingFactory = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
    internal static readonly Guid IID_IWICImagingFactory = new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
    internal static readonly Guid GUID_WICPixelFormat32bppPBGRA = new("6FDDC324-4E03-4BFE-B185-3D77768DC910");

    // ---- Enum / flag values ----
    internal const uint D2D1_FACTORY_TYPE_SINGLE_THREADED = 0;
    internal const uint DWRITE_FACTORY_TYPE_SHARED = 0;
    internal const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    internal const uint D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
    internal const uint D2D1_RENDER_TARGET_TYPE_DEFAULT = 0;
    internal const uint D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT = 4;
    internal const uint DWRITE_MEASURING_MODE_NATURAL = 0;
    internal const uint DWRITE_FONT_WEIGHT_NORMAL = 400;
    internal const uint DWRITE_FONT_STYLE_NORMAL = 0;
    internal const uint DWRITE_FONT_STRETCH_NORMAL = 5;
    internal const uint DWRITE_TEXT_ALIGNMENT_CENTER = 2;
    internal const uint DWRITE_PARAGRAPH_ALIGNMENT_CENTER = 2;
    internal const uint WICBitmapCacheOnLoad = 2;
    internal const uint CLSCTX_INPROC_SERVER = 1;
    internal const uint COINIT_APARTMENTTHREADED = 2;
    internal const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    // ---- Structs (exact native layout) ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_PIXEL_FORMAT
    {
        public uint format;     // DXGI_FORMAT
        public uint alphaMode;  // D2D1_ALPHA_MODE
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_RENDER_TARGET_PROPERTIES
    {
        public uint type;                 // D2D1_RENDER_TARGET_TYPE
        public D2D1_PIXEL_FORMAT pixelFormat;
        public float dpiX;
        public float dpiY;
        public uint usage;                // D2D1_RENDER_TARGET_USAGE
        public uint minLevel;             // D2D1_FEATURE_LEVEL
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_COLOR_F
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_RECT_F
    {
        public float left;
        public float top;
        public float right;
        public float bottom;
    }

    // ---- Module entry points ----
    [LibraryImport("d2d1.dll")]
    internal static partial int D2D1CreateFactory(uint factoryType, in Guid riid, IntPtr factoryOptions, out IntPtr factory);

    [LibraryImport("dwrite.dll")]
    internal static partial int DWriteCreateFactory(uint factoryType, in Guid iid, out IntPtr factory);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    // ---- vtable plumbing ----
    private static void** VtblOf(IntPtr obj) => *(void***)obj;

    /// <summary>IUnknown::Release (slot 2).</summary>
    internal static void Release(IntPtr unk)
    {
        if (unk != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)VtblOf(unk)[2])(unk);
    }

    /// <summary>IUnknown::QueryInterface (slot 0).</summary>
    internal static int QueryInterface(IntPtr unk, Guid iid, out IntPtr ppv)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)VtblOf(unk)[0])(unk, &iid, &result);
        ppv = result;
        return hr;
    }

    /// <summary>ID2D1Factory::CreateWicBitmapRenderTarget (slot 13).</summary>
    internal static int CreateWicBitmapRenderTarget(IntPtr factory, IntPtr wicBitmap, D2D1_RENDER_TARGET_PROPERTIES props, out IntPtr renderTarget)
    {
        IntPtr rt;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, D2D1_RENDER_TARGET_PROPERTIES*, IntPtr*, int>)VtblOf(factory)[13])(factory, wicBitmap, &props, &rt);
        renderTarget = rt;
        return hr;
    }

    /// <summary>ID2D1RenderTarget::CreateSolidColorBrush (slot 8); brushProperties passed as null.</summary>
    internal static int CreateSolidColorBrush(IntPtr renderTarget, D2D1_COLOR_F color, out IntPtr brush)
    {
        IntPtr b;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, IntPtr, IntPtr*, int>)VtblOf(renderTarget)[8])(renderTarget, &color, IntPtr.Zero, &b);
        brush = b;
        return hr;
    }

    /// <summary>ID2D1RenderTarget::BeginDraw (slot 48).</summary>
    internal static void BeginDraw(IntPtr renderTarget) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)VtblOf(renderTarget)[48])(renderTarget);

    /// <summary>ID2D1RenderTarget::Clear (slot 47).</summary>
    internal static void Clear(IntPtr renderTarget, D2D1_COLOR_F color) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, void>)VtblOf(renderTarget)[47])(renderTarget, &color);

    /// <summary>
    /// ID2D1RenderTarget::DrawText (slot 27) — returns void; errors surface from EndDraw. Pass the
    /// <see cref="D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT"/> option for color glyphs. <paramref name="target"/>
    /// may be the base render target or a QI'd ID2D1DeviceContext (same inherited slot).
    /// </summary>
    internal static void DrawText(IntPtr target, string text, IntPtr textFormat, D2D1_RECT_F layoutRect, IntPtr defaultFillBrush, uint options)
    {
        fixed (char* pText = text)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, D2D1_RECT_F*, IntPtr, uint, uint, void>)VtblOf(target)[27])(
                target, pText, (uint)text.Length, textFormat, &layoutRect, defaultFillBrush, options, DWRITE_MEASURING_MODE_NATURAL);
        }
    }

    /// <summary>ID2D1RenderTarget::EndDraw (slot 49); tag1/tag2 passed as null. Returns HRESULT.</summary>
    internal static int EndDraw(IntPtr renderTarget) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)VtblOf(renderTarget)[49])(renderTarget, IntPtr.Zero, IntPtr.Zero);

    /// <summary>IDWriteFactory::CreateTextFormat (slot 15); system font collection, locale "".</summary>
    internal static int CreateTextFormat(IntPtr factory, string fontFamily, float fontSize, out IntPtr textFormat)
    {
        IntPtr fmt;
        int hr;
        fixed (char* pFamily = fontFamily)
        fixed (char* pLocale = string.Empty)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, uint, uint, uint, float, char*, IntPtr*, int>)VtblOf(factory)[15])(
                factory, pFamily, IntPtr.Zero, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, fontSize, pLocale, &fmt);
        }

        textFormat = fmt;
        return hr;
    }

    /// <summary>IDWriteTextFormat::SetTextAlignment (slot 3).</summary>
    internal static int SetTextAlignment(IntPtr textFormat, uint alignment) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)VtblOf(textFormat)[3])(textFormat, alignment);

    /// <summary>IDWriteTextFormat::SetParagraphAlignment (slot 4).</summary>
    internal static int SetParagraphAlignment(IntPtr textFormat, uint alignment) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)VtblOf(textFormat)[4])(textFormat, alignment);

    /// <summary>IWICImagingFactory::CreateBitmap (slot 17); pixelFormat is REFWICPixelFormatGUID (a GUID pointer).</summary>
    internal static int CreateBitmap(IntPtr factory, uint width, uint height, Guid pixelFormat, uint cacheOption, out IntPtr bitmap)
    {
        IntPtr bmp;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, Guid*, uint, IntPtr*, int>)VtblOf(factory)[17])(
            factory, width, height, &pixelFormat, cacheOption, &bmp);
        bitmap = bmp;
        return hr;
    }

    /// <summary>IWICBitmapSource::CopyPixels (slot 7, inherited by IWICBitmap); whole-bitmap (prc null).</summary>
    internal static int CopyPixels(IntPtr bitmap, uint stride, byte[] buffer)
    {
        fixed (byte* pBuffer = buffer)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, byte*, int>)VtblOf(bitmap)[7])(
                bitmap, IntPtr.Zero, stride, (uint)buffer.Length, pBuffer);
        }
    }
}
