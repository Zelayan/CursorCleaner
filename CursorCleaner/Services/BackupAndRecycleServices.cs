using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public sealed class BackupService : IBackupService
{
    private readonly string _backupBasePath;
    private readonly ILogService _log;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public BackupService(ILogService log, string? backupBasePath = null)
    {
        _log = log;
        _backupBasePath = Path.GetFullPath(backupBasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CursorCleanerBackup"));
    }

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
            if (GetAvailableSpace(_backupBasePath) < originalSize)
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
        Directory.CreateDirectory(_backupBasePath);
        var backupDirectory = CreateUniqueDirectory();
        var leaf = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(leaf))
        {
            throw new IOException("The database backup file name is invalid.");
        }

        var destination = Path.GetFullPath(Path.Combine(backupDirectory, leaf));
        if (!PathSafety.IsWithin(destination, backupDirectory, allowRoot: false) || File.Exists(destination))
        {
            throw new IOException("The database backup destination is unsafe.");
        }

        await TryLogAsync("info", "backup.sqlite", "Reserved SQLite online backup destination.", destination, null, cancellationToken).ConfigureAwait(false);
        return destination;
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

    private static string BuildRootFolder(CursorDataRoot root)
    {
        var leaf = Path.GetFileName(PathSafety.Normalize(root.Path));
        var safeLeaf = string.Concat(leaf.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(PathSafety.Normalize(root.Path)).ToString("X8");
        return $"{root.Kind}_{safeLeaf}_{hash}";
    }

    private static long GetAvailableSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("The backup volume could not be determined.");
        return new DriveInfo(root).AvailableFreeSpace;
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

        return Task.Run(() => RecycleCore(path), CancellationToken.None);
    }

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
