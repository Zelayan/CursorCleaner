using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public sealed class ProcessService : IProcessService
{
    private static readonly string[] CursorProcessNames = ["Cursor", "Cursor - Insiders", "Cursor-Insiders"];

    public bool IsCursorRunning()
    {
        foreach (var processName in CursorProcessNames)
        {
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName(processName);
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}

public sealed class LogService : ILogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public LogService(string? logDirectory = null)
    {
        LogDirectory = Path.GetFullPath(logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CursorCleaner",
            "logs"));
    }

    public string LogDirectory { get; }

    public async Task WriteAsync(
        string level,
        string operation,
        string message,
        string? path = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new
        {
            timestampUtc = DateTime.UtcNow,
            level,
            operation,
            message,
            path,
            error = exception?.Message,
            exceptionType = exception?.GetType().FullName
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var logPath = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
            await using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, _jsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class SettingsService : ISettingsService
{
    private readonly ILogService _log;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsService(ILogService log, string? settingsDirectory = null)
    {
        _log = log;
        var directory = Path.GetFullPath(settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CursorCleaner"));
        SettingsPath = Path.Combine(directory, "settings.json");
    }

    public string SettingsPath { get; }

    public async Task<OperationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return new OperationSettings();
        }

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync<OperationSettings>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            if (settings is null || settings.RetentionDays < 0)
            {
                throw new JsonException("Settings are empty or contain an invalid retention period.");
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            await TryLogAsync("error", "settings.load", "Settings are damaged or unreadable; defaults were restored.", SettingsPath, ex).ConfigureAwait(false);
            return new OperationSettings();
        }
    }

    public async Task SaveAsync(OperationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(SettingsPath))
            {
                File.Replace(temporaryPath, SettingsPath, null);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }
        }
        catch (Exception ex)
        {
            TryDelete(temporaryPath);
            await TryLogAsync("error", "settings.save", "Failed to save settings.", SettingsPath, ex).ConfigureAwait(false);
            throw;
        }
    }

    private async Task TryLogAsync(string level, string operation, string message, string path, Exception exception)
    {
        try
        {
            await _log.WriteAsync(level, operation, message, path, exception).ConfigureAwait(false);
        }
        catch
        {
            // Settings recovery must still work if the log destination is unavailable.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

public sealed class CleanupPlannerService : ICleanupPlannerService
{
    public static readonly DataCategory[] AllowedCategories =
    [
        DataCategory.Workspace,
        DataCategory.ChatSession,
        DataCategory.AgentTranscript
    ];

    private readonly IPathGuard _pathGuard;

    public CleanupPlannerService(IPathGuard pathGuard)
    {
        _pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
    }

    public CleanupPlan CreatePlan(ScanResult scanResult, IEnumerable<string> approvedRoots, DateTime cutoffUtc)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentNullException.ThrowIfNull(approvedRoots);
        var roots = approvedRoots.Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
        if (roots.Length == 0)
        {
            throw new ArgumentException("At least one approved root is required.", nameof(approvedRoots));
        }

        var items = scanResult.Items
            .Where(item => AllowedCategories.Contains(item.Category))
            .Where(item => item.LastWriteTimeUtc < cutoffUtc)
            .Where(item => item.Size >= 0)
            .Where(item => roots.Any(root => IsSafeFileSnapshot(item, root)))
            .GroupBy(item => PathSafety.Normalize(item.FullPath), PathSafety.PathComparer)
            .Select(group => group.First())
            .Select(TryCreatePlanItem)
            .OfType<CleanupPlanItem>()
            .ToArray();

        return new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, items);
    }

    private CleanupPlanItem? TryCreatePlanItem(ScanItem item)
    {
        var path = PathSafety.Normalize(item.FullPath);
        if (!_pathGuard.TryGetFileIdentity(path, out var identity, out _) || identity is null)
        {
            return null;
        }

        return new CleanupPlanItem(
            path,
            item.RelativePath,
            item.Root with { Path = PathSafety.Normalize(item.Root.Path) },
            item.Category,
            item.Size,
            item.LastWriteTimeUtc,
            identity);
    }

    private static bool IsSafeFileSnapshot(ScanItem item, string approvedRoot)
    {
        try
        {
            var path = PathSafety.Normalize(item.FullPath);
            var scanRoot = PathSafety.Normalize(item.Root.Path);
            return !PathSafety.PathComparer.Equals(path, approvedRoot)
                && !PathSafety.PathComparer.Equals(path, scanRoot)
                && PathSafety.IsWithin(path, approvedRoot, allowRoot: false)
                && PathSafety.IsWithin(path, scanRoot, allowRoot: false)
                && !item.Attributes.HasFlag(FileAttributes.Directory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed class PathGuard : IPathGuard
{
    private static readonly string[] SqliteExtensions = [".vscdb", ".db", ".sqlite"];

    public PathGuard(IEnumerable<string>? cursorRoots = null)
    {
        CursorRoots = (cursorRoots ?? []).Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
    }

    public IReadOnlyList<string> CursorRoots { get; }

    public PathGuardResult ValidateCleanupTarget(string path, IEnumerable<string> approvedRoots) =>
        Validate(path, approvedRoots, requireSqlite: false);

    public PathGuardResult ValidateSqliteTarget(string path, IEnumerable<string> approvedRoots) =>
        Validate(path, approvedRoots, requireSqlite: true);

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

    private PathGuardResult Validate(string path, IEnumerable<string> approvedRoots, bool requireSqlite)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(approvedRoots);
            var normalized = PathSafety.Normalize(path);
            var requestedRoots = approvedRoots.Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
            var roots = requestedRoots.Where(requested => CursorRoots.Contains(requested, PathSafety.PathComparer)).ToArray();
            if (roots.Length == 0 || !roots.Any(root => PathSafety.IsWithin(normalized, root, allowRoot: false)))
            {
                return Reject("The target is outside the trusted Cursor roots or its root was not approved.");
            }

            if (!File.Exists(normalized))
            {
                return Reject("The target is not an existing file.");
            }

            if (HasReparsePoint(normalized, roots))
            {
                return Reject("Reparse points are not allowed in the target path.");
            }

            var extension = GetDatabaseExtension(normalized);
            if (requireSqlite && extension is null)
            {
                return Reject("Only .vscdb, .db, and .sqlite database files are allowed.");
            }

            if (!requireSqlite && (extension is not null || IsSidecar(normalized)))
            {
                return Reject("SQLite databases and sidecars cannot be cleaned as ordinary files.");
            }

            return new PathGuardResult(true, normalized, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Reject($"Path validation failed: {ex.Message}");
        }
    }

    private static bool HasReparsePoint(string path, IReadOnlyList<string> roots)
    {
        var containingRoot = roots.Where(root => PathSafety.IsWithin(path, root, allowRoot: false)).OrderByDescending(root => root.Length).First();
        var current = path;
        while (PathSafety.IsWithin(current, containingRoot))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (PathSafety.PathComparer.Equals(current, containingRoot))
            {
                break;
            }

            current = Path.GetDirectoryName(current) ?? throw new IOException("The target ancestry could not be traversed.");
        }

        return false;
    }

    private static string? GetDatabaseExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return SqliteExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? extension : null;
    }

    private static bool IsSidecar(string path) => path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase);

    private static PathGuardResult Reject(string error) => new(false, null, error);

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
