using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Reads the path the operating system reports for an opened file or directory. On Windows
/// and macOS that path is spelled as the file system stores it, whatever case was used to
/// open it, which is what makes ordinal path comparisons reliable there.
/// </summary>
internal static partial class NativeFileSystem
{
    internal const uint FILE_LIST_DIRECTORY = 0x1;
    internal const uint FILE_READ_ATTRIBUTES = 0x80;
    internal const uint FILE_SHARE_READ_WRITE = 0x1 | 0x2;
    internal const uint FILE_SHARE_ALL = FILE_SHARE_READ_WRITE | 0x4;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const int MAC_O_DIRECTORY = 0x100000;
    internal const int MAC_O_CLOEXEC = 0x1000000;

    /// <summary>
    /// The path of the existing directory <paramref name="directoryPath"/> as the file system
    /// spells it, on Windows and macOS; null elsewhere or when it cannot be opened.
    /// </summary>
    internal static string? TryReadDirectorySpelling(string directoryPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateWindowsFile(
                    directoryPath, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                return handle.IsInvalid ? null : ReadWindowsFinalPath(handle);
            }

            if (OperatingSystem.IsMacOS())
            {
                var descriptor = OpenUnixDirectory(directoryPath, MAC_O_DIRECTORY | MAC_O_CLOEXEC);
                if (descriptor < 0)
                {
                    return null;
                }

                using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                return ReadMacDirectoryPath(descriptor);
            }
        }
        catch (IOException)
        {
            // The directory vanished or could not be opened, so its spelling is unknown.
        }

        return null;
    }

    /// <summary>
    /// The physical path of the directory entry <paramref name="path"/> names on Windows, as
    /// the file system spells it: a link is opened itself rather than followed. Null when
    /// there is no such entry.
    /// </summary>
    internal static string? TryReadWindowsEntryPath(string path)
    {
        try
        {
            using var handle = CreateWindowsFile(
                path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            return handle.IsInvalid ? null : ReadWindowsFinalPath(handle);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The physical path of the file or directory opened as <paramref name="handle"/> on
    /// Windows, without the "\\?\" prefix.
    /// </summary>
    internal static string ReadWindowsFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetWindowsFinalPathName(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new IOException($"Could not resolve the opened directory: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// The current physical path of the directory opened as <paramref name="descriptor"/>
    /// on macOS.
    /// </summary>
    internal static string ReadMacDirectoryPath(int descriptor)
    {
        // ATTR_CMN_FULLPATH: an attrreference to the path, after the buffer's length field.
        const uint ATTR_CMN_FULLPATH = 0x08000000;
        const int ATTR_BIT_MAP_COUNT = 5;
        var attributes = new MacAttributeList { BitmapCount = ATTR_BIT_MAP_COUNT, CommonAttributes = ATTR_CMN_FULLPATH };
        var buffer = new byte[4096 + 12];
        if (GetMacAttributes(descriptor, ref attributes, buffer, (nuint)buffer.Length, 0) != 0)
        {
            throw new IOException($"Could not resolve the opened directory: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        const int referenceOffset = sizeof(uint);
        var dataOffset = BitConverter.ToInt32(buffer, referenceOffset);
        var dataLength = BitConverter.ToUInt32(buffer, referenceOffset + sizeof(int));
        var start = referenceOffset + dataOffset;
        if (dataLength == 0 || dataOffset < 0 || start + (long)dataLength > buffer.Length)
        {
            throw new IOException("Could not resolve the opened directory.");
        }

        // The length includes the terminating NUL.
        return Encoding.UTF8.GetString(buffer, start, (int)dataLength - 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacAttributeList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint CommonAttributes;
        public uint VolumeAttributes;
        public uint DirectoryAttributes;
        public uint FileAttributes;
        public uint ForkAttributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateWindowsFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetWindowsFinalPathName(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenUnixDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fgetattrlist", SetLastError = true)]
    private static partial int GetMacAttributes(
        int descriptor,
        ref MacAttributeList attributes,
        [Out] byte[] buffer,
        nuint bufferSize,
        uint options);
}
