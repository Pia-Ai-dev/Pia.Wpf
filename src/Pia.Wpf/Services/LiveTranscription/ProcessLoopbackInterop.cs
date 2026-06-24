using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// All P/Invoke and COM interop for per-process WASAPI loopback capture lives in this one file
/// (per the unit's "isolate all P/Invoke in one file" rule).
///
/// <para>Struct layouts, enum values, the device-path literal and the <c>IAudioClient::Initialize</c>
/// flags are taken from the Microsoft <b>ApplicationLoopback</b> sample
/// (<c>microsoft/Windows-classic-samples</c>, <c>Samples/ApplicationLoopback/cpp/LoopbackCapture.cpp</c>)
/// and the <c>audioclientactivationparams.h</c> / <c>mmdeviceapi.h</c> SDK reference. The COM
/// interface IIDs are the documented WASAPI IIDs (<c>audioclient.h</c> / <c>mmdeviceapi.h</c>).</para>
///
/// <para><b>UNVERIFIED:</b> the SDK header <c>req.target-min-winverclnt</c> is <b>Windows 10 Build
/// 20348</b>; the project's TFM floor is 17763, so the runtime guard in
/// <see cref="ProcessLoopbackAudioCaptureService"/> is load-bearing. None of this interop can be
/// exercised here (no live render stream from the target PID), so it is correct-by-construction
/// against the reference, not run-verified.</para>
/// </summary>
internal static class ProcessLoopbackInterop
{
    /// <summary>
    /// Pseudo device-interface path passed to <see cref="ActivateAudioInterfaceAsync"/> to request
    /// process-loopback capture (defined as <c>VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK</c> in
    /// <c>mmdeviceapi.h</c>).
    /// </summary>
    public const string VirtualAudioDeviceProcessLoopbackPath = "VAD\\Process_Loopback";

    // ---- IIDs (audioclient.h / mmdeviceapi.h) ----------------------------------------------------
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    // ---- Enums (audioclientactivationparams.h) ---------------------------------------------------
    public enum AUDIOCLIENT_ACTIVATION_TYPE
    {
        Default = 0,
        ProcessLoopback = 1,
    }

    public enum PROCESS_LOOPBACK_MODE
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1,
    }

    // ---- Activation params (audioclientactivationparams.h) ---------------------------------------
    // AUDIOCLIENT_ACTIVATION_PARAMS is a tagged union of a single member today
    // (ProcessLoopbackParams), so a flat sequential layout matches the C struct exactly.
    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    // ---- WAVEFORMATEX (mmreg.h) ------------------------------------------------------------------
    public const ushort WAVE_FORMAT_PCM = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    // ---- IAudioClient::Initialize flags (audioclient.h) ------------------------------------------
    public const uint AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    // ---- PROPVARIANT (propidl.h), VT_BLOB variant only -------------------------------------------
    public const ushort VT_BLOB = 65; // VARENUM.VT_BLOB

    // Explicit layout matching the 64-bit PROPVARIANT union: a 2-byte vt followed by three reserved
    // WORDs (8 bytes total) before the union payload. For VT_BLOB the payload is a BLOB
    // { ULONG cbSize; void* pBlobData }, which on x64 sits at offsets 8 and 16 (8-byte pointer
    // alignment). Total size matches sizeof(PROPVARIANT) = 24 on x64. The app is x64-only (NAudio +
    // WASAPI), so this single layout is sufficient.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public uint blobSize;   // BLOB.cbSize
        [FieldOffset(16)] public IntPtr blobData; // BLOB.pBlobData
    }

    // ---- COM interfaces --------------------------------------------------------------------------

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        // HRESULT GetActivateResult([out] HRESULT* activateResult, [out] IUnknown** activatedInterface)
        void GetActivateResult(
            [MarshalAs(UnmanagedType.Error)] out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        // HRESULT ActivateCompleted([in] IActivateAudioInterfaceAsyncOperation* activateOperation)
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        void Initialize(
            uint shareMode,
            uint streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            [In] ref WAVEFORMATEX pFormat,
            [In] ref Guid audioSessionGuid);

        uint GetBufferSize();
        long GetStreamLatency();
        uint GetCurrentPadding();

        [PreserveSig]
        int IsFormatSupported(uint shareMode, [In] ref WAVEFORMATEX pFormat, IntPtr ppClosestMatch);

        IntPtr GetMixFormat();
        void GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);

        // GetService([in] REFIID riid, [out] void** ppv)
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetService([In] ref Guid riid);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        // HRESULT GetBuffer([out] BYTE** ppData, [out] UINT32* pNumFramesToRead,
        //                   [out] DWORD* pdwFlags, [out] UINT64* pu64DevicePosition,
        //                   [out] UINT64* pu64QPCPosition)
        void GetBuffer(
            out IntPtr ppData,
            out uint pNumFramesToRead,
            out uint pdwFlags,
            out ulong pu64DevicePosition,
            out ulong pu64QPCPosition);

        void ReleaseBuffer(uint numFramesRead);

        void GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    // ---- mmdeviceapi.h entry point ---------------------------------------------------------------
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>
    /// Minimal agile completion handler that signals a managed event when activation finishes.
    /// <see cref="ActivateAudioInterfaceAsync"/> calls back on an MTA worker thread, so the handler
    /// must be agile (the COM-visible class is free-threaded by default for IUnknown-only interfaces).
    /// </summary>
    public sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _completed = new(false);

        public IActivateAudioInterfaceAsyncOperation? Operation { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Operation = activateOperation;
            _completed.Set();
        }

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);
    }

    /// <summary>Builds a 16-bit PCM <see cref="WAVEFORMATEX"/> for the given rate/channels.</summary>
    public static WAVEFORMATEX CreatePcm16Format(int sampleRate, int channels)
    {
        var blockAlign = (ushort)(channels * sizeof(short));
        return new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = blockAlign,
            nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
            cbSize = 0,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSupportedOnThisWindows()
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
}
