using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pia.Native;

internal static partial class UserEnvironmentBlock
{
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_IMPERSONATE = 0x0004;
    private const uint TOKEN_QUERY = 0x0008;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    /// <summary>The block Windows builds for this user from the registry, without this process's own variables; null on failure.</summary>
    public static Dictionary<string, string>? Read()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, out var raw))
            return null;

        using var token = new SafeAccessTokenHandle(raw);
        if (!CreateEnvironmentBlock(out var block, token, inherit: false) || block == 0)
            return null;

        try
        {
            return Parse(block);
        }
        finally
        {
            DestroyEnvironmentBlock(block);
        }
    }

    /// <summary>Reads a double-null-terminated UTF-16 block; the per-drive <c>=C:</c> entries are skipped.</summary>
    internal static Dictionary<string, string> Parse(nint block)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cursor = block;
        while (Marshal.PtrToStringUni(cursor) is { Length: > 0 } entry)
        {
            cursor += (entry.Length + 1) * sizeof(char);
            var separator = entry.IndexOf('=');
            if (separator > 0)
                variables[entry[..separator]] = entry[(separator + 1)..];
        }

        return variables;
    }
}
