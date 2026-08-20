using System.Text.Json;
using CursorCleaner.Models;
using CursorCleaner.Services;

namespace CursorCleaner.Tests;

[TestClass]
public sealed class OperationServiceTests
{
    [TestMethod]
    public void Planner_UsesStrictCutoffAndExcludesSqliteAndOther()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var cutoff = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var old = CreateFileItem(root, "workspaceStorage/old.json", DataCategory.Workspace, cutoff.AddTicks(-1));
        var boundary = CreateFileItem(root, "workspaceStorage/boundary.json", DataCategory.Workspace, cutoff);
        var sqlite = CreateFileItem(root, "state.vscdb", DataCategory.SQLite, cutoff.AddDays(-1));
        var other = CreateFileItem(root, "cache.bin", DataCategory.Other, cutoff.AddDays(-1));

        var plan = new CleanupPlannerService(new PathGuard([root.Path])).CreatePlan(CreateScanResult(old, boundary, sqlite, other), [root.Path], cutoff);

        Assert.AreEqual(1, plan.FileCount);
        Assert.AreEqual(old.FullPath, plan.Items[0].FullPath);
        Assert.AreEqual(old.Size, plan.TotalSize);
        Assert.IsNotNull(plan.Items[0].Identity);
        Assert.AreNotEqual(Guid.Empty, plan.Id);
    }

    [TestMethod]
    public void Planner_SelectedSessionsIgnoreCutoffAndExcludeNonSessions()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var recent = CreateFileItem(root, "projects/demo/chats/recent.json", DataCategory.ChatSession, DateTime.UtcNow);
        var transcript = CreateFileItem(root, "projects/demo/agent-transcripts/run.jsonl", DataCategory.AgentTranscript, DateTime.UtcNow);
        var workspace = CreateFileItem(root, "workspaceStorage/old.json", DataCategory.Workspace, cutoff.AddDays(-1));
        var sqlite = CreateFileItem(root, "state.vscdb", DataCategory.SQLite, cutoff.AddDays(-1));
        var planner = new CleanupPlannerService(new PathGuard([root.Path]));

        var dated = planner.CreatePlan(CreateScanResult(recent, transcript, workspace, sqlite), [root.Path], cutoff);
        var selected = planner.CreateSelectedPlan(
            CreateScanResult(recent, transcript, workspace, sqlite),
            [root.Path],
            [recent.FullPath, transcript.FullPath, workspace.FullPath, sqlite.FullPath]);

        Assert.AreEqual(1, dated.FileCount);
        Assert.AreEqual(workspace.FullPath, dated.Items[0].FullPath);
        Assert.AreEqual(2, selected.FileCount);
        CollectionAssert.AreEquivalent(
            new[] { recent.FullPath, transcript.FullPath },
            selected.Items.Select(item => item.FullPath).ToArray());
        Assert.IsTrue(selected.Items.All(item => item.Identity is not null));
    }

    [TestMethod]
    public void PathGuard_RejectsRootOutsideAndSqliteCleanup()
    {
        using var temp = new TemporaryDirectory();
        var rootPath = Path.Combine(temp.Path, "Cursor");
        Directory.CreateDirectory(rootPath);
        var inside = Path.Combine(rootPath, "item.json");
        var sqlite = Path.Combine(rootPath, "state.vscdb");
        var outside = Path.Combine(temp.Path, "outside.json");
        File.WriteAllText(inside, "x");
        File.WriteAllText(sqlite, "x");
        File.WriteAllText(outside, "x");
        var guard = new PathGuard([rootPath]);

        Assert.IsFalse(guard.ValidateCleanupTarget(rootPath, [rootPath]).IsSafe);
        Assert.IsFalse(guard.ValidateCleanupTarget(outside, [rootPath]).IsSafe);
        Assert.IsFalse(guard.ValidateCleanupTarget(sqlite, [rootPath]).IsSafe);
        Assert.IsTrue(guard.ValidateCleanupTarget(inside, [rootPath]).IsSafe);
    }

    [TestMethod]
    public async Task Backup_WritesValidManifestAndCopiesRelativePath()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(Path.Combine(temp.Path, "source"));
        var item = CreateFileItem(root, "workspaceStorage/a/item.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2), "payload");
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var result = await new BackupService(log, Path.Combine(temp.Path, "backups")).BackupAsync([ToPlanItem(item)]);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsNotNull(result.BackupDirectory);
        Assert.IsTrue(File.Exists(result.Files.Single().BackupPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.BackupDirectory, "manifest.json")));
        Assert.AreEqual(item.Size, document.RootElement.GetProperty("originalSize").GetInt64());
        Assert.AreEqual("success", document.RootElement.GetProperty("files")[0].GetProperty("result").GetString());
    }

    [TestMethod]
    public async Task Cleanup_FileChangeBlocksDeletion()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "workspaceStorage/item.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var plan = new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(item)]);
        await File.AppendAllTextAsync(item.FullPath, "changed");
        var recycle = new FakeRecycleBinService();
        var service = CreateCleanupService(temp.Path, false, recycle);

        var result = await service.ExecuteAsync(plan, true, new CleanupOptions(false));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Blocked);
        Assert.IsTrue(File.Exists(item.FullPath));
        Assert.AreEqual(0, recycle.Calls);
        StringAssert.Contains(result.Error!, "changed");
    }

    [TestMethod]
    public async Task Cleanup_CursorRunningBlocksAllWork()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "workspaceStorage/item.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var recycle = new FakeRecycleBinService();
        var service = CreateCleanupService(temp.Path, true, recycle);

        var result = await service.ExecuteAsync(new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(item)]), true, new CleanupOptions(false));

        Assert.IsTrue(result.Blocked);
        Assert.IsTrue(File.Exists(item.FullPath));
        Assert.AreEqual(0, recycle.Calls);
    }

    [TestMethod]
    public async Task Cleanup_PlanReplayIsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "workspaceStorage/item.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var plan = new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(item)]);
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true);
        var service = CreateCleanupService(temp.Path, false, recycle);

        var first = await service.ExecuteAsync(plan, true, new CleanupOptions(false));
        var second = await service.ExecuteAsync(plan, true, new CleanupOptions(false));

        Assert.IsTrue(first.Succeeded);
        Assert.IsTrue(second.Blocked);
        Assert.AreEqual(1, recycle.Calls);
    }

    [TestMethod]
    public async Task Cleanup_FakeRecyclePartialFailureReportsEachItem()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var first = CreateFileItem(root, "workspaceStorage/one.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var second = CreateFileItem(root, "workspaceStorage/two.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true, failPath: second.FullPath);
        var service = CreateCleanupService(temp.Path, false, recycle);

        var result = await service.ExecuteAsync(
            new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(first), ToPlanItem(second)]),
            true,
            new CleanupOptions(false));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, result.DeletedFiles);
        Assert.IsFalse(File.Exists(first.FullPath));
        Assert.IsTrue(File.Exists(second.FullPath));
        Assert.IsFalse(result.Items.Single(item => item.Path == second.FullPath).Succeeded);
    }

    [TestMethod]
    public async Task Cleanup_ForgedPlanRootOutsideTrustedCursorRootIsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var trustedRoot = CreateRoot(temp.Path);
        var forgedRootPath = Path.Combine(temp.Path, "forged");
        Directory.CreateDirectory(forgedRootPath);
        var forgedRoot = new CursorDataRoot(forgedRootPath, RootKind.Compatibility, "Forged root");
        var item = CreateFileItem(forgedRoot, "victim.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new CleanupService(new FakeProcessService(false), new PathGuard([trustedRoot.Path]),
            new FakeBackupService(), recycle, log);

        var result = await service.ExecuteAsync(
            new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(item)]), true, new CleanupOptions(false));

        Assert.IsTrue(result.Blocked);
        Assert.IsTrue(File.Exists(item.FullPath));
        Assert.AreEqual(0, recycle.Calls);
    }

    [TestMethod]
    public async Task Cleanup_CursorBlockedPlanCanRunAfterCursorCloses()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "workspaceStorage/item.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var plan = new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(item)]);
        var process = new MutableProcessService { Running = true };
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new CleanupService(process, new PathGuard([root.Path]), new FakeBackupService(), recycle, log);

        var blocked = await service.ExecuteAsync(plan, true, new CleanupOptions(false));
        process.Running = false;
        var retried = await service.ExecuteAsync(plan, true, new CleanupOptions(false));

        Assert.IsTrue(blocked.Blocked);
        Assert.IsTrue(retried.Succeeded, retried.Error);
        Assert.AreEqual(1, recycle.Calls);
    }

    [TestMethod]
    public async Task Cleanup_EmptyAndExpiredPlansAreBlockedWithoutClaiming()
    {
        using var temp = new TemporaryDirectory();
        var service = CreateCleanupService(temp.Path, false, new FakeRecycleBinService());
        var empty = new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, []);
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "old.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var expired = new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-31), [ToPlanItem(item)]);

        var emptyResult = await service.ExecuteAsync(empty, true, new CleanupOptions(false));
        var expiredResult = await service.ExecuteAsync(expired, true, new CleanupOptions(false));

        Assert.IsTrue(emptyResult.Blocked);
        StringAssert.Contains(emptyResult.Error!, "empty");
        Assert.IsTrue(expiredResult.Blocked);
        StringAssert.Contains(expiredResult.Error!, "expired");
        Assert.IsTrue(File.Exists(item.FullPath));
    }

    [TestMethod]
    public async Task Cleanup_DisallowedCategoryIsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "cache.bin", DataCategory.Other, DateTime.UtcNow.AddDays(-2));
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true);
        var service = CreateCleanupService(temp.Path, false, recycle);

        var result = await service.ExecuteAsync(
            new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(item)]), true, new CleanupOptions(false));

        Assert.IsTrue(result.Blocked);
        StringAssert.Contains(result.Error!, "category");
        Assert.IsTrue(File.Exists(item.FullPath));
        Assert.AreEqual(0, recycle.Calls);
    }

    [TestMethod]
    public async Task Cleanup_MissingIdentityIsBlocked()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "workspaceStorage/item.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true);
        var service = CreateCleanupService(temp.Path, false, recycle);

        var result = await service.ExecuteAsync(
            new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(item) with { Identity = null }]),
            true,
            new CleanupOptions(false));

        Assert.IsTrue(result.Blocked);
        StringAssert.Contains(result.Error!, "identity");
        Assert.IsTrue(File.Exists(item.FullPath));
        Assert.AreEqual(0, recycle.Calls);
    }

    [TestMethod]
    public async Task Cleanup_AutomaticBackupUsesOneSessionForTheEntirePlan()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var first = CreateFileItem(root, "workspaceStorage/one.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var second = CreateFileItem(root, "workspaceStorage/two.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true);
        var backup = new FakeBackupService();
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new CleanupService(new FakeProcessService(false), new PathGuard([root.Path]), backup, recycle, log);

        var result = await service.ExecuteAsync(
            new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [ToPlanItem(first), ToPlanItem(second)]),
            true,
            new CleanupOptions(true));

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual(1, backup.Calls);
        Assert.AreEqual(2, backup.LastItemCount);
        Assert.AreEqual(2, result.DeletedFiles);
    }

    [TestMethod]
    public async Task Cleanup_RecordedWrongFileIdentityBlocksDeletion()
    {
        using var temp = new TemporaryDirectory();
        var root = CreateRoot(temp.Path);
        var item = CreateFileItem(root, "workspaceStorage/item.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-2));
        var planItem = ToPlanItem(item) with { Identity = new FileIdentity(uint.MaxValue, ulong.MaxValue) };
        var recycle = new FakeRecycleBinService(deleteOnSuccess: true);
        var service = CreateCleanupService(temp.Path, false, recycle);

        var result = await service.ExecuteAsync(
            new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, [planItem]), true, new CleanupOptions(false));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Blocked);
        StringAssert.Contains(result.Error!, "identity changed");
        Assert.IsTrue(File.Exists(item.FullPath));
        Assert.AreEqual(0, recycle.Calls);
    }

    [TestMethod]
    public async Task Settings_DamagedJsonRestoresDefaultsAndLogsReason()
    {
        using var temp = new TemporaryDirectory();
        var logDirectory = Path.Combine(temp.Path, "logs");
        var settingsDirectory = Path.Combine(temp.Path, "settings");
        Directory.CreateDirectory(settingsDirectory);
        await File.WriteAllTextAsync(Path.Combine(settingsDirectory, "settings.json"), "{ damaged");
        var service = new SettingsService(new LogService(logDirectory), settingsDirectory);

        var settings = await service.LoadAsync();

        Assert.AreEqual(30, settings.RetentionDays);
        Assert.IsTrue(settings.AutomaticBackup);
        Assert.IsTrue(settings.UseRecycleBin);
        Assert.IsFalse(settings.AdvancedToolsEnabled);
        Assert.IsFalse(settings.AdvancedFeaturesEnabled);
        Assert.AreEqual(CleanerTheme.System, settings.Theme);
        var log = Directory.GetFiles(logDirectory).Single();
        StringAssert.Contains(await File.ReadAllTextAsync(log), "settings.load");
    }

    private static CleanupService CreateCleanupService(string tempPath, bool cursorRunning, FakeRecycleBinService recycle)
    {
        var log = new LogService(Path.Combine(tempPath, "logs"));
        return new CleanupService(
            new FakeProcessService(cursorRunning),
            new PathGuard([Path.Combine(tempPath, "Cursor")]),
            new FakeBackupService(),
            recycle,
            log);
    }

    private static CursorDataRoot CreateRoot(string path)
    {
        var rootPath = Path.Combine(path, "Cursor");
        Directory.CreateDirectory(rootPath);
        return new CursorDataRoot(rootPath, RootKind.RoamingData, "Cursor root");
    }

    private static ScanItem CreateFileItem(
        CursorDataRoot root,
        string relativePath,
        DataCategory category,
        DateTime time,
        string content = "data")
    {
        var path = Path.Combine(root.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, time);
        var info = new FileInfo(path);
        return new ScanItem(path, relativePath, root, category, info.Length, info.LastWriteTimeUtc, info.Attributes);
    }

    private static ScanResult CreateScanResult(params ScanItem[] items) => new(
        items,
        new ScanSummary(items.Length, items.Sum(item => item.Size), new Dictionary<DataCategory, long>(),
            new Dictionary<DataCategory, long>(), [], 0, TimeSpan.Zero),
        DateTime.UtcNow);

    private static CleanupPlanItem ToPlanItem(ScanItem item, FileIdentity? identity = null)
    {
        if (identity is null)
        {
            var guard = new PathGuard([]);
            Assert.IsTrue(guard.TryGetFileIdentity(item.FullPath, out identity, out var error), error);
        }

        return new CleanupPlanItem(
            item.FullPath, item.RelativePath, item.Root, item.Category, item.Size, item.LastWriteTimeUtc, identity);
    }

    private sealed class FakeProcessService(bool running) : IProcessService
    {
        public bool IsCursorRunning() => running;
    }

    private sealed class MutableProcessService : IProcessService
    {
        public bool Running { get; set; }
        public bool IsCursorRunning() => Running;
    }

    private sealed class FakeBackupService : IBackupService
    {
        public int Calls { get; private set; }
        public int LastItemCount { get; private set; }

        public Task<BackupOperationResult> BackupAsync(IEnumerable<CleanupPlanItem> items, CancellationToken cancellationToken = default)
        {
            var snapshots = items.ToArray();
            Calls++;
            LastItemCount = snapshots.Length;
            var results = snapshots.Select(item => new BackupItemResult(
                item.FullPath, "fake-backup", item.Size, item.LastWriteTimeUtc, true, null)).ToArray();
            return Task.FromResult(new BackupOperationResult(true, "fake", results.Sum(item => item.Size), results));
        }

        public Task<string> CreateSqliteBackupPathAsync(string databasePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRecycleBinService(bool deleteOnSuccess = false, string? failPath = null) : IRecycleBinService
    {
        public int Calls { get; private set; }

        public Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (string.Equals(path, failPath, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new RecycleResult(path, false, "Injected recycle failure."));
            }

            if (deleteOnSuccess)
            {
                File.Delete(path);
            }

            return Task.FromResult(new RecycleResult(path, true, null));
        }
    }
}
