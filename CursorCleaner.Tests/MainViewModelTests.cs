using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using CursorCleaner.Converters;
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
            var sessionPreviewChanges = 0;
            var workspacePreviewChanges = 0;
            viewModel.GeneratePreviewCommand.CanExecuteChanged += (_, _) => allPreviewChanges++;
            viewModel.GenerateSelectedSessionPreviewCommand.CanExecuteChanged += (_, _) => sessionPreviewChanges++;
            viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecuteChanged += (_, _) => workspacePreviewChanges++;

            Assert.IsFalse(viewModel.GeneratePreviewCommand.CanExecute(null));
            Assert.IsFalse(viewModel.GenerateSelectedSessionPreviewCommand.CanExecute(sessions));
            Assert.IsFalse(viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecute(workspaces));

            await viewModel.ScanForTestingAsync();

            Assert.AreSame(result, store.Latest);
            Assert.IsTrue(viewModel.GeneratePreviewCommand.CanExecute(null));
            Assert.IsFalse(viewModel.GenerateSelectedSessionPreviewCommand.CanExecute(new ArrayList()));
            Assert.IsFalse(viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecute(new ArrayList()));
            Assert.IsTrue(viewModel.GenerateSelectedSessionPreviewCommand.CanExecute(sessions));
            Assert.IsTrue(viewModel.GenerateSelectedWorkspacePreviewCommand.CanExecute(workspaces));
            Assert.IsTrue(allPreviewChanges > 0);
            Assert.IsTrue(sessionPreviewChanges > 0);
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
            using var viewModel = CreateViewModel(new FixedScanner(Result(
                Item(root, "a.db", DataCategory.SQLite),
                Item(root, "b.json", DataCategory.ChatSession))), new ScanResultStore(), new FixedWorkspaceAnalyzer([]), new FixedSessionAnalyzer([]));
            var resetCount = 0;
            ((INotifyCollectionChanged)viewModel.ScanItems).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) resetCount++;
            };

            await viewModel.ScanForTestingAsync();

            Assert.AreEqual(1, resetCount);
            Assert.AreEqual(2, viewModel.ScanVisibleCount);
            viewModel.SelectedCategory = "SQLite";
            Assert.AreEqual(1, viewModel.ScanVisibleCount);
            Assert.IsTrue(viewModel.HasScanResult);
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
    public void Converters_ReturnChineseAndLocalTime()
    {
        var category = new DataCategoryConverter();
        var theme = new ThemeConverter();
        var local = new UtcToLocalTimeConverter();
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        var utc = new DateTime(2026, 8, 20, 12, 30, 0, DateTimeKind.Utc);

        Assert.AreEqual("历史会话", category.Convert(DataCategory.ChatSession, typeof(string), null!, culture));
        Assert.AreEqual("跟随系统", theme.Convert(CleanerTheme.System, typeof(string), null!, culture));
        Assert.AreEqual(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture), local.Convert(utc, typeof(string), null!, culture));
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
        ISessionContentService? sessionContent = null)
    {
        pathService ??= new FixedPathService();
        return new(
            pathService, scanner, workspaceAnalyzer, sessionAnalyzer, store,
            new FixedProcessService(), new NullLogService(), settings ?? new MemorySettingsService(),
            planner ?? new CleanupPlannerService(new PathGuard(pathService.GetDataRoots().Select(root => root.Path))),
            cleanup ?? new NullCleanupService(), new NullShellService(),
            new NullSqliteService(), dialogs ?? new NullDialogService(),
            sessionContent ?? new NullSessionContentService());
    }

    private static ScanItem Item(CursorDataRoot root, string relative, DataCategory category, DateTime? time = null) =>
        new(Path.Combine(root.Path, relative.Replace('/', Path.DirectorySeparatorChar)), relative, root, category, 10,
            time ?? DateTime.UtcNow.AddDays(-40), FileAttributes.Normal);

    private static ScanResult Result(params ScanItem[] items) => new(items,
        new ScanSummary(items.Length, items.Sum(item => item.Size), new Dictionary<DataCategory, long>(),
            new Dictionary<DataCategory, long>(), [], 0, TimeSpan.Zero), DateTime.UtcNow);

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action().GetAwaiter().GetResult(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

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

    private sealed class FixedProcessService : IProcessService { public bool IsCursorRunning() => false; }
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
        public CleanupPlan CreatePlan(ScanResult scanResult, IEnumerable<string> approvedRoots, DateTime cutoffUtc)
        {
            CutoffUtc = cutoffUtc;
            return new CleanupPlan(Guid.NewGuid(), DateTime.UtcNow, scanResult.Items.Select(item => new CleanupPlanItem(item.FullPath, item.RelativePath, item.Root, item.Category, item.Size, item.LastWriteTimeUtc)));
        }
    }
    private sealed class FixedDialogService(bool answer) : IDialogService
    {
        public string? LastMessage { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) { LastMessage = message; return Task.FromResult(answer); }
        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
}
