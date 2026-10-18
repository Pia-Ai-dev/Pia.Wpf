using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Pia.TestMcpServer;

// Deliberately no path validation: the sandbox is the only guard under test.
[McpServerToolType]
public sealed partial class FileTools
{
    private const string PathHint =
        "Paths may be absolute (e.g. C:\\data\\file.txt) or relative to the server's current working directory.";

    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint ReparseTagMountPoint = 0xA0000003;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [McpServerTool(Name = "list_directory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the entries of a directory, one per line: '[D] name' for sub-directories and "
        + "'[F] name (size bytes)' for files. " + PathHint)]
    public static CallToolResult ListDirectory(
        [Description("Directory to list. Absolute or relative to the server's working directory. "
            + "Defaults to '.' (the working directory itself).")]
        string path = ".")
        => ToolResults.Run(() =>
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            var sb = new StringBuilder();
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("[D] ").AppendLine(d.Name);
            }

            foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("[F] ").Append(f.Name).Append(" (").Append(f.Length).AppendLine(" bytes)");
            }

            return sb.Length == 0 ? $"(directory {dir.FullName} is empty)" : sb.ToString().TrimEnd();
        });

    [McpServerTool(Name = "read_file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reads a text file and returns its full content. " + PathHint)]
    public static CallToolResult ReadFile(
        [Description("File to read. Absolute or relative to the server's working directory.")]
        string path)
        => ToolResults.Run(() => File.ReadAllText(Path.GetFullPath(path)));

    [McpServerTool(Name = "write_file", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Creates a text file or OVERWRITES an existing one with the given content (UTF-8). "
        + "Missing parent directories are created. " + PathHint)]
    public static CallToolResult WriteFile(
        [Description("File to write. Absolute or relative to the server's working directory.")]
        string path,
        [Description("The complete text content to write. Replaces any existing content of the file.")]
        string content)
        => ToolResults.Run(() =>
        {
            var full = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content ?? string.Empty);
            File.WriteAllBytes(full, bytes);
            return $"Wrote {bytes.Length} bytes to {full}";
        });

    [McpServerTool(Name = "delete_file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Permanently deletes a single file (no recycle bin). " + PathHint)]
    public static CallToolResult DeleteFile(
        [Description("File to delete. Absolute or relative to the server's working directory.")]
        string path)
        => ToolResults.Run(() =>
        {
            var full = Path.GetFullPath(path);

            // File.Delete ignores a missing file, which would read as a successful delete.
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Could not find file '{full}'.", full);
            }

            File.Delete(full);
            return $"Deleted {full}";
        });

    [McpServerTool(Name = "delete_directory", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Permanently deletes a directory. Fails on a non-empty directory unless 'recursive' is true, "
        + "in which case all files and sub-directories are deleted as well. " + PathHint)]
    public static CallToolResult DeleteDirectory(
        [Description("Directory to delete. Absolute or relative to the server's working directory.")]
        string path,
        [Description("true = delete the directory including all of its contents; "
            + "false (default) = delete only if the directory is empty.")]
        bool recursive = false)
        => ToolResults.Run(() =>
        {
            var full = Path.GetFullPath(path);
            Directory.Delete(full, recursive);
            return $"Deleted directory {full}";
        });

    [McpServerTool(Name = "export", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Sends data out over the network: an HTTP POST of a file's content (or of a short marker text "
        + "when no path is given) to the given URL, timeout 10 s. Any HTTP response status counts as delivered. "
        + PathHint)]
    public static Task<CallToolResult> Export(
        [Description("Absolute http:// or https:// URL that receives the POST, e.g. https://example.com/import.")]
        string url,
        [Description("Optional file whose content is posted. Absolute or relative to the server's working "
            + "directory. When omitted, a short marker text is posted instead.")]
        string? path = null,
        CancellationToken cancellationToken = default)
        => ToolResults.RunAsync(async () =>
        {
            var text = string.IsNullOrWhiteSpace(path) ? "pia-test-mcp-export" : File.ReadAllText(Path.GetFullPath(path));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var body = new StringContent(text, Encoding.UTF8);
            body.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            using var response = await http.PostAsync(url, body, cancellationToken).ConfigureAwait(false);
            return $"Exported {Encoding.UTF8.GetByteCount(text)} bytes to {url}: HTTP {(int)response.StatusCode}";
        });

    [McpServerTool(Name = "get_server_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns JSON describing the server process: current working directory, user, process id, "
        + "and whether it is running inside an AppContainer sandbox. Takes no arguments.")]
    public static CallToolResult GetServerInfo()
        => ToolResults.Run(() =>
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
            var info = new
            {
                cwd = Environment.CurrentDirectory,
                user = $"{Environment.UserDomainName}\\{Environment.UserName}",
                processId = Environment.ProcessId,
                inAppContainer = localAppData.Contains(@"\Packages\", StringComparison.OrdinalIgnoreCase)
                                 && localAppData.Contains(@"\AC", StringComparison.OrdinalIgnoreCase),
                localAppData,
                processPath = Environment.ProcessPath,
                machine = Environment.MachineName,
                framework = RuntimeInformation.FrameworkDescription,
            };
            return JsonSerializer.Serialize(info, IndentedJson);
        });

    [McpServerTool(Name = "get_environment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reports whether environment variables are set in the server process, one line per name: "
        + "'<name>: set (length N)', '<name>: empty' or '<name>: absent'. Never returns a value.")]
    public static CallToolResult GetEnvironment(
        [Description("Comma-separated variable names, e.g. 'OS,ComSpec'.")]
        string names)
        => ToolResults.Run(() =>
        {
            var lines = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => Environment.GetEnvironmentVariable(name) switch
                {
                    null => $"{name}: absent",
                    "" => $"{name}: empty",
                    var value => $"{name}: set (length {value.Length})",
                });
            return string.Join(Environment.NewLine, lines);
        });

    [McpServerTool(Name = "create_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Creates a directory junction or a directory symbolic link at 'path' that points at 'target'. "
        + "Returns 'created', or the exception type and the Win32 error code. " + PathHint)]
    public static CallToolResult CreateLink(
        [Description("Where the link is created. Must not exist yet.")]
        string path,
        [Description("The directory the link points at.")]
        string target,
        [Description("'junction' or 'symlink'.")]
        string kind)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var fullTarget = Path.GetFullPath(target);
            switch (kind)
            {
                case "junction":
                    CreateJunction(full, fullTarget);
                    break;
                case "symlink":
                    Directory.CreateSymbolicLink(full, fullTarget);
                    break;
                default:
                    throw new ArgumentException($"Unknown link kind '{kind}'.", nameof(kind));
            }

            return ToolResults.Ok("created");
        }
        catch (Exception ex)
        {
            return ToolResults.Error(new LinkFailedException(ex));
        }
    }

    private sealed class LinkFailedException(Exception cause)
        : Exception($"{cause.GetType().Name}, Win32 error {Win32Code(cause)}: {cause.Message}");

    private static int Win32Code(Exception ex) => ex switch
    {
        Win32Exception win32 => win32.NativeErrorCode,
        _ when (ex.HResult & 0xFFFF0000) == 0x80070000 => ex.HResult & 0xFFFF,
        _ => ex.HResult,
    };

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        try
        {
            using var handle = CreateFileW(path, GenericWrite, 0, IntPtr.Zero, OpenExisting,
                FlagOpenReparsePoint | FlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw Win32Failure("CreateFileW");

            var buffer = MountPointBuffer(@"\??\" + target, target);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw Win32Failure("FSCTL_SET_REPARSE_POINT");
        }
        catch
        {
            Directory.Delete(path, recursive: false);
            throw;
        }
    }

    private static Win32Exception Win32Failure(string step)
    {
        var code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{step} failed with Win32 error {code}");
    }

    // REPARSE_DATA_BUFFER, MountPointReparseBuffer: lengths in bytes without the terminators.
    private static byte[] MountPointBuffer(string substituteName, string printName)
    {
        var substitute = Encoding.Unicode.GetBytes(substituteName);
        var print = Encoding.Unicode.GetBytes(printName);
        var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[8 + dataLength];
        using var writer = new BinaryWriter(new MemoryStream(buffer));
        writer.Write(ReparseTagMountPoint);
        writer.Write((ushort)dataLength);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)substitute.Length);
        writer.Write((ushort)(substitute.Length + 2));
        writer.Write((ushort)print.Length);
        writer.Write(substitute);
        writer.Write((ushort)0);
        writer.Write(print);
        writer.Write((ushort)0);
        return buffer;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, byte[] inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
