using System.Runtime.InteropServices;
using System.Text;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public sealed class VolumeService : IVolumeService
{
    public bool TryGetVolume(string path, out VolumeInfo? volume, out string? error)
    {
        volume = null;
        error = null;
        try
        {
            var existingPath = ResolveExistingPath(PathSafety.Normalize(path));
            if (OperatingSystem.IsWindows())
            {
                return TryGetWindowsVolume(existingPath, out volume, out error);
            }

            if (OperatingSystem.IsMacOS())
            {
                return TryGetMacVolume(existingPath, out volume, out error);
            }

            error = "Volume inspection is not available on this platform.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"Volume information could not be read: {ex.Message}";
            return false;
        }
    }

    private static string ResolveExistingPath(string path)
    {
        var current = path;
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                throw new IOException("No existing ancestor was found for the volume probe.");
            }

            current = parent;
        }

        return current;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryGetWindowsVolume(string path, out VolumeInfo? volume, out string? error)
    {
        volume = null;
        var mount = new StringBuilder(512);
        if (!GetVolumePathName(path, mount, mount.Capacity))
        {
            error = $"Volume mount point could not be read (Win32 error {Marshal.GetLastWin32Error()}).";
            return false;
        }

        if (!GetVolumeInformation(mount.ToString(), null, 0, out var serial, out _, out _, null, 0))
        {
            error = $"Volume identity could not be read (Win32 error {Marshal.GetLastWin32Error()}).";
            return false;
        }

        if (!GetDiskFreeSpaceEx(mount.ToString(), out _, out _, out var available))
        {
            error = $"Volume free space could not be read (Win32 error {Marshal.GetLastWin32Error()}).";
            return false;
        }

        var display = Path.TrimEndingDirectorySeparator(mount.ToString());
        volume = new VolumeInfo($"win:{serial:X8}", display, ToLong(available));
        error = null;
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool TryGetMacVolume(string path, out VolumeInfo? volume, out string? error)
    {
        volume = null;
        if (Stat(path, out var status) != 0)
        {
            error = $"Volume identity could not be read (errno {Marshal.GetLastPInvokeError()}).";
            return false;
        }

        var mount = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && IsOnVolume(path, drive.RootDirectory.FullName))
            .OrderByDescending(drive => PathSafety.Normalize(drive.RootDirectory.FullName).Length)
            .FirstOrDefault();
        if (mount is null)
        {
            error = "Volume mount point could not be resolved.";
            return false;
        }

        volume = new VolumeInfo(
            $"mac:{unchecked((ulong)status.Device):X}",
            PathSafety.Normalize(mount.RootDirectory.FullName),
            mount.AvailableFreeSpace);
        error = null;
        return true;
    }

    private static bool IsOnVolume(string path, string volumeRoot)
    {
        var normalizedPath = PathSafety.Normalize(path);
        var normalizedRoot = PathSafety.Normalize(volumeRoot);
        if (PathSafety.PathComparer.Equals(normalizedPath, normalizedRoot))
        {
            return true;
        }

        var prefix = PathSafety.PathComparer.Equals(normalizedRoot, Path.GetPathRoot(normalizedRoot))
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(
            prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

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
