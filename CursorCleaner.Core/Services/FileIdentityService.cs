using System.Runtime.InteropServices;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public static class FileIdentityService
{
    public static IFileIdentityService CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsFileIdentityService();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacFileIdentityService();
        }

        return new UnsupportedFileIdentityService();
    }
}

public sealed class WindowsFileIdentityService : IFileIdentityService
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public bool TryGetFileIdentity(string path, out FileIdentity? identity, out string? error)
    {
        identity = null;
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "File identity verification requires Windows.";
            return false;
        }

        try
        {
            using var handle = File.OpenHandle(
                PathSafety.Normalize(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            if (!GetFileInformationByHandle(handle, out var information))
            {
                error = $"File identity could not be read (Win32 error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            identity = new FileIdentity(information.VolumeSerialNumber, fileId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"File identity could not be read: {ex.Message}";
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public sealed class MacFileIdentityService : IFileIdentityService
{
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public bool TryGetFileIdentity(string path, out FileIdentity? identity, out string? error)
    {
        identity = null;
        error = null;
        if (!OperatingSystem.IsMacOS())
        {
            error = "File identity verification requires macOS.";
            return false;
        }

        try
        {
            var normalized = PathSafety.Normalize(path);
            if (Stat(normalized, out var status) != 0)
            {
                error = $"File identity could not be read (errno {Marshal.GetLastPInvokeError()}).";
                return false;
            }

            var device = unchecked((ulong)status.Device);
            var inode = status.Inode;
            if (device == 0 && inode == 0)
            {
                error = "File identity could not be read (empty device and inode).";
                return false;
            }

            identity = new FileIdentity(device, inode);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"File identity could not be read: {ex.Message}";
            return false;
        }
    }

    // Darwin arm64 / x86_64 struct stat: st_dev at 0, st_ino at 8. Extra fields are padding so the
    // libc `stat` symbol can fill a complete record without depending on a raw 256-byte guess.
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        public int Device;
        public ushort Mode;
        public ushort Nlink;
        public ulong Inode;
        public uint Uid;
        public uint Gid;
        public int Rdev;
        private readonly int _timespecAlignment;
        public Timespec Atim;
        public Timespec Mtim;
        public Timespec Ctim;
        public Timespec Btim;
        public long Size;
        public long Blocks;
        public int Blksize;
        public uint Flags;
        public uint Gen;
        public int Lspare;
        public long Qspare0;
        public long Qspare1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Sec;
        public long Nsec;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinStat status);
}

public sealed class UnsupportedFileIdentityService : IFileIdentityService
{
    public bool TryGetFileIdentity(string path, out FileIdentity? identity, out string? error)
    {
        identity = null;
        error = "File identity verification is not available on this platform.";
        return false;
    }
}
