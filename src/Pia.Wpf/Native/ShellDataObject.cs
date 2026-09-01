using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Pia.Native;

/// <summary>
/// COM plumbing for the shell's virtual-file drag formats. A source whose items have no path yet —
/// Outlook's message list is the one that matters — publishes CFSTR_FILEDESCRIPTORW plus
/// CFSTR_FILECONTENTS instead of CF_HDROP, and the receiver has to pull and write the bytes itself.
/// </summary>
internal static class ShellDataObject
{
    public const string FileGroupDescriptorW = "FileGroupDescriptorW";
    public const string FileContents = "FileContents";

    public const int STGM_READWRITE = 0x00000002;
    public const int STGM_SHARE_EXCLUSIVE = 0x00000010;
    public const int STGM_CREATE = 0x00001000;

    // DV_E_TYMED / DV_E_FORMATETC: the source has the item but not in the medium we asked for.
    public const int DV_E_TYMED = unchecked((int)0x80040069);
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    public static extern ushort RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClipboardFormatNameW")]
    public static extern int GetClipboardFormatName(uint format, [Out] char[] lpszFormatName, int cchMaxCount);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void StgCreateDocfile(string pwcsName, int grfMode, int reserved, out IStorage ppstgOpen);

    /// <summary>Only <c>CopyTo</c> and <c>Commit</c> are ever called; the rest are vtable slots that have to
    /// be declared in order for those two to land on the right entries.</summary>
    [ComImport]
    [Guid("0000000B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, int grfMode, int reserved1, int reserved2, out IStream ppstm);
        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IntPtr reserved1, int grfMode, int reserved2, out IStream ppstm);
        void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, int grfMode, int reserved1, int reserved2, out IStorage ppstg);
        void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string? pwcsName, IntPtr pstgPriority, int grfMode, IntPtr snbExclude, int reserved, out IStorage ppstg);
        void CopyTo(int ciidExclude, IntPtr rgiidExclude, IntPtr snbExclude, IStorage pstgDest);
        void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IntPtr pstgDest, [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName, int grfFlags);
        void Commit(int grfCommitFlags);
        void Revert();
        void EnumElements(int reserved1, IntPtr reserved2, int reserved3, out IntPtr ppenum);
        void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsName);
        void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsOldName, [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName);
        void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string? pwcsName, IntPtr pctime, IntPtr patime, IntPtr pmtime);
        void SetClass(ref Guid clsid);
        void SetStateBits(int grfStateBits, int grfMask);
        void Stat(out STATSTG pstatstg, int grfStatFlag);
    }
}
