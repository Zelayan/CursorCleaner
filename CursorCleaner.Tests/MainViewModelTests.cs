using System.Collections;
using System.Collections.Specialized;
using CursorCleaner.Helpers;
using CursorCleaner.Models;
using CursorCleaner.Services;
using CursorCleaner.ViewModels;

namespace CursorCleaner.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    [TestMethod]
    public async Task SuccessfulScan_RefreshesPreviewCommandsAndRequiresSelection()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.RoamingData, "test");
            var sessionItem = Item(root, "projects/p/session.json", DataCategory.ChatSession);
            var workspaceItem = Item(root, "workspaceStorage/w/data.json", DataCategory.Workspace);
            var result = Result(sessionItem, workspaceItem);
            var session = new SessionInfo("s", sessionItem.FullPath, "session", "p", DataCategory.ChatSession, 10, DateTime.UtcNow.AddDays(-40));
            var workspace = new WorkspaceInfo("w", Path.Combine(root.Path, "workspaceStorage", "w"), null, "w", 1, 10, DateTime.UtcNow.AddDays(-40), true);
            var store = new ScanResultStore();
            using var viewModel = CreateViewModel(new FixedScanner(result), store, new FixedWorkspaceAnalyzer([workspace]), new FixedSessionAnalyzer([session]));
            var sessions = new ArrayList { session };
            var workspaces = new ArrayList { workspace };
            var allPreviewChanges = 0;
            var deleteChanges = 0;
            var workspacePreviewChanges = 0;
            viewModel.GeneratePreviewCommand.CanExecuteChanged += (_, _) => allPreviewChanges++;
            viewModel.DeleteSelectedSessionsCommand.CanExecuteChanged += (_, _) => deleteChanges++;
            viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecuteChanged += (_, _) => workspacePreviewChanges++;

            Assert.IsFalse(viewModel.GeneratePreviewCommand.CanExecute(null));
            Assert.IsFalse(viewModel.DeleteSelectedSessionsCommand.CanExecute(sessions));
            Assert.IsFalse(viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecute(workspaces));

            await viewModel.ScanForTestingAsync();

            Assert.AreSame(result, store.Latest);
            Assert.IsTrue(viewModel.GeneratePreviewCommand.CanExecute(null));
            Assert.IsFalse(viewModel.DeleteSelectedSessionsCommand.CanExecute(new ArrayList()));
            Assert.IsFalse(viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecute(new ArrayList()));
            Assert.IsTrue(viewModel.DeleteSelectedSessionsCommand.CanExecute(sessions));
            Assert.IsTrue(viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecute(workspaces));
            Assert.IsTrue(allPreviewChanges > 0);
            Assert.IsTrue(deleteChanges > 0);
            Assert.IsTrue(workspacePreviewChanges > 0);
        });
    }

    [TestMethod]
    public async Task AnalyzerFailure_RetainsPreviousStoreAndCollectionsAtomically()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.RoamingData, "test");
            var previous = Result(Item(root, "projects/old/session.json", DataCategory.ChatSession));
            var next = Result(Item(root, "projects/new/session.json", DataCategory.ChatSession));
            var store = new ScanResultStore();
            store.Set(previous);
            using var viewModel = CreateViewModel(new FixedScanner(next), store, new ThrowingWorkspaceAnalyzer(), new FixedSessionAnalyzer([]));
            viewModel.ScanItems.Add(previous.Items[0]);

            await viewModel.ScanForTestingAsync();

            Assert.AreSame(previous, store.Latest);
            Assert.AreEqual(1, viewModel.ScanItems.Count);
            Assert.AreEqual(previous.Items[0].FullPath, viewModel.ScanItems[0].FullPath);
            StringAssert.Contains(viewModel.CurrentScanPath, "保留上一次完整扫描结果");
            Assert.IsFalse(viewModel.GeneratePreviewCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public async Task InitializeAsync_AutoScansAndSummarizesStaleSessions()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.RoamingData, "test");
            var oldItem = Item(root, "projects/p/old.json", DataCategory.ChatSession, DateTime.UtcNow.AddDays(-40));
            var recentItem = Item(root, "projects/p/recent.json", DataCategory.ChatSession, DateTime.UtcNow);
            var workspaceItem = Item(root, "workspaceStorage/w/data.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-40));
            var sqliteItem = Item(root, "state.vscdb", DataCategory.SQLite, DateTime.UtcNow.AddDays(-40));
            var oldSession = new SessionInfo("old", oldItem.FullPath, "旧会话", "p", DataCategory.ChatSession, 10, DateTime.UtcNow.AddDays(-40));
            var recentSession = new SessionInfo("recent", recentItem.FullPath, "最近会话", "p", DataCategory.ChatSession, 10, DateTime.UtcNow);
            var scanner = new RecordingScanner(Result(oldItem, recentItem, workspaceItem, sqliteItem), Result(oldItem, recentItem, workspaceItem, sqliteItem));
            using var viewModel = CreateViewModel(
                scanner,
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([oldSession, recentSession]));

            await viewModel.InitializeAsync();

            Assert.AreEqual(1, scanner.ScanCalls);
            Assert.IsTrue(viewModel.HasScanResult);
            Assert.AreEqual(30, viewModel.RetentionDays);
            Assert.IsTrue(viewModel.UseRetention30);
            Assert.AreEqual(1, viewModel.StaleSessionCount);
            Assert.AreEqual(1, viewModel.StaleSessionFileCount);
            Assert.AreEqual(10, viewModel.StaleSessionBytes);
            StringAssert.Contains(viewModel.StaleCleanupSummaryText, "30 天前");
            StringAssert.Contains(viewModel.StaleCleanupSummaryText, "可从 Cursor 数据中移除");
            Assert.AreEqual("清理 30 天前的数据", viewModel.CleanupRetentionButtonText);
            Assert.IsTrue(viewModel.CleanupStaleSessionsCommand.CanExecute(null));
            Assert.IsFalse(viewModel.AdvancedFeaturesEnabled);

            viewModel.RetentionDays = 90;
            Assert.IsFalse(viewModel.HasStaleSessions);
            Assert.IsFalse(viewModel.CleanupStaleSessionsCommand.CanExecute(null));
            StringAssert.Contains(viewModel.StaleCleanupSummaryText, "90 天前没有可清理的旧会话");

            viewModel.RetentionDays = 7;
            Assert.IsTrue(viewModel.HasStaleSessions);
            Assert.AreEqual("清理 7 天前的数据", viewModel.CleanupRetentionButtonText);
            Assert.IsTrue(viewModel.CleanupStaleSessionsCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public async Task CleanupStaleSessions_ExcludesWorkspaceAndRequiresConfirmation()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.RoamingData, "test");
            var oldItem = Item(root, "projects/p/old.json", DataCategory.ChatSession, DateTime.UtcNow.AddDays(-40));
            var transcriptItem = Item(root, "projects/p/agent-transcripts/run.jsonl", DataCategory.AgentTranscript, DateTime.UtcNow.AddDays(-40));
            var workspaceItem = Item(root, "workspaceStorage/w/data.json", DataCategory.Workspace, DateTime.UtcNow.AddDays(-40));
            var sqliteItem = Item(root, "state.vscdb", DataCategory.SQLite, DateTime.UtcNow.AddDays(-40));
            var otherItem = Item(root, "cache.bin", DataCategory.Other, DateTime.UtcNow.AddDays(-40));
            var oldSession = new SessionInfo("old", oldItem.FullPath, "旧会话", "p", DataCategory.ChatSession, 10, DateTime.UtcNow.AddDays(-40));
            var transcript = new SessionInfo("t", transcriptItem.FullPath, "转录", "p", DataCategory.AgentTranscript, 10, DateTime.UtcNow.AddDays(-40));
            var scanner = new RecordingScanner(Result(oldItem, transcriptItem, workspaceItem, sqliteItem, otherItem), Result(oldItem, transcriptItem, workspaceItem, sqliteItem, otherItem));
            var cleanup = new RecordingCleanupService();
            var planner = new CapturingPlanner();
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                scanner,
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([oldSession, transcript]),
                planner: planner,
                cleanup: cleanup,
                dialogs: dialogs);

            await viewModel.ScanForTestingAsync();
            await viewModel.CleanupStaleSessionsCommand.ExecuteAsync();

            CollectionAssert.AreEquivalent(new[] { oldItem.FullPath, transcriptItem.FullPath }, planner.LastSelectedPaths.ToArray());
            Assert.IsFalse(planner.LastSelectedPaths.Contains(workspaceItem.FullPath));
            Assert.IsFalse(planner.LastSelectedPaths.Contains(sqliteItem.FullPath));
            Assert.IsFalse(planner.LastSelectedPaths.Contains(otherItem.FullPath));
            StringAssert.Contains(dialogs.LastTitle!, "确认清理 30 天前的数据");
            StringAssert.Contains(dialogs.LastMessage!, "可从 Cursor 数据中移除");
            StringAssert.Contains(dialogs.LastMessage!, "工作区、SQLite 文件和其他缓存不纳入本次清理");
            StringAssert.Contains(dialogs.LastMessage!, "清空后才会释放磁盘空间");
            Assert.IsTrue(cleanup.LastConfirmed);
            Assert.AreEqual(2, scanner.ScanCalls);
        });
    }

    [TestMethod]
    public async Task CleanupStaleSessions_CancelConfirmDoesNotWrite()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var oldPath = Path.Combine(rootPath, "old.json");
            await File.WriteAllTextAsync(oldPath, "session");
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-40));
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var oldItem = Item(root, "old.json", DataCategory.ChatSession, DateTime.UtcNow.AddDays(-40));
            var oldSession = new SessionInfo("old", oldItem.FullPath, "旧会话", "p", DataCategory.ChatSession, 10, DateTime.UtcNow.AddDays(-40));
            var scanner = new RecordingScanner(Result(oldItem), Result(oldItem));
            var cleanup = new RecordingCleanupService();
            var sqlite = new RecordingSqliteService();
            var dialogs = new FixedDialogService(false);
            using var viewModel = CreateViewModel(
                scanner,
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([oldSession]),
                cleanup: cleanup,
                dialogs: dialogs,
                pathService: new FixedPathService(root),
                sqlite: sqlite);

            await viewModel.ScanForTestingAsync();
            await viewModel.CleanupStaleSessionsCommand.ExecuteAsync();

            Assert.IsNull(cleanup.LastPlan);
            Assert.AreEqual(0, sqlite.LastIds.Count);
            Assert.AreEqual(1, scanner.ScanCalls);
            StringAssert.Contains(dialogs.LastTitle!, "确认清理");
        });
    }

    [TestMethod]
    public async Task CleanupStaleSessions_CursorRunningDoesNotClean()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var oldPath = Path.Combine(rootPath, "old.json");
            await File.WriteAllTextAsync(oldPath, "session");
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-40));
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var oldItem = Item(root, "old.json", DataCategory.ChatSession, DateTime.UtcNow.AddDays(-40));
            var oldSession = new SessionInfo("old", oldItem.FullPath, "旧会话", "p", DataCategory.ChatSession, 10, DateTime.UtcNow.AddDays(-40));
            var cleanup = new RecordingCleanupService();
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                new FixedScanner(Result(oldItem)),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([oldSession]),
                cleanup: cleanup,
                dialogs: dialogs,
                pathService: new FixedPathService(root),
                process: new FixedProcessService(true));

            await viewModel.ScanForTestingAsync();
            await viewModel.CleanupStaleSessionsCommand.ExecuteAsync();

            Assert.IsNull(cleanup.LastPlan);
            Assert.AreEqual("无法清理旧数据", dialogs.LastTitle);
            StringAssert.Contains(dialogs.LastMessage!, "Cursor 正在运行");
        });
    }

    [TestMethod]
    public async Task DeleteSelectedSessions_SuccessfulSqliteSuggestsOptimizeWithoutAdvancedTools()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var databasePath = Path.Combine(rootPath, "state.vscdb");
            await File.WriteAllTextAsync(databasePath, "db");
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var database = Item(root, "state.vscdb", DataCategory.SQLite, DateTime.UtcNow);
            var session = new SessionInfo(
                "488ef4de-7b32-4b7c-b7be-6b67203f8717",
                string.Empty,
                "Greeting conversation",
                "empty-window",
                DataCategory.ChatSession,
                0,
                DateTime.UtcNow.AddDays(-40),
                SessionSource.Database,
                databasePath,
                ["488ef4de-7b32-4b7c-b7be-6b67203f8717"]);
            var sqlite = new RecordingSqliteService();
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                new RecordingScanner(Result(database), Result(database)),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([session]),
                cleanup: new RecordingCleanupService(),
                dialogs: dialogs,
                pathService: new FixedPathService(root),
                sqlite: sqlite);

            await viewModel.ScanForTestingAsync();
            Assert.IsFalse(viewModel.AdvancedToolsEnabled);
            await viewModel.DeleteSelectedSessionsCommand.ExecuteAsync(new ArrayList { session });

            Assert.IsTrue(viewModel.SuggestDatabaseOptimize);
            Assert.IsTrue(viewModel.OptimizeSuggestedDatabaseCommand.CanExecute(null));
            StringAssert.Contains(dialogs.LastMessage!, "可选择优化数据库");
            Assert.IsFalse(viewModel.VacuumCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public async Task CleanupStaleSessions_SqliteNoticeSummarizesManyDatabases()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var sessionPath = Path.Combine(rootPath, "old.json");
            await File.WriteAllTextAsync(sessionPath, "session");
            File.SetLastWriteTimeUtc(sessionPath, DateTime.UtcNow.AddDays(-40));
            var databases = new List<ScanItem>();
            for (var index = 0; index < 8; index++)
            {
                var relative = Path.Combine("workspaceStorage", $"w{index}", "state.vscdb");
                var fullPath = Path.Combine(rootPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, new string('d', 40 + index));
                databases.Add(Item(root, relative.Replace('\\', '/'), DataCategory.SQLite, DateTime.UtcNow.AddDays(-40)));
            }

            var searchPath = Path.Combine(rootPath, "conversation-search.db");
            await File.WriteAllTextAsync(searchPath, "search");
            databases.Add(Item(root, "conversation-search.db", DataCategory.SQLite, DateTime.UtcNow.AddDays(-40)));
            var sessionItem = Item(root, "old.json", DataCategory.ChatSession, DateTime.UtcNow.AddDays(-40));
            var session = new SessionInfo(
                "488ef4de-7b32-4b7c-b7be-6b67203f8717",
                sessionItem.FullPath,
                "旧会话",
                "p",
                DataCategory.ChatSession,
                10,
                DateTime.UtcNow.AddDays(-40),
                SessionSource.Both,
                databases[0].FullPath,
                ["488ef4de-7b32-4b7c-b7be-6b67203f8717"]);
            var dialogs = new FixedDialogService(false);
            var items = new[] { sessionItem }.Concat(databases).ToArray();
            using var viewModel = CreateViewModel(
                new FixedScanner(Result(items)),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([session]),
                cleanup: new RecordingCleanupService(),
                dialogs: dialogs,
                pathService: new FixedPathService(root));

            await viewModel.ScanForTestingAsync();
            await viewModel.CleanupStaleSessionsCommand.ExecuteAsync();

            StringAssert.Contains(dialogs.LastMessage!, "9 个聊天数据库");
            StringAssert.Contains(dialogs.LastMessage!, "8 个 state.vscdb");
            StringAssert.Contains(dialogs.LastMessage!, "1 个 conversation-search.db");
            Assert.IsFalse(dialogs.LastMessage!.Contains("state.vscdb 约", StringComparison.Ordinal));
            Assert.IsTrue(dialogs.LastMessage!.Split("state.vscdb", StringSplitOptions.None).Length <= 3);
        });
    }

    [TestMethod]
    public async Task EmptyPreview_DoesNotEnableCleanup_AndSettingsBecomeDirty()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.RoamingData, "test");
            var recent = Item(root, "projects/p/recent.json", DataCategory.ChatSession, DateTime.UtcNow);
            using var viewModel = CreateViewModel(new FixedScanner(Result(recent)), new ScanResultStore(), new FixedWorkspaceAnalyzer([]), new FixedSessionAnalyzer([]));
            await viewModel.InitializeAsync();
            await viewModel.ScanForTestingAsync();

            viewModel.GeneratePreviewCommand.Execute(null);

            Assert.IsFalse(viewModel.HasCleanupPlan);
            Assert.IsFalse(viewModel.CleanupCommand.CanExecute(null));
            Assert.IsFalse(viewModel.IsSettingsDirty);
            viewModel.RetentionDays = 7;
            Assert.IsTrue(viewModel.IsSettingsDirty);
            Assert.IsTrue(viewModel.SaveSettingsCommand.CanExecute(null));
            StringAssert.Contains(viewModel.SettingsStatus, "未保存");
        });
    }

    [TestMethod]
    public async Task Preview_NavigatesToSpaceAndUsesSelectedPolicy()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.RoamingData, "test");
            var old = Item(root, "projects/p/old.json", DataCategory.ChatSession, DateTime.UtcNow.AddDays(-60));
            var planner = new CapturingPlanner();
            using var viewModel = CreateViewModel(new FixedScanner(Result(old)), new ScanResultStore(), new FixedWorkspaceAnalyzer([]), new FixedSessionAnalyzer([]), planner: planner);
            await viewModel.ScanForTestingAsync();
            viewModel.SelectedPage = 1;
            viewModel.CleanupPolicyMode = CleanupPolicyMode.CutoffDate;
            viewModel.CustomCutoff = new DateTime(2025, 1, 15);

            viewModel.GeneratePreviewCommand.Execute(null);

            Assert.AreEqual(3, viewModel.SelectedPage);
            Assert.IsTrue(viewModel.IsCutoffPolicy);
            Assert.IsFalse(viewModel.IsRetentionPolicy);
            Assert.AreEqual(viewModel.CustomCutoff.Value.Date.ToUniversalTime(), planner.CutoffUtc);
            Assert.AreEqual(StatusSeverity.Normal, viewModel.OperationStatusSeverity);
        });
    }

    [TestMethod]
    public async Task SaveDuringEdit_LeavesSettingsDirty()
    {
        await RunStaAsync(async () =>
        {
            var service = new BlockingSettingsService();
            using var viewModel = CreateViewModel(new FixedScanner(Result()), new ScanResultStore(), new FixedWorkspaceAnalyzer([]), new FixedSessionAnalyzer([]), settings: service);
            await viewModel.InitializeAsync();
            viewModel.RetentionDays = 7;

            var save = viewModel.SaveSettingsCommand.ExecuteAsync();
            await service.SaveStarted.Task;
            viewModel.AutomaticBackup = false;
            service.AllowSave.SetResult();
            await save;

            Assert.IsTrue(viewModel.IsSettingsDirty);
            Assert.AreEqual(StatusSeverity.Warning, viewModel.SettingsStatusSeverity);
            StringAssert.Contains(viewModel.SettingsStatus, "仍有未保存");
        });
    }

    [TestMethod]
    public async Task CloseWithUnsavedSettingsNo_RestoresCloseState()
    {
        await RunStaAsync(async () =>
        {
            var dialogs = new FixedDialogService(false);
            using var viewModel = CreateViewModel(new FixedScanner(Result()), new ScanResultStore(), new FixedWorkspaceAnalyzer([]), new FixedSessionAnalyzer([]), dialogs: dialogs);
            await viewModel.InitializeAsync();
            viewModel.RetentionDays = 7;

            var result = await viewModel.RequestCloseAsync();

            Assert.IsFalse(result);
            Assert.IsFalse(viewModel.IsClosing);
            Assert.IsTrue(viewModel.SaveSettingsCommand.CanExecute(null));
            Assert.AreEqual("放弃未保存更改并关闭？", dialogs.LastMessage);
        });
    }

    [TestMethod]
    public async Task ScanUsesSingleResetAndTracksVisibleCounts()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.RoamingData, "test");
            var session = new SessionInfo("s", Path.Combine(root.Path, "b.json"), "会话", "p", DataCategory.ChatSession, 10, DateTime.UtcNow);
            using var viewModel = CreateViewModel(
                new FixedScanner(Result(
                    Item(root, "a.db", DataCategory.SQLite),
                    Item(root, "b.json", DataCategory.ChatSession))),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([session]));
            var resetCount = 0;
            ((INotifyCollectionChanged)viewModel.ScanItems).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) resetCount++;
            };

            await viewModel.ScanForTestingAsync();

            Assert.AreEqual(1, resetCount);
            Assert.AreEqual(2, viewModel.ScanVisibleCount);
            Assert.AreEqual(1, viewModel.SessionVisibleCount);
            Assert.AreEqual("会话", viewModel.Sessions[0].Title);
            Assert.IsTrue(viewModel.HasScanResult);
            Assert.IsFalse(viewModel.AdvancedFeaturesEnabled);
        });
    }

    [TestMethod]
    public async Task CancelledCleanup_RescansWithLiveToken()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var oldPath = Path.Combine(rootPath, "old.json");
            await File.WriteAllTextAsync(oldPath, "session");
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-60));
            var remainingPath = Path.Combine(rootPath, "remaining.json");
            await File.WriteAllTextAsync(remainingPath, "keep");
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var oldItem = Item(root, "old.json", DataCategory.ChatSession, DateTime.UtcNow.AddDays(-60));
            var remaining = Item(root, "remaining.json", DataCategory.ChatSession, DateTime.UtcNow);
            var scanner = new RecordingScanner(Result(oldItem), Result(remaining));
            var cleanup = new CancelledCleanupService();
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(scanner, new ScanResultStore(), new FixedWorkspaceAnalyzer([]), new FixedSessionAnalyzer([]), cleanup: cleanup, dialogs: dialogs, pathService: new FixedPathService(root));

            await viewModel.ScanForTestingAsync();
            viewModel.GeneratePreviewCommand.Execute(null);
            Assert.IsTrue(viewModel.HasCleanupPlan);

            await viewModel.CleanupCommand.ExecuteAsync();

            Assert.AreEqual(2, scanner.ScanCalls);
            Assert.IsFalse(scanner.LastTokenCancelled);
            Assert.AreEqual(1, viewModel.ScanItems.Count);
            Assert.AreEqual(remaining.FullPath, viewModel.ScanItems[0].FullPath);
            StringAssert.Contains(viewModel.OperationStatus, "清理已取消");
        });
    }

    [TestMethod]
    public async Task DeleteSelectedSessions_IgnoresRetentionAndConfirmsBeforeCleanup()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var recentPath = Path.Combine(rootPath, "recent.json");
            await File.WriteAllTextAsync(recentPath, "session");
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var recent = Item(root, "recent.json", DataCategory.ChatSession, DateTime.UtcNow);
            var session = new SessionInfo("s", recent.FullPath, "最近会话", "p", DataCategory.ChatSession, 10, DateTime.UtcNow);
            var remaining = Item(root, "remaining.json", DataCategory.ChatSession, DateTime.UtcNow);
            var scanner = new RecordingScanner(Result(recent), Result(remaining));
            var cleanup = new RecordingCleanupService();
            var dialogs = new FixedDialogService(true);
            var planner = new CapturingPlanner();
            using var viewModel = CreateViewModel(
                scanner,
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([session]),
                planner: planner,
                cleanup: cleanup,
                dialogs: dialogs,
                pathService: new FixedPathService(root));

            await viewModel.ScanForTestingAsync();
            viewModel.SelectedPage = 1;

            await viewModel.DeleteSelectedSessionsCommand.ExecuteAsync(new ArrayList { session });

            Assert.AreEqual(recent.FullPath, planner.LastSelectedPaths.Single());
            Assert.AreSame(planner.LastPlan, cleanup.LastPlan);
            Assert.IsTrue(cleanup.LastConfirmed);
            Assert.AreEqual(2, scanner.ScanCalls);
            Assert.AreEqual(1, viewModel.SelectedPage);
            StringAssert.Contains(dialogs.LastTitle, "确认删除所选会话");
            StringAssert.Contains(dialogs.LastMessage!, "没有可匹配的会话 ID");
            StringAssert.Contains(dialogs.LastMessage!, "会话文件：");
            Assert.IsFalse(dialogs.LastMessage!.Contains("只保留最新一份", StringComparison.Ordinal));
            Assert.IsFalse(dialogs.LastMessage!.Contains("预计立即释放 0.0 B", StringComparison.Ordinal));
            StringAssert.Contains(viewModel.OperationStatus, "清理已取消");
        });
    }

    [TestMethod]
    public async Task DeleteSelectedSessions_DatabaseOnlyDeletesSqliteWithoutFilePlan()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var databasePath = Path.Combine(rootPath, "state.vscdb");
            await File.WriteAllTextAsync(databasePath, "db");
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var database = Item(root, "state.vscdb", DataCategory.SQLite, DateTime.UtcNow);
            var session = new SessionInfo(
                "488ef4de-7b32-4b7c-b7be-6b67203f8717",
                string.Empty,
                "Greeting conversation",
                "empty-window",
                DataCategory.ChatSession,
                0,
                DateTime.UtcNow,
                SessionSource.Database,
                databasePath,
                ["488ef4de-7b32-4b7c-b7be-6b67203f8717"]);
            var scanner = new RecordingScanner(Result(database), Result(database));
            var cleanup = new RecordingCleanupService();
            var sqlite = new RecordingSqliteService();
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                scanner,
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([session]),
                cleanup: cleanup,
                dialogs: dialogs,
                pathService: new FixedPathService(root),
                sqlite: sqlite);

            await viewModel.ScanForTestingAsync();
            await viewModel.DeleteSelectedSessionsCommand.ExecuteAsync(new ArrayList { session });

            Assert.IsNull(cleanup.LastPlan);
            CollectionAssert.AreEqual(new[] { "488ef4de-7b32-4b7c-b7be-6b67203f8717" }, sqlite.LastIds.ToArray());
            CollectionAssert.AreEqual(new[] { databasePath }, sqlite.LastDatabasePaths.ToArray());
            StringAssert.Contains(dialogs.LastMessage!, "SQLite");
            StringAssert.Contains(dialogs.LastMessage!, "只保留最新一份");
            StringAssert.Contains(dialogs.LastMessage!, "不会立刻变小");
            StringAssert.Contains(dialogs.LastMessage!, "1 个聊天数据库");
            StringAssert.Contains(dialogs.LastMessage!, "1 个 state.vscdb");
            Assert.IsFalse(dialogs.LastMessage!.Contains("state.vscdb 约", StringComparison.Ordinal));
            StringAssert.Contains(dialogs.LastMessage!, "优化数据库");
            Assert.IsFalse(dialogs.LastMessage!.Contains("预计立即释放 0.0 B", StringComparison.Ordinal));
            StringAssert.Contains(viewModel.OperationStatus, "优化数据库");
            Assert.AreEqual(2, scanner.ScanCalls);
        });
    }

    [TestMethod]
    public async Task DeleteSelectedSessions_SqliteFailureDoesNotDeleteFiles()
    {
        await RunStaAsync(async () =>
        {
            using var temp = new TemporaryDirectory();
            var rootPath = Path.Combine(temp.Path, "Cursor");
            Directory.CreateDirectory(rootPath);
            var sessionPath = Path.Combine(rootPath, "session.json");
            var databasePath = Path.Combine(rootPath, "state.vscdb");
            await File.WriteAllTextAsync(sessionPath, "session");
            await File.WriteAllTextAsync(databasePath, "db");
            var root = new CursorDataRoot(rootPath, RootKind.RoamingData, "test");
            var sessionItem = Item(root, "session.json", DataCategory.ChatSession, DateTime.UtcNow);
            var database = Item(root, "state.vscdb", DataCategory.SQLite, DateTime.UtcNow);
            var session = new SessionInfo(
                "488ef4de-7b32-4b7c-b7be-6b67203f8717",
                sessionItem.FullPath,
                "Greeting conversation",
                "empty-window",
                DataCategory.ChatSession,
                10,
                DateTime.UtcNow,
                SessionSource.Both,
                databasePath,
                ["488ef4de-7b32-4b7c-b7be-6b67203f8717"]);
            var scanner = new RecordingScanner(Result(sessionItem, database), Result(sessionItem, database));
            var cleanup = new RecordingCleanupService();
            var sqlite = new FailingSqliteService();
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                scanner,
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([session]),
                cleanup: cleanup,
                dialogs: dialogs,
                pathService: new FixedPathService(root),
                sqlite: sqlite);

            await viewModel.ScanForTestingAsync();
            await viewModel.DeleteSelectedSessionsCommand.ExecuteAsync(new ArrayList { session });

            Assert.IsNull(cleanup.LastPlan);
            StringAssert.Contains(viewModel.OperationStatus, "未删除会话文件");
            StringAssert.Contains(dialogs.LastTitle!, "SQLite 未完整成功");
            Assert.IsTrue(File.Exists(sessionPath));
        });
    }

    [TestMethod]
    public void DisplayText_ReturnsChineseLabels()
    {
        var utc = new DateTime(2026, 8, 20, 12, 30, 0, DateTimeKind.Utc);
        var databaseOnly = new SessionInfo(
            "488ef4de-7b32-4b7c-b7be-6b67203f8717",
            string.Empty,
            "db",
            null,
            DataCategory.ChatSession,
            0,
            utc,
            SessionSource.Database,
            "/tmp/state.vscdb");
        var fileSession = new SessionInfo(
            "file",
            "/tmp/a.json",
            "file",
            "p",
            DataCategory.ChatSession,
            1024,
            utc,
            SessionSource.File);

        Assert.AreEqual("历史会话", DisplayText.Category(DataCategory.ChatSession));
        Assert.AreEqual("跟随系统", DisplayText.Theme(CleanerTheme.System));
        Assert.AreEqual("可按会话删除", DisplayText.Recommendation(DataCategory.ChatSession));
        Assert.AreEqual("项目路径已不存在，可考虑清理", DisplayText.WorkspaceRecommendation(true));
        Assert.AreEqual(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), DisplayText.LocalTime(utc));
        Assert.AreEqual("—", DisplayText.FormatSessionSize(databaseOnly));
        Assert.AreEqual("—", databaseOnly.DisplaySizeText);
        Assert.AreEqual(ByteSizeFormatter.Format(1024), DisplayText.FormatSessionSize(fileSession));
    }

    [TestMethod]
    public void AdvancedFlags_ClampHiddenPagesToSettings()
    {
        using var viewModel = CreateViewModel(new FixedScanner(Result()), new ScanResultStore(), new FixedWorkspaceAnalyzer([]), new FixedSessionAnalyzer([]));
        viewModel.AdvancedFeaturesEnabled = true;
        viewModel.SelectedPage = 3;
        viewModel.AdvancedFeaturesEnabled = false;
        Assert.AreEqual(5, viewModel.SelectedPage);

        viewModel.AdvancedToolsEnabled = true;
        viewModel.SelectedPage = 4;
        viewModel.AdvancedToolsEnabled = false;
        Assert.AreEqual(5, viewModel.SelectedPage);
    }

    [TestMethod]
    public async Task CleanupLegacySqliteBackups_ConfirmsAndLeavesRollingBackup()
    {
        await RunStaAsync(async () =>
        {
            var backup = new RecordingBackupService(
                Path.Combine(Path.GetTempPath(), "CursorCleanerBackup"),
                new SqliteBackupUsage(Path.Combine(Path.GetTempPath(), "CursorCleanerBackup"), 100, 1, 200, 2));
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                new FixedScanner(Result()),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([]),
                dialogs: dialogs,
                backup: backup);
            viewModel.AdvancedToolsEnabled = true;

            await viewModel.CleanupLegacySqliteBackupsCommand.ExecuteAsync();

            Assert.AreEqual(1, backup.CleanupCalls);
            StringAssert.Contains(dialogs.LastTitle, "确认删除旧 SQLite 备份");
            StringAssert.Contains(dialogs.LastMessage!, "不会删除会话文件备份");
            StringAssert.Contains(viewModel.SqliteBackupCleanupStatus, "已删除");
        });
    }

    [TestMethod]
    public async Task StopCursor_NotRunning_CommandDisabled()
    {
        await RunStaAsync(async () =>
        {
            using var viewModel = CreateViewModel(
                new FixedScanner(Result()),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([]),
                process: new FixedProcessService(false));
            await viewModel.InitializeAsync();

            Assert.IsFalse(viewModel.CanStopCursor);
            Assert.IsFalse(viewModel.StopCursorCommand.CanExecute(null));
            Assert.AreEqual("Cursor 未运行", viewModel.CursorStateText);
        });
    }

    [TestMethod]
    public async Task StopCursor_ConfirmCancel_DoesNotStop()
    {
        await RunStaAsync(async () =>
        {
            var process = new FixedProcessService(true);
            var dialogs = new FixedDialogService(false);
            using var viewModel = CreateViewModel(
                new FixedScanner(Result()),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([]),
                dialogs: dialogs,
                process: process);
            await viewModel.InitializeAsync();

            Assert.IsTrue(viewModel.CanStopCursor);
            await viewModel.StopCursorCommand.ExecuteAsync();

            Assert.AreEqual(0, process.StopCalls);
            Assert.IsTrue(viewModel.IsCursorRunning);
            Assert.AreEqual("确认强制停止 Cursor", dialogs.LastTitle);
        });
    }

    [TestMethod]
    public async Task StopCursor_ConfirmSuccess_RefreshesState()
    {
        await RunStaAsync(async () =>
        {
            var process = new FixedProcessService(true);
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                new FixedScanner(Result()),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([]),
                dialogs: dialogs,
                process: process);
            await viewModel.InitializeAsync();

            await viewModel.StopCursorCommand.ExecuteAsync();

            Assert.AreEqual(1, process.StopCalls);
            Assert.IsFalse(viewModel.IsCursorRunning);
            Assert.IsFalse(viewModel.CanStopCursor);
            Assert.AreEqual("Cursor 未运行", viewModel.CursorStateText);
            Assert.AreEqual("Cursor 已停止", viewModel.OperationStatus);
            Assert.AreEqual(StatusSeverity.Success, viewModel.OperationStatusSeverity);
        });
    }

    [TestMethod]
    public async Task StopCursor_Failure_ShowsErrorAndStaysRunning()
    {
        await RunStaAsync(async () =>
        {
            var process = new FixedProcessService(true)
            {
                StopResult = new StopCursorResult(false, true, 0, "permission denied")
            };
            var dialogs = new FixedDialogService(true);
            using var viewModel = CreateViewModel(
                new FixedScanner(Result()),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([]),
                dialogs: dialogs,
                process: process);
            await viewModel.InitializeAsync();

            await viewModel.StopCursorCommand.ExecuteAsync();

            Assert.AreEqual(1, process.StopCalls);
            Assert.IsTrue(viewModel.IsCursorRunning);
            Assert.IsTrue(viewModel.CanStopCursor);
            StringAssert.Contains(viewModel.OperationStatus, "permission denied");
            Assert.AreEqual(StatusSeverity.Error, viewModel.OperationStatusSeverity);
            Assert.AreEqual("无法停止 Cursor", dialogs.LastTitle);
        });
    }

    [TestMethod]
    public async Task SelectingSingleSession_LoadsReadOnlyPreview()
    {
        await RunStaAsync(async () =>
        {
            var root = new CursorDataRoot(Path.GetTempPath(), RootKind.UserProfile, "test");
            var session = new SessionInfo("s", Path.Combine(root.Path, "projects", "p", "one.jsonl"), "会话标题", "p", DataCategory.AgentTranscript, 10, DateTime.UtcNow);
            var content = new FixedSessionContentService(new SessionContentPreview(
                session.FilePath,
                [new SessionMessage("user", "用户", "你好")],
                false,
                null));
            using var viewModel = CreateViewModel(
                new FixedScanner(Result()),
                new ScanResultStore(),
                new FixedWorkspaceAnalyzer([]),
                new FixedSessionAnalyzer([session]),
                sessionContent: content);

            await viewModel.ScanForTestingAsync();
            viewModel.NotifySelectionChanged(new ArrayList { session }, null);
            await WaitUntilAsync(() => viewModel.HasSessionPreview);

            Assert.AreEqual(session.FilePath, content.LastPath);
            Assert.AreEqual("会话标题", viewModel.SessionPreviewTitle);
            Assert.AreEqual(1, viewModel.SessionPreviewMessages.Count);
            Assert.AreEqual("你好", viewModel.SessionPreviewMessages[0].Text);
            StringAssert.Contains(viewModel.SessionPreviewStatus, "只读");

            viewModel.NotifySelectionChanged(new ArrayList { session, session }, null);
            Assert.AreEqual(0, viewModel.SessionPreviewMessages.Count);
            Assert.IsFalse(viewModel.HasSessionPreview);
            StringAssert.Contains(viewModel.SessionPreviewStatus, "已选择多项");
        });
    }

    [TestMethod]
    public void BulkObservableCollection_ReplaceRangeRaisesOneReset()
    {
        var collection = new BulkObservableCollection<int> { 9 };
        var notifications = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => notifications.Add(e.Action);

        collection.ReplaceRange([1, 2, 3]);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, collection.ToArray());
        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Reset }, notifications);
    }

    private static MainViewModel CreateViewModel(
        ICursorScannerService scanner,
        IScanResultStore store,
        IWorkspaceAnalyzerService workspaceAnalyzer,
        ISessionAnalyzerService sessionAnalyzer,
        ICleanupPlannerService? planner = null,
        ISettingsService? settings = null,
        IDialogService? dialogs = null,
        ICleanupService? cleanup = null,
        ICursorPathService? pathService = null,
        ISessionContentService? sessionContent = null,
        ISqliteService? sqlite = null,
        IBackupService? backup = null,
        IProcessService? process = null)
    {
        pathService ??= new FixedPathService();
        return new(
            pathService, scanner, workspaceAnalyzer, sessionAnalyzer, store,
            process ?? new FixedProcessService(), new NullLogService(), settings ?? new MemorySettingsService(),
            planner ?? new CleanupPlannerService(new PathGuard(pathService.GetDataRoots().Select(root => root.Path))),
            cleanup ?? new NullCleanupService(), new NullShellService(),
            sqlite ?? new NullSqliteService(), dialogs ?? new NullDialogService(),
            sessionContent ?? new NullSessionContentService(),
            new NullThemeService(),
            backup ?? new NullBackupService());
    }

    private static ScanItem Item(CursorDataRoot root, string relative, DataCategory category, DateTime? time = null) =>
        new(Path.Combine(root.Path, relative.Replace('/', Path.DirectorySeparatorChar)), relative, root, category, 10,
            time ?? DateTime.UtcNow.AddDays(-40), FileAttributes.Normal);

    private static ScanResult Result(params ScanItem[] items) => new(items,
        new ScanSummary(items.Length, items.Sum(item => item.Size), new Dictionary<DataCategory, long>(),
            new Dictionary<DataCategory, long>(), [], 0, TimeSpan.Zero), DateTime.UtcNow);

    private static Task RunStaAsync(Func<Task> action) => action();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Timed out waiting for session preview.");
    }

    private sealed class FixedPathService : ICursorPathService
    {
        private readonly CursorDataRoot _root;
        public FixedPathService(CursorDataRoot? root = null) => _root = root ?? new(Path.GetTempPath(), RootKind.RoamingData, "test");
        public IReadOnlyList<CursorDataRoot> GetDataRoots() => [_root];
    }

    private sealed class FixedScanner(ScanResult result) : ICursorScannerService
    {
        public async IAsyncEnumerable<ScanItem> ScanItemsAsync(IProgress<ScanProgress>? progress = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in result.Items) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }
        public Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class RecordingScanner(ScanResult first, ScanResult second) : ICursorScannerService
    {
        public int ScanCalls { get; private set; }
        public bool LastTokenCancelled { get; private set; }

        public async IAsyncEnumerable<ScanItem> ScanItemsAsync(IProgress<ScanProgress>? progress = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in (ScanCalls == 0 ? first : second).Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LastTokenCancelled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            var result = ScanCalls == 0 ? first : second;
            ScanCalls++;
            return Task.FromResult(result);
        }
    }

    private sealed class CancelledCleanupService : ICleanupService
    {
        public Task<CleanupOperationResult> ExecuteAsync(CleanupPlan plan, bool confirmed, CleanupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CleanupOperationResult(plan.Id, false, false, true, [], "Cleanup was cancelled before the next file."));
    }

    private sealed class RecordingCleanupService : ICleanupService
    {
        public CleanupPlan? LastPlan { get; private set; }
        public bool LastConfirmed { get; private set; }
        public Task<CleanupOperationResult> ExecuteAsync(CleanupPlan plan, bool confirmed, CleanupOptions options, CancellationToken cancellationToken = default)
        {
            LastPlan = plan;
            LastConfirmed = confirmed;
            return Task.FromResult(new CleanupOperationResult(plan.Id, false, false, true, [], "Cleanup was cancelled before the next file."));
        }
    }

    private sealed class FixedWorkspaceAnalyzer(IReadOnlyList<WorkspaceInfo> result) : IWorkspaceAnalyzerService
    {
        public Task<IReadOnlyList<WorkspaceInfo>> AnalyzeAsync(ScanResult scanResult, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class ThrowingWorkspaceAnalyzer : IWorkspaceAnalyzerService
    {
        public Task<IReadOnlyList<WorkspaceInfo>> AnalyzeAsync(ScanResult scanResult, CancellationToken cancellationToken = default) => throw new InvalidOperationException("analysis failed");
    }

    private sealed class FixedSessionAnalyzer(IReadOnlyList<SessionInfo> result) : ISessionAnalyzerService
    {
        public Task<IReadOnlyList<SessionInfo>> AnalyzeAsync(ScanResult scanResult, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class FixedProcessService : IProcessService
    {
        private bool _running;
        public int StopCalls { get; private set; }
        public StopCursorResult StopResult { get; set; } = new(true, false, 0, null);

        public FixedProcessService(bool running = false)
        {
            _running = running;
            if (running)
            {
                StopResult = new StopCursorResult(true, true, 1, null);
            }
        }

        public bool IsCursorRunning() => _running;

        public Task<StopCursorResult> StopCursorAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            if (StopResult.Succeeded)
            {
                _running = false;
            }

            return Task.FromResult(StopResult);
        }
    }
    private sealed class NullLogService : ILogService
    {
        public string LogDirectory => Path.GetTempPath();
        public Task WriteAsync(string level, string operation, string message, string? path = null, Exception? exception = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class MemorySettingsService : ISettingsService
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "settings.json");
        public Task<OperationSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OperationSettings());
        public Task SaveAsync(OperationSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class BlockingSettingsService : ISettingsService
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "blocking-settings.json");
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OperationSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OperationSettings());
        public async Task SaveAsync(OperationSettings settings, CancellationToken cancellationToken = default)
        {
            SaveStarted.SetResult();
            await AllowSave.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class CapturingPlanner : ICleanupPlannerService
    {
        public DateTime CutoffUtc { get; private set; }
        public IReadOnlyList<string> LastSelectedPaths { get; private set; } = [];
        public CleanupPlan? LastPlan { get; private set; }
        public CleanupPlan CreatePlan(ScanResult scanResult, IEnumerable<string> approvedRoots, DateTime cutoffUtc)
        {
            CutoffUtc = cutoffUtc;
            LastPlan = new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, scanResult.Items.Select(item => new CleanupPlanItem(item.FullPath, item.RelativePath, item.Root, item.Category, item.Size, item.LastWriteTimeUtc)));
            return LastPlan;
        }

        public CleanupPlan CreateSelectedPlan(ScanResult scanResult, IEnumerable<string> approvedRoots, IEnumerable<string> selectedPaths)
        {
            LastSelectedPaths = selectedPaths.ToArray();
            var selected = LastSelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            LastPlan = new CleanupPlan(
                Guid.NewGuid(),
                DateTime.UtcNow,
                scanResult.Items
                    .Where(item => selected.Contains(item.FullPath))
                    .Select(item => new CleanupPlanItem(item.FullPath, item.RelativePath, item.Root, item.Category, item.Size, item.LastWriteTimeUtc)));
            return LastPlan;
        }
    }
    private sealed class FixedDialogService(bool answer) : IDialogService
    {
        public string? LastTitle { get; private set; }
        public string? LastMessage { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            LastTitle = title;
            LastMessage = message;
            return Task.FromResult(answer);
        }
        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            LastTitle = title;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }
    private sealed class NullCleanupService : ICleanupService
    {
        public Task<CleanupOperationResult> ExecuteAsync(CleanupPlan plan, bool confirmed, CleanupOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class NullShellService : IShellService
    {
        public void OpenDirectory(string path) { }
        public void SelectFile(string path) { }
        public void OpenLogs() { }
    }
    private sealed class NullSqliteService : ISqliteService
    {
        public Task<SqliteMaintenanceResult> VacuumAsync(string databasePath, IEnumerable<string> approvedRoots, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SqliteChatCleanupResult> DeleteChatRecordsAsync(IEnumerable<string> conversationIds, IEnumerable<string> databasePaths, IEnumerable<string> approvedRoots, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SqliteChatCleanupResult(true, false, [], null));
    }

    private sealed class RecordingSqliteService : ISqliteService
    {
        public IReadOnlyList<string> LastIds { get; private set; } = [];
        public IReadOnlyList<string> LastDatabasePaths { get; private set; } = [];
        public Task<SqliteMaintenanceResult> VacuumAsync(string databasePath, IEnumerable<string> approvedRoots, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SqliteChatCleanupResult> DeleteChatRecordsAsync(IEnumerable<string> conversationIds, IEnumerable<string> databasePaths, IEnumerable<string> approvedRoots, CancellationToken cancellationToken = default)
        {
            LastIds = conversationIds.ToArray();
            LastDatabasePaths = databasePaths.ToArray();
            return Task.FromResult(new SqliteChatCleanupResult(true, false, [new SqliteChatDatabaseResult(LastDatabasePaths.FirstOrDefault() ?? string.Empty, true, 3, null, null)], null));
        }
    }
    private sealed class NullSessionContentService : ISessionContentService
    {
        public Task<SessionContentPreview> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionContentPreview(path, [], false, null));
    }
    private sealed class FixedSessionContentService(SessionContentPreview preview) : ISessionContentService
    {
        public string? LastPath { get; private set; }
        public Task<SessionContentPreview> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            LastPath = path;
            return Task.FromResult(preview);
        }
    }
    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullThemeService : IThemeService
    {
        public void Apply(CleanerTheme theme) { }
    }

    private sealed class NullBackupService : IBackupService
    {
        public string BackupRootPath => Path.GetTempPath();
        public Task<BackupOperationResult> BackupAsync(IEnumerable<CleanupPlanItem> items, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> CreateSqliteBackupPathAsync(string databasePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> CommitSqliteBackupAsync(string stagingPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void EnsureVolumeFreeSpace(string pathOnVolume, long requiredBytes, string operationLabel) { }
        public SqliteBackupUsage GetSqliteBackupUsage() => new(BackupRootPath, 0, 0, 0, 0);
        public Task<SqliteBackupCleanupResult> CleanupLegacySqliteBackupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SqliteBackupCleanupResult(true, 0, 0, null));
    }

    private sealed class RecordingBackupService : IBackupService
    {
        public string BackupRootPath { get; }
        public SqliteBackupUsage Usage { get; set; }
        public SqliteBackupCleanupResult CleanupResult { get; set; } = new(true, 1, 10, null);
        public int CleanupCalls { get; private set; }

        public RecordingBackupService(string backupRootPath, SqliteBackupUsage? usage = null)
        {
            BackupRootPath = backupRootPath;
            Usage = usage ?? new SqliteBackupUsage(backupRootPath, 0, 0, 0, 0);
        }

        public Task<BackupOperationResult> BackupAsync(IEnumerable<CleanupPlanItem> items, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> CreateSqliteBackupPathAsync(string databasePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> CommitSqliteBackupAsync(string stagingPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void EnsureVolumeFreeSpace(string pathOnVolume, long requiredBytes, string operationLabel) { }
        public SqliteBackupUsage GetSqliteBackupUsage() => Usage;
        public Task<SqliteBackupCleanupResult> CleanupLegacySqliteBackupsAsync(CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return Task.FromResult(CleanupResult);
        }
    }

    private sealed class FailingSqliteService : ISqliteService
    {
        public Task<SqliteMaintenanceResult> VacuumAsync(string databasePath, IEnumerable<string> approvedRoots, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SqliteChatCleanupResult> DeleteChatRecordsAsync(
            IEnumerable<string> conversationIds,
            IEnumerable<string> databasePaths,
            IEnumerable<string> approvedRoots,
            CancellationToken cancellationToken = default)
        {
            var paths = databasePaths.ToArray();
            return Task.FromResult(new SqliteChatCleanupResult(
                false,
                false,
                [new SqliteChatDatabaseResult(paths.FirstOrDefault() ?? string.Empty, false, 0, null, "Injected SQLite failure.")],
                "Injected SQLite failure."));
        }
    }
}
