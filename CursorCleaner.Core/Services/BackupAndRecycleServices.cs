using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public sealed class BackupService : IBackupService
{
    public const string SqliteFolderName = "sqlite";
    public const string StagingFileName = "staging.vscdb";
    public const string StagingFilePrefix = "staging_";
    public const string StagingFileExtension = ".vscdb";
    public const string CurrentFileName = "current.vscdb";

    private static readonly Regex LegacyTimestampDirectory = new(
        @"^\d{4}-\d{2}-\d{2}_\d{6}(_\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _backupBasePath;
    private readonly ILogService _log;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string, long?> _availableSpace;

    public BackupService(ILogService log, string? backupBasePath = null, Func<string, long?>? availableSpace = null)
    {
        _log = log;
        _backupBasePath = Path.GetFullPath(backupBasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CursorCleanerBackup"));
        _availableSpace = availableSpace ?? (path => TryGetAvailableSpace(path, out var available) ? available : null);
    }

    public string BackupRootPath => _backupBasePath;

    public async Task<BackupOperationResult> BackupAsync(IEnumerable<CleanupPlanItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshots = items.ToArray();
        var originalSize = snapshots.Sum(item => Math.Max(0, item.Size));
        if (snapshots.Length == 0)
        {
            return new BackupOperationResult(true, null, 0, []);
        }

        string backupDirectory;
        try
        {
            Directory.CreateDirectory(_backupBasePath);
            if (TryGetAvailableSpace(_backupBasePath, out var available) && available < originalSize)
            {
                return new BackupOperationResult(false, null, originalSize, [], "Insufficient free space for the backup.");
            }

            backupDirectory = CreateUniqueDirectory();
        }
        catch (Exception ex)
        {
            await TryLogAsync("error", "backup.create", "Failed to create the backup directory.", _backupBasePath, ex, cancellationToken).ConfigureAwait(false);
            return new BackupOperationResult(false, null, originalSize, [], ex.Message);
        }

        var results = new List<BackupItemResult>(snapshots.Length);
        foreach (var item in snapshots)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new BackupItemResult(item.FullPath, null, item.Size, item.LastWriteTimeUtc, false, "Backup was cancelled."));
                continue;
            }

            string? destination = null;
            try
            {
                var source = PathSafety.Normalize(item.FullPath);
                var root = PathSafety.Normalize(item.Root.Path);
                if (!PathSafety.IsWithin(source, root, allowRoot: false) || !File.Exists(source))
                {
                    throw new IOException("The source is missing or outside its recorded root.");
                }

                var relative = Path.GetRelativePath(root, source);
                if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    throw new IOException("The source relative path is unsafe.");
                }

                var rootFolder = BuildRootFolder(item.Root);
                destination = Path.GetFullPath(Path.Combine(backupDirectory, "files", rootFolder, relative));
                if (!PathSafety.IsWithin(destination, backupDirectory, allowRoot: false))
                {
                    throw new IOException("The backup destination is unsafe.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(destination, item.LastWriteTimeUtc);
                results.Add(new BackupItemResult(source, destination, item.Size, item.LastWriteTimeUtc, true, null));
                await TryLogAsync("info", "backup.file", "File backed up.", source, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException)
            {
                if (destination is not null)
                {
                    TryDelete(destination);
                }

                results.Add(new BackupItemResult(item.FullPath, destination, item.Size, item.LastWriteTimeUtc, false, ex.Message));
                await TryLogAsync("error", "backup.file", "File backup failed.", item.FullPath, ex, CancellationToken.None).ConfigureAwait(false);
            }
        }

        try
        {
            var manifest = new
            {
                createdAt = DateTime.UtcNow,
                originalSize,
                files = results.Select(result => new
                {
                    originalPath = result.OriginalPath,
                    backupPath = result.BackupPath,
                    size = result.Size,
                    time = result.LastWriteTimeUtc,
                    result = result.Succeeded ? "success" : "failed",
                    error = result.Error
                })
            };
            var manifestPath = Path.Combine(backupDirectory, "manifest.json");
            await using var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, manifest, _jsonOptions, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryLogAsync("error", "backup.manifest", "Failed to write the backup manifest.", backupDirectory, ex, CancellationToken.None).ConfigureAwait(false);
            return new BackupOperationResult(false, backupDirectory, originalSize, results, $"Manifest creation failed: {ex.Message}");
        }

        var succeeded = results.All(result => result.Succeeded);
        return new BackupOperationResult(succeeded, backupDirectory, originalSize, results,
            succeeded ? null : "One or more files could not be backed up.");
    }

    public async Task<string> CreateSqliteBackupPathAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = PathSafety.Normalize(databasePath);
        if (!File.Exists(source))
        {
            throw new IOException("The database to back up is missing.");
        }

        var requiredBytes = GetCombinedSqliteSize(source);
        Directory.CreateDirectory(_backupBasePath);
        var sqliteRoot = GetSqliteRoot();
        Directory.CreateDirectory(sqliteRoot);
        var folder = GetRollingFolder(source);
        Directory.CreateDirectory(folder);

        EnsureVolumeFreeSpace(sqliteRoot, requiredBytes, "Insufficient free space for the SQLite backup");

        var destination = Path.GetFullPath(Path.Combine(folder, $"{StagingFilePrefix}{Guid.NewGuid():N}{StagingFileExtension}"));
        if (!PathSafety.IsWithin(destination, folder, allowRoot: false) || !IsStagingFileName(Path.GetFileName(destination)))
        {
            throw new IOException("The database backup destination is unsafe.");
        }

        TryDeleteDatabase(destination);
        await TryLogAsync("info", "backup.sqlite", "Reserved SQLite online backup destination.", destination, null, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public async Task<string> CommitSqliteBackupAsync(string stagingPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staging = PathSafety.Normalize(stagingPath);
        var folder = Path.GetDirectoryName(staging);
        if (string.IsNullOrWhiteSpace(folder)
            || !PathSafety.IsWithin(staging, GetSqliteRoot(), allowRoot: false)
            || !IsStagingFileName(Path.GetFileName(staging))
            || !File.Exists(staging))
        {
            throw new IOException("The SQLite staging backup path is invalid.");
        }

        var current = Path.GetFullPath(Path.Combine(folder, CurrentFileName));
        if (!PathSafety.IsWithin(current, folder, allowRoot: false))
        {
            throw new IOException("The SQLite current backup destination is unsafe.");
        }

        File.Move(staging, current, overwrite: true);
        TryDelete(staging + "-wal");
        TryDelete(staging + "-shm");
        TryDelete(staging + "-journal");
        await TryLogAsync("info", "backup.sqlite.commit", "Committed verified SQLite backup as current.", current, null, cancellationToken).ConfigureAwait(false);
        return current;
    }

    public void EnsureVolumeFreeSpace(string pathOnVolume, long requiredBytes, string operationLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOnVolume);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationLabel);
        if (requiredBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        var probePath = ResolveExistingPath(PathSafety.Normalize(pathOnVolume));
        var available = _availableSpace(probePath);
        if (available is null)
        {
            throw new IOException($"{operationLabel}; free space could not be determined.");
        }

        if (available.Value < requiredBytes)
        {
            throw new IOException($"{operationLabel}; {requiredBytes} bytes required.");
        }
    }

    private static string ResolveExistingPath(string path)
    {
        var current = path;
        while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        return string.IsNullOrWhiteSpace(current) ? path : current;
    }

    public static bool IsStagingFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (string.Equals(fileName, StagingFileName, StringComparison.Ordinal))
        {
            return true;
        }

        return fileName.StartsWith(StagingFilePrefix, StringComparison.Ordinal)
               && fileName.EndsWith(StagingFileExtension, StringComparison.Ordinal)
               && fileName.Length > StagingFilePrefix.Length + StagingFileExtension.Length;
    }

    public SqliteBackupUsage GetSqliteBackupUsage()
    {
        var sqliteRoot = GetSqliteRoot();
        var rollingBytes = 0L;
        var rollingCount = 0;
        if (Directory.Exists(sqliteRoot))
        {
            foreach (var folder in Directory.EnumerateDirectories(sqliteRoot))
            {
                var current = Path.Combine(folder, CurrentFileName);
                if (!File.Exists(current))
                {
                    continue;
                }

                rollingCount++;
                rollingBytes += GetDirectorySize(folder);
            }
        }

        var legacyBytes = 0L;
        var legacyCount = 0;
        if (Directory.Exists(_backupBasePath))
        {
            foreach (var directory in EnumerateLegacySqliteDirectories())
            {
                legacyCount++;
                legacyBytes += GetDirectorySize(directory);
            }
        }

        return new SqliteBackupUsage(_backupBasePath, rollingBytes, rollingCount, legacyBytes, legacyCount);
    }

    public async Task<SqliteBackupCleanupResult> CleanupLegacySqliteBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = 0;
        var reclaimed = 0L;
        try
        {
            foreach (var directory in EnumerateLegacySqliteDirectories().ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = GetDirectorySize(directory);
                Directory.Delete(directory, recursive: true);
                deleted++;
                reclaimed += size;
                await TryLogAsync("info", "backup.sqlite.legacy", "Deleted a legacy timestamped SQLite backup directory.", directory, null, cancellationToken).ConfigureAwait(false);
            }

            return new SqliteBackupCleanupResult(true, deleted, reclaimed, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await TryLogAsync("error", "backup.sqlite.legacy", "Failed to delete a legacy timestamped SQLite backup directory.", _backupBasePath, ex, CancellationToken.None).ConfigureAwait(false);
            return new SqliteBackupCleanupResult(false, deleted, reclaimed, ex.Message);
        }
    }

    private string CreateUniqueDirectory()
    {
        var name = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        for (var suffix = 0; ; suffix++)
        {
            var candidate = Path.Combine(_backupBasePath, suffix == 0 ? name : $"{name}_{suffix}");
            try
            {
                Directory.CreateDirectory(candidate);
                if (!Directory.EnumerateFileSystemEntries(candidate).Any())
                {
                    return candidate;
                }
            }
            catch (IOException) when (Directory.Exists(candidate))
            {
            }
        }
    }

    private string GetSqliteRoot() => Path.GetFullPath(Path.Combine(_backupBasePath, SqliteFolderName));

    private string GetRollingFolder(string databasePath)
    {
        var leaf = Path.GetFileName(databasePath);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            throw new IOException("The database backup file name is invalid.");
        }

        var safeLeaf = string.Concat(leaf.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var hash = Sha256Prefix(CanonicalPath(databasePath));
        var folder = Path.GetFullPath(Path.Combine(GetSqliteRoot(), $"{safeLeaf}_{hash}"));
        if (!PathSafety.IsWithin(folder, GetSqliteRoot(), allowRoot: false))
        {
            throw new IOException("The SQLite rolling backup folder is unsafe.");
        }

        return folder;
    }

    private IEnumerable<string> EnumerateLegacySqliteDirectories()
    {
        if (!Directory.Exists(_backupBasePath))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(_backupBasePath))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, SqliteFolderName, StringComparison.Ordinal)
                || !LegacyTimestampDirectory.IsMatch(name)
                || !IsLegacySqliteBackupDirectory(directory))
            {
                continue;
            }

            yield return directory;
        }
    }

    private static bool IsLegacySqliteBackupDirectory(string directory)
    {
        if (Directory.Exists(Path.Combine(directory, "files")))
        {
            return false;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (entries.Length == 0)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                return false;
            }

            var extension = Path.GetExtension(entry);
            if (!extension.Equals(".vscdb", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static long GetCombinedSqliteSize(string path)
    {
        return new[] { path, path + "-wal", path + "-shm" }
            .Where(File.Exists)
            .Sum(candidate => new FileInfo(candidate).Length);
    }

    private static long GetDirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string CanonicalPath(string path)
    {
        var normalized = PathSafety.Normalize(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string Sha256Prefix(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private static string BuildRootFolder(CursorDataRoot root)
    {
        var leaf = Path.GetFileName(PathSafety.Normalize(root.Path));
        var safeLeaf = string.Concat(leaf.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(PathSafety.Normalize(root.Path)).ToString("X8");
        return $"{root.Kind}_{safeLeaf}_{hash}";
    }

    private static bool TryGetAvailableSpace(string path, out long available)
    {
        available = 0;
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(path));
            if (!drive.IsReady)
            {
                return false;
            }

            available = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryLogAsync(string level, string operation, string message, string path, Exception? exception, CancellationToken cancellationToken)
    {
        try { await _log.WriteAsync(level, operation, message, path, exception, cancellationToken).ConfigureAwait(false); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDatabase(string path)
    {
        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
        TryDelete(path + "-journal");
    }
}

public sealed class WindowsRecycleBinService : IRecycleBinService
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxRecycleOnDelete = 0x00080000;

    public Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new RecycleResult(path, false, "The Windows recycle bin is unavailable on this platform."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RecycleResult>(cancellationToken);
        }

#pragma warning disable CA1416
        return Task.Run(() => RecycleCore(path), CancellationToken.None);
#pragma warning restore CA1416
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static RecycleResult RecycleCore(string path)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            var normalized = Path.GetFullPath(path);
            var operationType = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(operationType)!;
            ThrowIfFailed(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofxRecycleOnDelete));
            var shellItemId = typeof(IShellItem).GUID;
            ThrowIfFailed(SHCreateItemFromParsingName(normalized, IntPtr.Zero, ref shellItemId, out item));
            ThrowIfFailed(operation.DeleteItem(item!, IntPtr.Zero));
            ThrowIfFailed(operation.PerformOperations());
            ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted)
            {
                return new RecycleResult(normalized, false, "The recycle operation was aborted by the shell.");
            }

            return File.Exists(normalized)
                ? new RecycleResult(normalized, false, "The shell reported success but the file still exists.")
                : new RecycleResult(normalized, true, null);
        }
        catch (Exception ex)
        {
            return new RecycleResult(path, false, $"Recycle operation failed: {ex.Message}");
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static void ThrowIfFailed(int hresult) => Marshal.ThrowExceptionForHR(hresult);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(uint ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destinationFolder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string copyName, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destinationFolder);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destinationFolder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

public static class RecycleBinService
{
    public static IRecycleBinService CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRecycleBinService();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacTrashService();
        }

        return new UnsupportedRecycleBinService();
    }
}

public sealed class MacTrashService : IRecycleBinService
{
    public Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return Task.FromResult(new RecycleResult(path, false, "The macOS Trash is unavailable on this platform."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RecycleResult>(cancellationToken);
        }

        return Task.Run(() => RecycleCore(path), CancellationToken.None);
    }

    private static RecycleResult RecycleCore(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path);
            if (!File.Exists(normalized) && !Directory.Exists(normalized))
            {
                return new RecycleResult(normalized, false, "The target no longer exists.");
            }

            var escaped = EscapeAppleScript(normalized);
            var startInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"tell application \"Finder\" to delete POSIX file \"{escaped}\"");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new RecycleResult(normalized, false, "osascript could not be started.");
            }

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new RecycleResult(normalized, false, "The Trash operation timed out.");
            }

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                return new RecycleResult(normalized, false, string.IsNullOrWhiteSpace(error)
                    ? $"Trash operation failed with exit code {process.ExitCode}."
                    : error);
            }

            return File.Exists(normalized) || Directory.Exists(normalized)
                ? new RecycleResult(normalized, false, "Finder reported success but the file still exists.")
                : new RecycleResult(normalized, true, null);
        }
        catch (Exception ex)
        {
            return new RecycleResult(path, false, $"Trash operation failed: {ex.Message}");
        }
    }

    private static string EscapeAppleScript(string path) =>
        path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class UnsupportedRecycleBinService : IRecycleBinService
{
    public Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RecycleResult(path, false, "A recycle bin is unavailable on this platform."));
}
