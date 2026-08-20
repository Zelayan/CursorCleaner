using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public sealed class CleanupService : ICleanupService
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(30);
    private readonly IProcessService _processService;
    private readonly IPathGuard _pathGuard;
    private readonly IBackupService _backupService;
    private readonly IRecycleBinService _recycleBinService;
    private readonly ILogService _log;
    private readonly ConcurrentDictionary<Guid, byte> _claimedPlans = new();

    public CleanupService(
        IProcessService processService,
        IPathGuard pathGuard,
        IBackupService backupService,
        IRecycleBinService recycleBinService,
        ILogService log)
    {
        _processService = processService;
        _pathGuard = pathGuard;
        _backupService = backupService;
        _recycleBinService = recycleBinService;
        _log = log;
    }

    public async Task<CleanupOperationResult> ExecuteAsync(
        CleanupPlan plan,
        bool confirmed,
        CleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        if (!confirmed)
        {
            return Block(plan.Id, "Cleanup requires explicit confirmation.");
        }

        if (plan.Items.Count == 0)
        {
            return Block(plan.Id, "An empty cleanup plan cannot be executed.");
        }

        var age = DateTime.UtcNow - plan.CreatedAtUtc;
        if (age < TimeSpan.Zero || age > PlanLifetime)
        {
            return Block(plan.Id, "The cleanup plan has expired; create a new preview.");
        }

        var initialIdentities = new Dictionary<string, FileIdentity>(PathSafety.PathComparer);
        var authorizedItems = new List<CleanupPlanItem>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            if (!CleanupPlannerService.AllowedCategories.Contains(item.Category))
            {
                return Block(plan.Id, "A cleanup plan contains a category that cannot be deleted.");
            }

            if (item.Identity is null)
            {
                return Block(plan.Id, "A cleanup plan item is missing the scan-time file identity.");
            }

            if (!_pathGuard.CursorRoots.Contains(PathSafety.Normalize(item.Root.Path), PathSafety.PathComparer))
            {
                return Block(plan.Id, "A cleanup plan root is not an exact trusted Cursor root.");
            }

            var guard = _pathGuard.ValidateCleanupTarget(item.FullPath, [item.Root.Path]);
            if (!guard.IsSafe)
            {
                return Block(plan.Id, guard.Error ?? "Cleanup plan authorization failed.");
            }

            var path = guard.NormalizedPath!;
            if (!MatchesSnapshot(path, item, out var changedReason)
                || !TryCaptureIdentity(path, item.Identity, out var initialIdentity, out changedReason)
                || initialIdentity is null)
            {
                return Block(plan.Id, changedReason ?? "Cleanup plan snapshot verification failed.");
            }

            initialIdentities[path] = initialIdentity;
            authorizedItems.Add(item with { FullPath = path, Identity = initialIdentity });
        }

        if (_processService.IsCursorRunning())
        {
            await TryLogAsync("error", "cleanup.blocked", "Cursor is running; no cleanup was performed.", null, null).ConfigureAwait(false);
            return Block(plan.Id, "Cursor is running; close Cursor before cleaning.");
        }

        if (!_claimedPlans.TryAdd(plan.Id, 0))
        {
            return Block(plan.Id, "This cleanup plan has already begun execution.");
        }

        var backupsByPath = new Dictionary<string, BackupItemResult>(PathSafety.PathComparer);
        if (options.AutomaticBackup)
        {
            var backup = await _backupService.BackupAsync(authorizedItems, cancellationToken).ConfigureAwait(false);
            foreach (var backedUpItem in backup.Files)
            {
                backupsByPath[PathSafety.Normalize(backedUpItem.OriginalPath)] = backedUpItem;
            }

            if (!backup.Succeeded && backup.Files.Count == 0)
            {
                return new CleanupOperationResult(plan.Id, false, true, false, [], backup.Error ?? "Backup failed; no files were deleted.");
            }
        }

        var results = new List<CleanupItemResult>(authorizedItems.Count);
        foreach (var item in authorizedItems)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new CleanupOperationResult(plan.Id, false, false, true, results, "Cleanup was cancelled before the next file.");
            }

            var guard = _pathGuard.ValidateCleanupTarget(item.FullPath, [item.Root.Path]);
            if (!guard.IsSafe)
            {
                await AddFailureAsync(results, item.FullPath, guard.Error ?? "Path validation failed.").ConfigureAwait(false);
                continue;
            }

            var path = guard.NormalizedPath!;
            if (!MatchesSnapshot(path, item, out var changedReason)
                || !initialIdentities.TryGetValue(path, out var initialIdentity)
                || !TryCaptureIdentity(path, initialIdentity, out var executionIdentity, out changedReason)
                || executionIdentity is null)
            {
                await AddFailureAsync(results, path, changedReason ?? "Cleanup plan snapshot verification failed.").ConfigureAwait(false);
                continue;
            }

            string? backupPath = null;
            if (options.AutomaticBackup)
            {
                if (!backupsByPath.TryGetValue(path, out var backedUpItem) || !backedUpItem.Succeeded)
                {
                    await AddFailureAsync(results, path, backedUpItem?.Error ?? "Backup failed; the file was not deleted.").ConfigureAwait(false);
                    continue;
                }

                backupPath = backedUpItem.BackupPath;
            }

            if (_processService.IsCursorRunning())
            {
                const string runningError = "Cursor started during cleanup; remaining destructive operations were stopped.";
                await AddFailureAsync(results, path, runningError, backupPath).ConfigureAwait(false);
                return new CleanupOperationResult(plan.Id, false, true, false, results, runningError);
            }

            guard = _pathGuard.ValidateCleanupTarget(path, [item.Root.Path]);
            if (!guard.IsSafe
                || !MatchesSnapshot(path, item, out changedReason)
                || !TryCaptureIdentity(path, executionIdentity, out _, out changedReason))
            {
                await AddFailureAsync(results, path, guard.Error ?? changedReason ?? "The file changed after backup.", backupPath).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (options.Disposition == CleanupDisposition.Recycle)
                {
                    var recycle = await _recycleBinService.RecycleAsync(path, cancellationToken).ConfigureAwait(false);
                    if (!recycle.Succeeded)
                    {
                        await AddFailureAsync(results, path, recycle.Error ?? "Recycle operation failed.", backupPath).ConfigureAwait(false);
                        continue;
                    }
                }
                else
                {
                    var finalGuard = _pathGuard.ValidateCleanupTarget(path, [item.Root.Path]);
                    if (!finalGuard.IsSafe
                        || !MatchesSnapshot(path, item, out changedReason)
                        || !TryCaptureIdentity(path, executionIdentity, out _, out changedReason))
                    {
                        await AddFailureAsync(results, path, finalGuard.Error ?? changedReason ?? "Final file verification failed.", backupPath).ConfigureAwait(false);
                        continue;
                    }

                    File.Delete(path);
                    if (File.Exists(path))
                    {
                        throw new IOException("The file still exists after permanent deletion.");
                    }
                }

                results.Add(new CleanupItemResult(path, true, item.Size, null, backupPath));
                await TryLogAsync("info", "cleanup.file", "File cleaned successfully.", path, null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                await AddFailureAsync(results, path, ex.Message, backupPath, ex).ConfigureAwait(false);
            }
        }

        return new CleanupOperationResult(plan.Id, results.All(result => result.Succeeded), false, false, results,
            results.Any(result => !result.Succeeded) ? "Cleanup completed with one or more failures." : null);
    }

    private bool TryCaptureIdentity(string path, FileIdentity? expected, out FileIdentity? actual, out string? error)
    {
        if (!_pathGuard.TryGetFileIdentity(path, out actual, out error))
        {
            return false;
        }

        if (expected is not null && actual != expected)
        {
            error = "The file identity changed after validation.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool MatchesSnapshot(string path, CleanupPlanItem item, out string? error)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "The file no longer exists.";
                return false;
            }

            if (info.Length != item.Size || info.LastWriteTimeUtc != item.LastWriteTimeUtc)
            {
                error = "The file size or modification time changed after the plan was created.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The file snapshot could not be verified: {ex.Message}";
            return false;
        }
    }

    private async Task AddFailureAsync(List<CleanupItemResult> results, string path, string error, string? backupPath = null, Exception? exception = null)
    {
        results.Add(new CleanupItemResult(path, false, 0, error, backupPath));
        await TryLogAsync("error", "cleanup.file", error, path, exception).ConfigureAwait(false);
    }

    private async Task TryLogAsync(string level, string operation, string message, string? path, Exception? exception)
    {
        try { await _log.WriteAsync(level, operation, message, path, exception).ConfigureAwait(false); } catch { }
    }

    private static CleanupOperationResult Block(Guid planId, string error) => new(planId, false, true, false, [], error);
}

public sealed class ShellService : IShellService
{
    private readonly ILogService _log;

    public ShellService(ILogService log)
    {
        _log = log;
    }

    public void OpenDirectory(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"Directory not found: {normalized}");
        }

        StartExplorer(normalized);
    }

    public void SelectFile(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!File.Exists(normalized))
        {
            throw new FileNotFoundException("File not found.", normalized);
        }

        StartExplorer($"/select,\"{normalized}\"");
    }

    public void OpenLogs()
    {
        Directory.CreateDirectory(_log.LogDirectory);
        StartExplorer(_log.LogDirectory);
    }

    private static void StartExplorer(string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true
        });
    }
}
