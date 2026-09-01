using System.Runtime.InteropServices;
using NAudio.Dmo;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// COM surface of the Windows Voice Capture DSP (<c>mfwmaaec.dll</c>), the OS-supplied acoustic echo
/// canceller. NAudio ships the DMO buffer types but keeps <c>IMediaObject</c> internal and its
/// <c>MediaObject</c> wrapper unconstructable from outside, so the object itself is declared here.
/// </summary>
internal static class VoiceCaptureDsp
{
    /// <summary>CLSID_CWMAudioAEC — registered as "AEC" against <c>mfwmaaec.dll</c>.</summary>
    internal static readonly Guid ClassId = new("745057c7-f353-4f2d-a7ee-58434477730e");

    internal static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MediaSubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid FormatWaveFormatEx = new("05589f81-c356-11ce-bf01-00aa0055595a");

    private static readonly Guid AecPropertyFormat = new("6f52c567-0360-4bd2-9617-ccbf1421c939");

    // PID_FIRST_USABLE is 2, and every MFPKEY_WMAAECMA_* key counts up from there in declaration order.
    internal static readonly PropertyKey SystemMode = new(AecPropertyFormat, 2);
    internal static readonly PropertyKey SourceMode = new(AecPropertyFormat, 3);

    /// <summary>AEC_SYSTEM_MODE.SINGLE_CHANNEL_AEC — echo cancellation, no microphone-array processing.</summary>
    internal const int SingleChannelAec = 0;

    internal const int ClsCtxInprocServer = 0x1;

    private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");

    internal static object Create()
    {
        var clsid = ClassId;
        var iid = IidUnknown;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var instance);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        return instance;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        int context,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid formatId, int propertyId)
{
    public Guid FormatId = formatId;
    public int PropertyId = propertyId;
}

/// <summary>
/// Just enough PROPVARIANT to write a VT_I4 or VT_BOOL. Sized to the real 24-byte union so the DSP can
/// never write past it; nothing here reads a variant back.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    private const short VtI4 = 3;
    private const short VtBool = 11;
    private const short VariantTrue = -1;

    [FieldOffset(0)] public short Type;
    [FieldOffset(8)] public int Int32Value;
    [FieldOffset(8)] public short BoolValue;

    internal static PropVariant FromInt32(int value) => new() { Type = VtI4, Int32Value = value };

    internal static PropVariant FromBool(bool value)
        => new() { Type = VtBool, BoolValue = value ? VariantTrue : (short)0 };
}

[StructLayout(LayoutKind.Sequential)]
internal struct DspMediaType
{
    public Guid MajorType;
    public Guid SubType;
    [MarshalAs(UnmanagedType.Bool)] public bool FixedSizeSamples;
    [MarshalAs(UnmanagedType.Bool)] public bool TemporalCompression;
    public int SampleSize;
    public Guid FormatType;
    public IntPtr Unknown;
    public int FormatSize;
    public IntPtr Format;
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int propertyCount);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

/// <summary>
/// <c>IMediaObject</c> (mediaobj.h). Every method is declared, in vtable order — a COM interface cannot
/// be trimmed to the methods a caller happens to use.
/// </summary>
[ComImport]
[Guid("d8ad0f58-5494-4102-97c5-ec798e59bcf4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMediaObject
{
    [PreserveSig] int GetStreamCount(out int inputStreams, out int outputStreams);
    [PreserveSig] int GetInputStreamInfo(int inputStreamIndex, out int flags);
    [PreserveSig] int GetOutputStreamInfo(int outputStreamIndex, out int flags);
    [PreserveSig] int GetInputType(int inputStreamIndex, int typeIndex, out DspMediaType mediaType);
    [PreserveSig] int GetOutputType(int outputStreamIndex, int typeIndex, out DspMediaType mediaType);
    [PreserveSig] int SetInputType(int inputStreamIndex, [In] ref DspMediaType mediaType, int flags);
    [PreserveSig] int SetOutputType(int outputStreamIndex, [In] ref DspMediaType mediaType, int flags);
    [PreserveSig] int GetInputCurrentType(int inputStreamIndex, out DspMediaType mediaType);
    [PreserveSig] int GetOutputCurrentType(int outputStreamIndex, out DspMediaType mediaType);
    [PreserveSig] int GetInputSizeInfo(int inputStreamIndex, out int size, out int maxLookahead, out int alignment);
    [PreserveSig] int GetOutputSizeInfo(int outputStreamIndex, out int size, out int alignment);
    [PreserveSig] int GetInputMaxLatency(int inputStreamIndex, out long maxLatency);
    [PreserveSig] int SetInputMaxLatency(int inputStreamIndex, long maxLatency);
    [PreserveSig] int Flush();
    [PreserveSig] int Discontinuity(int inputStreamIndex);
    [PreserveSig] int AllocateStreamingResources();
    [PreserveSig] int FreeStreamingResources();
    [PreserveSig] int GetInputStatus(int inputStreamIndex, out int flags);
    [PreserveSig] int ProcessInput(int inputStreamIndex, IMediaBuffer buffer, int flags, long timestamp, long timeLength);
    [PreserveSig] int ProcessOutput(int flags, int outputBufferCount, [In, Out] DmoOutputDataBuffer[] outputBuffers, out int status);
    [PreserveSig] int Lock(bool acquireLock);
}
