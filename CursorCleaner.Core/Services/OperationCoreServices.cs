using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public sealed class ProcessService : IProcessService
{
    private static readonly string[] CursorProcessNames =
    [
        "Cursor",
        "Cursor - Insiders",
        "Cursor-Insiders",
        "Cursor Helper",
        "Cursor Helper (GPU)",
        "Cursor Helper (Renderer)",
        "Cursor Helper (Plugin)"
    ];

    private static readonly string[] CursorAppNames =
    [
        "Cursor",
        "Cursor - Insiders",
        "Cursor-Insiders"
    ];

    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ForceWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public bool IsCursorRunning() => IsAnyCursorProcessRunning();

    public async Task<StopCursorResult> StopCursorAsync(CancellationToken cancellationToken = default)
    {
        var running = EnumerateCursorProcesses();
        if (running.Count == 0)
        {
            return new StopCursorResult(true, false, 0, null);
        }

        var initialCount = running.Count;
        DisposeAll(running);

        await TryGracefulQuitAsync(cancellationToken).ConfigureAwait(false);
        if (await WaitUntilStoppedAsync(GracefulWait, cancellationToken).ConfigureAwait(false))
        {
            return new StopCursorResult(true, true, initialCount, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var terminated = ForceKillAll(out var killErrors);
        if (await WaitUntilStoppedAsync(ForceWait, cancellationToken).ConfigureAwait(false))
        {
            return new StopCursorResult(true, true, Math.Max(terminated, 1), null);
        }

        if (!IsCursorRunning())
        {
            return new StopCursorResult(true, true, Math.Max(terminated, 1), null);
        }

        var error = killErrors.Count > 0
            ? string.Join(" ", killErrors)
            : "Cursor is still running after force stop.";
        return new StopCursorResult(false, true, terminated, error);
    }

    private static List<Process> EnumerateCursorProcesses()
    {
        var result = new List<Process>();
        foreach (var processName in CursorProcessNames)
        {
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName(processName);
                result.AddRange(processes);
                processes = [];
            }
            catch (InvalidOperationException)
            {
                DisposeAll(processes);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                DisposeAll(processes);
            }
            finally
            {
                DisposeAll(processes);
            }
        }

        return result;
    }

    private static Task TryGracefulQuitAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var appName in CursorAppNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryQuitMacApp(appName);
            }

            return Task.CompletedTask;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var processName in CursorAppNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Process[] processes = [];
                try
                {
                    processes = Process.GetProcessesByName(processName);
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.CloseMainWindow();
                            }
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                        }
                    }
                }
                finally
                {
                    DisposeAll(processes);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void TryQuitMacApp(string appName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"tell application \"{appName}\" to quit");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }
    }

    private static int ForceKillAll(out List<string> errors)
    {
        errors = [];
        var terminated = 0;
        foreach (var process in EnumerateCursorProcesses())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    terminated++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                errors.Add(ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        return terminated;
    }

    private static async Task<bool> WaitUntilStoppedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAnyCursorProcessRunning())
            {
                return true;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        return !IsAnyCursorProcessRunning();
    }

    private static bool IsAnyCursorProcessRunning()
    {
        var processes = EnumerateCursorProcesses();
        var running = processes.Count > 0;
        DisposeAll(processes);
        return running;
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}

public sealed class LogService : ILogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public LogService(string? logDirectory = null)
    {
        LogDirectory = Path.GetFullPath(logDirectory ?? AppStorage.DefaultLogs);
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
        var directory = Path.GetFullPath(settingsDirectory ?? AppStorage.DefaultRoot);
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

            if (OperatingSystem.IsWindows() && File.Exists(SettingsPath))
            {
                File.Replace(temporaryPath, SettingsPath, null);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath, overwrite: true);
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

    public static readonly DataCategory[] SessionCategories =
    [
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
        return Create(scanResult, approvedRoots, item =>
            AllowedCategories.Contains(item.Category) && item.LastWriteTimeUtc < cutoffUtc);
    }

    public CleanupPlan CreateSelectedPlan(
        ScanResult scanResult,
        IEnumerable<string> approvedRoots,
        IEnumerable<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        var selected = selectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathSafety.Normalize)
            .ToHashSet(PathSafety.PathComparer);
        if (selected.Count == 0)
        {
            return new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, []);
        }

        return Create(scanResult, approvedRoots, item =>
            SessionCategories.Contains(item.Category) &&
            selected.Contains(PathSafety.Normalize(item.FullPath)));
    }

    private CleanupPlan Create(
        ScanResult scanResult,
        IEnumerable<string> approvedRoots,
        Func<ScanItem, bool> match)
    {
        ArgumentNullException.ThrowIfNull(approvedRoots);
        var roots = approvedRoots.Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
        if (roots.Length == 0)
        {
            throw new ArgumentException("At least one approved root is required.", nameof(approvedRoots));
        }

        var items = scanResult.Items
            .Where(match)
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
    private readonly IFileIdentityService _identity;

    public PathGuard(IEnumerable<string>? cursorRoots = null, IFileIdentityService? identity = null)
    {
        CursorRoots = (cursorRoots ?? []).Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
        _identity = identity ?? FileIdentityService.CreateDefault();
    }

    public IReadOnlyList<string> CursorRoots { get; }

    public PathGuardResult ValidateCleanupTarget(string path, IEnumerable<string> approvedRoots) =>
        Validate(path, approvedRoots, requireSqlite: false);

    public PathGuardResult ValidateSqliteTarget(string path, IEnumerable<string> approvedRoots) =>
        Validate(path, approvedRoots, requireSqlite: true);

    public bool TryGetFileIdentity(string path, out FileIdentity? identity, out string? error) =>
        _identity.TryGetFileIdentity(path, out identity, out error);

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
}
