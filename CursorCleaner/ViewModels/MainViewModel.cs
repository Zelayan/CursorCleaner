using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using CursorCleaner.Helpers;
using CursorCleaner.Models;
using CursorCleaner.Services;

namespace CursorCleaner.ViewModels;

public enum CleanupPolicyMode
{
    RetentionPeriod,
    CutoffDate
}

public enum StatusSeverity
{
    Normal,
    Success,
    Warning,
    Error
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string AllProjects = "全部项目";
    private readonly ICursorPathService _pathService;
    private readonly ICursorScannerService _scanner;
    private readonly IWorkspaceAnalyzerService _workspaceAnalyzer;
    private readonly ISessionAnalyzerService _sessionAnalyzer;
    private readonly IScanResultStore _store;
    private readonly IProcessService _process;
    private readonly ILogService _log;
    private readonly ISettingsService _settingsService;
    private readonly ICleanupPlannerService _planner;
    private readonly ICleanupService _cleanup;
    private readonly IShellService _shell;
    private readonly ISqliteService _sqlite;
    private readonly IDialogService _dialogs;
    private readonly ISessionContentService _sessionContent;
    private readonly object _activityGate = new();
    private readonly HashSet<Task> _activities = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _scopeCancellation;
    private CancellationTokenSource? _scanFilterCancellation;
    private CancellationTokenSource? _sessionFilterCancellation;
    private CancellationTokenSource? _workspaceFilterCancellation;
    private CancellationTokenSource? _sessionPreviewCancellation;
    private ScanResult? _scanResult;
    private CleanupPlan? _cleanupPlan;
    private OperationSettings _settings = new();
    private bool _loadingSettings;
    private bool _disposed;
    private bool _closeRequested;
    private bool _isVacuuming;
    private bool _isCleaning;
    private bool _isScopeRefreshing;
    private long _settingsVersion;
    private long _scopeGeneration;
    private int _selectedPage;
    private bool _isScanning;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string _scanStatus = "尚未扫描";
    private string _currentScanPath = "等待扫描";
    private long _progressFiles;
    private long _progressBytes;
    private double _progressPercent;
    private string _lastScanText = "从未";
    private string _searchText = string.Empty;
    private string _selectedCategory = "全部类型";
    private string _sessionSearch = string.Empty;
    private string? _sessionProject = AllProjects;
    private string _workspaceSearch = string.Empty;
    private int _retentionDays = 30;
    private DateTime? _customCutoff;
    private CleanupPolicyMode _cleanupPolicyMode;
    private string _previewStatus = "扫描后可生成清理预览";
    private string _operationStatus = string.Empty;
    private string _closingStatus = string.Empty;
    private ScanItem? _selectedDatabase;
    private string _sqliteStatus = "请选择数据库";
    private long _sqliteBefore;
    private long _sqliteAfter;
    private string? _sqliteBackupPath;
    private int _previewFiles;
    private int _previewWorkspaces;
    private int _previewSessions;
    private long _previewBytes;
    private int _cleanedFiles;
    private long _cleanedBytes;
    private long _currentUsage;
    private bool _isSettingsDirty;
    private int _scanVisibleCount;
    private int _sessionVisibleCount;
    private int _workspaceVisibleCount;
    private int _selectedSessionCount;
    private int _selectedWorkspaceCount;
    private long _sessionPreviewGeneration;
    private SessionInfo? _previewedSession;
    private string _sessionPreviewTitle = "选择一个会话以预览内容";
    private string _sessionPreviewStatus = "预览为只读，不会修改 Cursor 数据。";
    private bool _sessionPreviewTruncated;
    private bool _hasSessionPreview;
    private StatusSeverity _scanStatusSeverity;
    private StatusSeverity _cursorStatusSeverity;
    private StatusSeverity _operationStatusSeverity;
    private StatusSeverity _sqliteStatusSeverity;
    private StatusSeverity _settingsStatusSeverity;

    public MainViewModel(
        ICursorPathService pathService, ICursorScannerService scanner,
        IWorkspaceAnalyzerService workspaceAnalyzer, ISessionAnalyzerService sessionAnalyzer,
        IScanResultStore store, IProcessService process, ILogService log,
        ISettingsService settingsService, ICleanupPlannerService planner,
        ICleanupService cleanup, IShellService shell, ISqliteService sqlite,
        IDialogService dialogs, ISessionContentService sessionContent)
    {
        _pathService = pathService;
        _scanner = scanner;
        _workspaceAnalyzer = workspaceAnalyzer;
        _sessionAnalyzer = sessionAnalyzer;
        _store = store;
        _process = process;
        _log = log;
        _settingsService = settingsService;
        _planner = planner;
        _cleanup = cleanup;
        _shell = shell;
        _sqlite = sqlite;
        _dialogs = dialogs;
        _sessionContent = sessionContent;

        ScanItemsView = CollectionViewSource.GetDefaultView(ScanItems);
        ScanItemsView.Filter = FilterScanItem;
        SessionsView = CollectionViewSource.GetDefaultView(Sessions);
        SessionsView.Filter = FilterSession;
        WorkspacesView = CollectionViewSource.GetDefaultView(Workspaces);
        WorkspacesView.Filter = FilterWorkspace;

        NavigateCommand = new RelayCommand(p => SelectedPage = int.TryParse(p?.ToString(), out var page) ? page : 0);
        ScanCommand = new AsyncRelayCommand(token => TrackActivityAsync(() => ScanAsync(token)), () => !IsScanning && !IsBusy && !_closeRequested);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        GeneratePreviewCommand = new RelayCommand(_ => GeneratePreview(null), _ => CanPreview);
        GenerateSelectedWorkspacePreviewCommand = new RelayCommand(p => GeneratePreview(GetSelectedWorkspacePaths(p as IList)), p => CanPreview && HasSelectedWorkspaces(p));
        DeleteSelectedSessionsCommand = new AsyncRelayCommand(
            (parameter, token) => TrackActivityAsync(() => DeleteSelectedSessionsAsync(parameter, token)),
            parameter => CanPreview && HasSelectedSessions(parameter) && !IsCleaning);
        CleanupCommand = new AsyncRelayCommand(token => TrackActivityAsync(() => CleanupAsync(token)), () => _cleanupPlan?.FileCount > 0 && !IsBusy && !IsScanning && !_closeRequested);
        CancelCleanupCommand = new RelayCommand(() =>
        {
            CleanupCommand.Cancel();
            DeleteSelectedSessionsCommand.Cancel();
        }, () => IsCleaning);
        VacuumCommand = new AsyncRelayCommand(token => TrackActivityAsync(() => VacuumAsync(token)), () => SelectedDatabase is not null && AdvancedToolsEnabled && !IsBusy && !IsScanning && !_closeRequested);
        OpenDirectoryCommand = new RelayCommand(OpenDirectory);
        OpenLogsCommand = new RelayCommand(() => TryShell(_shell.OpenLogs));
        OpenDataDirectoryCommand = new RelayCommand(() => TryShell(() => _shell.OpenDirectory(Path.GetDirectoryName(_settingsService.SettingsPath)!)));
        SaveSettingsCommand = new AsyncRelayCommand(token => TrackActivityAsync(() => SaveSettingsAsync(token)), () => IsSettingsDirty && !IsBusy && !IsScanning && !_closeRequested);
    }

    public ObservableCollection<ScanItem> ScanItems { get; } = new BulkObservableCollection<ScanItem>();
    public ObservableCollection<SessionInfo> Sessions { get; } = new BulkObservableCollection<SessionInfo>();
    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = new BulkObservableCollection<WorkspaceInfo>();
    public ObservableCollection<LargeFileInfo> LargeFiles { get; } = new BulkObservableCollection<LargeFileInfo>();
    public ObservableCollection<ScanItem> Databases { get; } = new BulkObservableCollection<ScanItem>();
    public ObservableCollection<string> SessionProjects { get; } = new BulkObservableCollection<string> { AllProjects };
    public ObservableCollection<SessionMessage> SessionPreviewMessages { get; } = new BulkObservableCollection<SessionMessage>();
    public IReadOnlyList<string> Categories { get; } = ["全部类型", "历史会话", "Workspace", "SQLite", "Agent Transcripts", "其他"];
    public IReadOnlyList<int> RetentionOptions { get; } = [7, 30, 90];
    public Array ThemeOptions => Enum.GetValues<CleanerTheme>();
    public ICollectionView ScanItemsView { get; }
    public ICollectionView SessionsView { get; }
    public ICollectionView WorkspacesView { get; }

    public ICommand NavigateCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand GeneratePreviewCommand { get; }
    public RelayCommand GenerateSelectedWorkspacePreviewCommand { get; }
    public AsyncRelayCommand DeleteSelectedSessionsCommand { get; }
    public AsyncRelayCommand CleanupCommand { get; }
    public RelayCommand CancelCleanupCommand { get; }
    public AsyncRelayCommand VacuumCommand { get; }
    public RelayCommand OpenDirectoryCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenDataDirectoryCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }

    public int SelectedPage { get => _selectedPage; set => SetProperty(ref _selectedPage, value); }
    public bool IsScanning { get => _isScanning; private set { if (SetProperty(ref _isScanning, value)) { OnPropertyChanged(nameof(IsProgressVisible)); RaiseCommands(); } } }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanEditTargets)); RaiseCommands(); } } }
    public bool IsCleaning { get => _isCleaning; private set { if (SetProperty(ref _isCleaning, value)) CancelCleanupCommand.NotifyCanExecuteChanged(); } }
    public bool IsScopeRefreshing { get => _isScopeRefreshing; private set => SetProperty(ref _isScopeRefreshing, value); }
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }
    public bool CanEditTargets => !IsBusy;
    public bool IsProgressVisible => IsScanning;
    public string ScanStatus { get => _scanStatus; private set => SetProperty(ref _scanStatus, value); }
    public StatusSeverity ScanStatusSeverity { get => _scanStatusSeverity; private set => SetProperty(ref _scanStatusSeverity, value); }
    public string CurrentScanPath { get => _currentScanPath; private set => SetProperty(ref _currentScanPath, value); }
    public long ProgressFiles { get => _progressFiles; private set => SetProperty(ref _progressFiles, value); }
    public long ProgressBytes { get => _progressBytes; private set => SetProperty(ref _progressBytes, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string LastScanText { get => _lastScanText; private set => SetProperty(ref _lastScanText, value); }
    public string DataDirectory => string.Join("  |  ", _pathService.GetDataRoots().Select(r => r.Path));
    public string ScopeNotice => "扫描器会读取全部 Cursor 数据根；以下范围仅筛选显示内容和清理计划。";
    public bool IsCursorRunning => _process.IsCursorRunning();
    public string CursorStateText => IsCursorRunning ? "Cursor 正在运行：可扫描，但清理和数据库维护将被阻止" : "Cursor 未运行";
    public StatusSeverity CursorStatusSeverity { get => _cursorStatusSeverity; private set => SetProperty(ref _cursorStatusSeverity, value); }
    public string ClosingStatus { get => _closingStatus; private set { if (SetProperty(ref _closingStatus, value)) OnPropertyChanged(nameof(IsClosing)); } }
    public bool IsClosing => !string.IsNullOrWhiteSpace(ClosingStatus);

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) DebounceRefresh(ScanItemsView, ref _scanFilterCancellation, UpdateScanVisibleCount); } }
    public string SelectedCategory { get => _selectedCategory; set { if (SetProperty(ref _selectedCategory, value)) RefreshView(ScanItemsView, UpdateScanVisibleCount); } }
    public string SessionSearch { get => _sessionSearch; set { if (SetProperty(ref _sessionSearch, value)) DebounceRefresh(SessionsView, ref _sessionFilterCancellation, UpdateSessionVisibleCount); } }
    public string? SessionProject { get => _sessionProject; set { if (SetProperty(ref _sessionProject, value)) RefreshView(SessionsView, UpdateSessionVisibleCount); } }
    public string WorkspaceSearch { get => _workspaceSearch; set { if (SetProperty(ref _workspaceSearch, value)) DebounceRefresh(WorkspacesView, ref _workspaceFilterCancellation, UpdateWorkspaceVisibleCount); } }

    public bool HasScanResult => _scanResult is not null;
    public int ScanVisibleCount { get => _scanVisibleCount; private set { if (SetProperty(ref _scanVisibleCount, value)) OnPropertyChanged(nameof(IsScanFilterEmpty)); } }
    public int SessionVisibleCount { get => _sessionVisibleCount; private set { if (SetProperty(ref _sessionVisibleCount, value)) OnPropertyChanged(nameof(IsSessionFilterEmpty)); } }
    public int WorkspaceVisibleCount { get => _workspaceVisibleCount; private set { if (SetProperty(ref _workspaceVisibleCount, value)) OnPropertyChanged(nameof(IsWorkspaceFilterEmpty)); } }
    public int SelectedSessionCount { get => _selectedSessionCount; private set => SetProperty(ref _selectedSessionCount, value); }
    public int SelectedWorkspaceCount { get => _selectedWorkspaceCount; private set => SetProperty(ref _selectedWorkspaceCount, value); }
    public string SessionPreviewTitle { get => _sessionPreviewTitle; private set => SetProperty(ref _sessionPreviewTitle, value); }
    public string SessionPreviewStatus { get => _sessionPreviewStatus; private set => SetProperty(ref _sessionPreviewStatus, value); }
    public bool SessionPreviewTruncated { get => _sessionPreviewTruncated; private set => SetProperty(ref _sessionPreviewTruncated, value); }
    public bool HasSessionPreview { get => _hasSessionPreview; private set => SetProperty(ref _hasSessionPreview, value); }
    public bool IsScanFilterEmpty => HasScanResult && ScanVisibleCount == 0;
    public bool IsSessionFilterEmpty => HasScanResult && SessionVisibleCount == 0;
    public bool IsWorkspaceFilterEmpty => HasScanResult && WorkspaceVisibleCount == 0;

    public long TotalBytes { get; private set; }
    public long SessionBytes { get; private set; }
    public long WorkspaceBytes { get; private set; }
    public long SqliteBytes { get; private set; }
    public long AgentBytes { get; private set; }
    public long OtherBytes { get; private set; }
    public long TotalFiles { get; private set; }

    public int RetentionDays
    {
        get => _retentionDays;
        set
        {
            if (!SetProperty(ref _retentionDays, value)) return;
            _settings.RetentionDays = value;
            if (!_loadingSettings) MarkSettingsDirty();
            InvalidatePreview("保留策略已更改，请重新生成预览");
        }
    }
    public CleanupPolicyMode CleanupPolicyMode { get => _cleanupPolicyMode; set { if (SetProperty(ref _cleanupPolicyMode, value)) { OnPropertyChanged(nameof(IsRetentionPolicy)); OnPropertyChanged(nameof(IsCutoffPolicy)); InvalidatePreview("清理策略已更改，请重新生成预览"); } } }
    public bool IsRetentionPolicy => CleanupPolicyMode == CleanupPolicyMode.RetentionPeriod;
    public bool IsCutoffPolicy => CleanupPolicyMode == CleanupPolicyMode.CutoffDate;
    public DateTime? CustomCutoff { get => _customCutoff; set { if (SetProperty(ref _customCutoff, value)) InvalidatePreview("自定义日期已更改，请重新生成预览"); } }
    public string PreviewStatus { get => _previewStatus; private set => SetProperty(ref _previewStatus, value); }
    public string OperationStatus { get => _operationStatus; private set => SetProperty(ref _operationStatus, value); }
    public StatusSeverity OperationStatusSeverity { get => _operationStatusSeverity; private set => SetProperty(ref _operationStatusSeverity, value); }
    public int PreviewFiles { get => _previewFiles; private set => SetProperty(ref _previewFiles, value); }
    public int PreviewWorkspaces { get => _previewWorkspaces; private set => SetProperty(ref _previewWorkspaces, value); }
    public int PreviewSessions { get => _previewSessions; private set => SetProperty(ref _previewSessions, value); }
    public long PreviewBytes { get => _previewBytes; private set => SetProperty(ref _previewBytes, value); }
    public int CleanedFiles { get => _cleanedFiles; private set => SetProperty(ref _cleanedFiles, value); }
    public long CleanedBytes { get => _cleanedBytes; private set => SetProperty(ref _cleanedBytes, value); }
    public long CurrentUsage { get => _currentUsage; private set => SetProperty(ref _currentUsage, value); }
    public bool CanPreview => _scanResult is not null && !IsScanning && !IsBusy && !_closeRequested;
    public bool HasCleanupPlan => _cleanupPlan?.FileCount > 0;

    public ScanItem? SelectedDatabase { get => _selectedDatabase; set { if (IsBusy) return; if (SetProperty(ref _selectedDatabase, value)) { SqliteStatus = value is null ? "请选择数据库" : "只读检查就绪；执行时将先备份"; SqliteStatusSeverity = StatusSeverity.Normal; VacuumCommand.NotifyCanExecuteChanged(); } } }
    public string SqliteStatus { get => _sqliteStatus; private set => SetProperty(ref _sqliteStatus, value); }
    public StatusSeverity SqliteStatusSeverity { get => _sqliteStatusSeverity; private set => SetProperty(ref _sqliteStatusSeverity, value); }
    public long SqliteBefore { get => _sqliteBefore; private set => SetProperty(ref _sqliteBefore, value); }
    public long SqliteAfter { get => _sqliteAfter; private set => SetProperty(ref _sqliteAfter, value); }
    public long SqliteReclaimed => Math.Max(0, SqliteBefore - SqliteAfter);
    public string? SqliteBackupPath { get => _sqliteBackupPath; private set => SetProperty(ref _sqliteBackupPath, value); }

    public bool AutomaticBackup { get => _settings.AutomaticBackup; set { if (_settings.AutomaticBackup != value) { _settings.AutomaticBackup = value; SettingsChanged(nameof(AutomaticBackup)); } } }
    public bool UseRecycleBin { get => _settings.UseRecycleBin; set { if (_settings.UseRecycleBin != value) { _settings.UseRecycleBin = value; SettingsChanged(nameof(UseRecycleBin)); } } }
    public bool ScanRoamingData { get => _settings.ScanRoamingData; set { if (_settings.ScanRoamingData != value) { _settings.ScanRoamingData = value; SettingsChanged(nameof(ScanRoamingData), true); } } }
    public bool ScanLocalData { get => _settings.ScanLocalData; set { if (_settings.ScanLocalData != value) { _settings.ScanLocalData = value; SettingsChanged(nameof(ScanLocalData), true); } } }
    public bool ScanUserProfile { get => _settings.ScanUserProfile; set { if (_settings.ScanUserProfile != value) { _settings.ScanUserProfile = value; SettingsChanged(nameof(ScanUserProfile), true); } } }
    public bool AdvancedFeaturesEnabled
    {
        get => _settings.AdvancedFeaturesEnabled;
        set
        {
            if (_settings.AdvancedFeaturesEnabled == value) return;
            _settings.AdvancedFeaturesEnabled = value;
            SettingsChanged(nameof(AdvancedFeaturesEnabled));
            if (!value && SelectedPage is 2 or 3) SelectedPage = 5;
        }
    }
    public bool AdvancedToolsEnabled
    {
        get => _settings.AdvancedToolsEnabled;
        set
        {
            if (_settings.AdvancedToolsEnabled == value) return;
            _settings.AdvancedToolsEnabled = value;
            SettingsChanged(nameof(AdvancedToolsEnabled));
            OnPropertyChanged(nameof(AdvancedToolsDisabled));
            if (!value && SelectedPage == 4) SelectedPage = 5;
            VacuumCommand.NotifyCanExecuteChanged();
        }
    }
    public bool AdvancedToolsDisabled => !AdvancedToolsEnabled;
    public CleanerTheme Theme { get => _settings.Theme; set { if (_settings.Theme != value) { _settings.Theme = value; SettingsChanged(nameof(Theme)); App.ApplyTheme(value); } } }
    public string SettingsStatus { get; private set; } = "设置尚未保存";
    public StatusSeverity SettingsStatusSeverity { get => _settingsStatusSeverity; private set => SetProperty(ref _settingsStatusSeverity, value); }
    public bool IsSettingsDirty { get => _isSettingsDirty; private set { if (SetProperty(ref _isSettingsDirty, value)) SaveSettingsCommand.NotifyCanExecuteChanged(); } }

    public Task InitializeAsync() => TrackActivityAsync(InitializeCoreAsync);

    public Task ScanForTestingAsync(CancellationToken cancellationToken = default) =>
        TrackActivityAsync(() => ScanAsync(cancellationToken));

    private async Task InitializeCoreAsync()
    {
        _loadingSettings = true;
        try
        {
            _settings = await _settingsService.LoadAsync(_lifetimeCancellation.Token);
            _retentionDays = _settings.RetentionDays is 7 or 30 or 90 ? _settings.RetentionDays : 30;
            _settings.RetentionDays = _retentionDays;
            App.ApplyTheme(_settings.Theme);
            NotifyAllSettings();
            IsSettingsDirty = false;
            SettingsStatus = $"已加载设置（无效文件会使用默认值并记录日志）：{_settingsService.SettingsPath}";
            SettingsStatusSeverity = StatusSeverity.Normal;
            OnPropertyChanged(nameof(SettingsStatus));
            OnPropertyChanged(nameof(DataDirectory));
            RefreshCursorState();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception ex) { await ReportErrorAsync("settings.initialize", "加载设置失败", ex); }
        finally { _loadingSettings = false; }
    }

    private async Task ScanAsync(CancellationToken commandToken)
    {
        CancelScopeRefresh();
        Interlocked.Increment(ref _scopeGeneration);
        IsScopeRefreshing = false;
        InvalidatePreview("重新扫描中，原预览已失效");
        _scanCancellation?.Dispose();
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(commandToken, _lifetimeCancellation.Token);
        var token = _scanCancellation.Token;
        IsScanning = true;
        ScanStatus = "正在扫描全部 Cursor 数据根";
        ScanStatusSeverity = StatusSeverity.Normal;
        CurrentScanPath = "准备中";
        ProgressFiles = 0;
        ProgressBytes = 0;
        ProgressPercent = 0;
        RefreshCursorState();
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressFiles = p.FilesScanned;
            ProgressBytes = p.BytesScanned;
            CurrentScanPath = p.CurrentPath ?? $"已完成 {p.RootsCompleted}/{p.TotalRoots} 个根目录";
            ProgressPercent = p.TotalRoots == 0 ? 0 : p.RootsCompleted * 100d / p.TotalRoots;
        });
        try
        {
            var result = await _scanner.ScanAsync(progress, token);
            var snapshot = await BuildCurrentScopeSnapshotAsync(result, token);
            token.ThrowIfCancellationRequested();
            CommitSnapshot(result, snapshot, true);
            ProgressFiles = result.Summary.TotalFiles;
            ProgressBytes = result.Summary.TotalBytes;
            ProgressPercent = 100;
            LastScanText = result.CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            ScanStatus = $"扫描完成：显示 {ScanItems.Count:N0} / 实际扫描 {result.Summary.TotalFiles:N0} 个文件，错误 {result.Summary.ErrorCount:N0}";
            ScanStatusSeverity = result.Summary.ErrorCount > 0 ? StatusSeverity.Warning : StatusSeverity.Success;
            CurrentScanPath = "扫描完成";
            await _log.WriteAsync("info", "ui.scan", ScanStatus);
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "扫描已取消";
            ScanStatusSeverity = StatusSeverity.Warning;
            CurrentScanPath = "已保留上一次完整扫描结果";
            await TryLogAsync("info", "ui.scan", "Scan cancelled; previous snapshot retained.");
        }
        catch (Exception ex)
        {
            ScanStatus = $"扫描失败：{ex.Message}";
            ScanStatusSeverity = StatusSeverity.Error;
            CurrentScanPath = "已保留上一次完整扫描结果，请查看日志";
            await ReportErrorAsync("ui.scan", "扫描失败", ex);
        }
        finally
        {
            IsScanning = false;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            RaiseCommands();
        }
    }

    private async Task<ViewSnapshot> BuildCurrentScopeSnapshotAsync(ScanResult fullResult, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var generation = Volatile.Read(ref _scopeGeneration);
            var scopedItems = fullResult.Items.Where(IsRootEnabled).ToArray();
            var scopedResult = new ScanResult(scopedItems, fullResult.Summary, fullResult.CompletedAtUtc);
            var workspaceTask = _workspaceAnalyzer.AnalyzeAsync(scopedResult, token);
            var sessionTask = _sessionAnalyzer.AnalyzeAsync(scopedResult, token);
            await Task.WhenAll(workspaceTask, sessionTask);
            if (generation == Volatile.Read(ref _scopeGeneration))
                return CreateViewSnapshot(scopedItems, await workspaceTask, await sessionTask);
        }
    }

    private static ViewSnapshot CreateViewSnapshot(IReadOnlyList<ScanItem> items, IReadOnlyList<WorkspaceInfo> workspaces, IReadOnlyList<SessionInfo> sessions) => new(
        items, workspaces, sessions,
        items.OrderByDescending(i => i.Size).Take(50).Select(i => new LargeFileInfo(i.FullPath, i.Size, i.Category, i.LastWriteTimeUtc)).ToArray(),
        items.Where(IsDatabase).ToArray(),
        sessions.Select(s => s.ProjectName).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.CurrentCultureIgnoreCase).Order().ToArray(),
        items.LongCount(), items.Sum(i => i.Size),
        items.Where(i => i.Category == DataCategory.ChatSession).Sum(i => i.Size),
        items.Where(i => i.Category == DataCategory.Workspace).Sum(i => i.Size),
        items.Where(i => i.Category == DataCategory.SQLite).Sum(i => i.Size),
        items.Where(i => i.Category == DataCategory.AgentTranscript).Sum(i => i.Size),
        items.Where(i => i.Category == DataCategory.Other).Sum(i => i.Size));

    private void CommitSnapshot(ScanResult fullResult, ViewSnapshot snapshot, bool updateStore)
    {
        var previousProject = SessionProject;
        using (ScanItemsView.DeferRefresh()) Replace(ScanItems, snapshot.Items);
        using (WorkspacesView.DeferRefresh()) Replace(Workspaces, snapshot.Workspaces);
        using (SessionsView.DeferRefresh()) Replace(Sessions, snapshot.Sessions);
        Replace(LargeFiles, snapshot.LargeFiles);
        Replace(Databases, snapshot.Databases);
        Replace(SessionProjects, new[] { AllProjects }.Concat(snapshot.Projects));
        SessionProject = previousProject is not null && SessionProjects.Contains(previousProject, StringComparer.CurrentCultureIgnoreCase)
            ? SessionProjects.First(p => string.Equals(p, previousProject, StringComparison.CurrentCultureIgnoreCase))
            : AllProjects;
        SelectedDatabase = Databases.FirstOrDefault();
        TotalFiles = snapshot.TotalFiles;
        TotalBytes = snapshot.TotalBytes;
        SessionBytes = snapshot.SessionBytes;
        WorkspaceBytes = snapshot.WorkspaceBytes;
        SqliteBytes = snapshot.SqliteBytes;
        AgentBytes = snapshot.AgentBytes;
        OtherBytes = snapshot.OtherBytes;
        CurrentUsage = TotalBytes;
        foreach (var name in new[] { nameof(TotalFiles), nameof(TotalBytes), nameof(SessionBytes), nameof(WorkspaceBytes), nameof(SqliteBytes), nameof(AgentBytes), nameof(OtherBytes) }) OnPropertyChanged(name);
        RefreshView(ScanItemsView, UpdateScanVisibleCount);
        RefreshView(SessionsView, UpdateSessionVisibleCount);
        RefreshView(WorkspacesView, UpdateWorkspaceVisibleCount);
        if (updateStore)
        {
            _store.Set(fullResult);
            _scanResult = fullResult;
            OnPropertyChanged(nameof(HasScanResult));
            OnPropertyChanged(nameof(IsScanFilterEmpty));
            OnPropertyChanged(nameof(IsSessionFilterEmpty));
            OnPropertyChanged(nameof(IsWorkspaceFilterEmpty));
        }
        ResetSessionPreview("扫描结果已更新，请重新选择会话预览内容");
        RaiseCommands();
    }

    private void GeneratePreview(HashSet<string>? selectedPaths)
    {
        if (_scanResult is null || selectedPaths is { Count: 0 }) return;
        try
        {
            var source = _scanResult;
            if (selectedPaths is not null)
            {
                var selected = source.Items.Where(i => selectedPaths.Contains(i.FullPath)).ToArray();
                source = new ScanResult(selected, source.Summary, source.CompletedAtUtc);
            }
            var cutoff = CleanupPolicyMode == CleanupPolicyMode.CutoffDate && CustomCutoff.HasValue
                ? CustomCutoff.Value.Date.ToUniversalTime()
                : DateTime.UtcNow.AddDays(-RetentionDays);
            var plan = _planner.CreatePlan(source, GetApprovedRoots(), cutoff);
            _cleanupPlan = plan.FileCount > 0 ? plan : null;
            PreviewFiles = plan.FileCount;
            PreviewBytes = plan.TotalSize;
            PreviewWorkspaces = plan.Items.Count(i => i.Category == DataCategory.Workspace);
            PreviewSessions = plan.Items.Count(i => i.Category is DataCategory.ChatSession or DataCategory.AgentTranscript);
            PreviewStatus = plan.FileCount == 0 ? "预览为空：没有符合保留策略和范围的文件" : $"预览已生成，截止 {cutoff.ToLocalTime():yyyy-MM-dd HH:mm}";
            SelectedPage = 3;
            OnPropertyChanged(nameof(HasCleanupPlan));
            CleanupCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) { InvalidatePreview($"无法生成预览：{ex.Message}"); }
    }

    private async Task DeleteSelectedSessionsAsync(object? parameter, CancellationToken token)
    {
        if (_scanResult is null) return;
        var sessions = (parameter as IList)?.OfType<SessionInfo>().ToArray() ?? [];
        if (sessions.Length == 0) return;
        var filePaths = sessions
            .Select(session => session.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conversationIds = sessions
            .SelectMany(session => session.DeletableConversationIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var databasePaths = GetChatDatabasePaths();

        CleanupPlan? plan = null;
        if (filePaths.Count > 0)
        {
            try
            {
                plan = _planner.CreateSelectedPlan(_scanResult, GetApprovedRoots(), filePaths);
            }
            catch (Exception ex)
            {
                OperationStatus = $"无法删除所选会话：{ex.Message}";
                OperationStatusSeverity = StatusSeverity.Error;
                if (!_closeRequested) await _dialogs.ShowErrorAsync("无法删除所选会话", ex.Message, token);
                return;
            }
        }

        var fileCount = plan?.FileCount ?? 0;
        if (fileCount == 0 && conversationIds.Length == 0)
        {
            const string message = "所选会话没有可删除的文件，也没有可匹配的 SQLite 会话 ID。非 UUID 文件不会改数据库。";
            OperationStatus = message;
            OperationStatusSeverity = StatusSeverity.Warning;
            if (!_closeRequested) await _dialogs.ShowErrorAsync("无法删除所选会话", message, token);
            return;
        }

        if (_process.IsCursorRunning())
        {
            await _dialogs.ShowErrorAsync("无法删除所选会话", "Cursor 正在运行。请关闭 Cursor 后再删除所选会话。", token);
            InvalidatePreview("Cursor 运行中，预览已失效");
            return;
        }

        var mode = UseRecycleBin ? "回收站" : "永久删除";
        var backup = AutomaticBackup ? "会话文件先自动备份" : "会话文件不创建备份";
        var sqliteLine = conversationIds.Length == 0
            ? "所选会话没有可匹配的 SQLite ID，不会修改数据库。"
            : $"同时删除 SQLite 中 {conversationIds.Length:N0} 个会话 ID 的聊天记录；数据库会先做在线备份（不受文件备份开关影响）。";
        var fileLine = fileCount == 0
            ? "没有可回收的会话文件。"
            : $"将处理 {fileCount:N0} 个会话文件，预计释放 {ByteSizeFormatter.Format(plan!.TotalSize)}。";
        var confirmed = await _dialogs.ConfirmAsync(
            "确认删除所选会话",
            $"{fileLine}\n{sqliteLine}\n模式：{backup}，{mode}。\n请保持 Cursor 关闭。此操作只能执行一次，是否继续？",
            token);
        if (!confirmed) return;

        IsBusy = true;
        IsCleaning = true;
        BusyText = "正在删除所选会话，取消后可能已有部分内容完成处理";
        OperationStatus = "正在删除所选会话";
        OperationStatusSeverity = StatusSeverity.Normal;
        InvalidatePreview("正在执行清理，原预览已失效");
        CleanupOperationResult? fileResult = null;
        SqliteChatCleanupResult? sqliteResult = null;
        try
        {
            if (conversationIds.Length > 0)
            {
                sqliteResult = await _sqlite.DeleteChatRecordsAsync(conversationIds, databasePaths, GetApprovedRoots(), token);
                if (sqliteResult.Blocked)
                {
                    OperationStatus = $"SQLite 聊天删除被阻止：{sqliteResult.Error}";
                    OperationStatusSeverity = StatusSeverity.Error;
                    if (!_closeRequested)
                        await _dialogs.ShowErrorAsync("无法删除所选会话", sqliteResult.Error ?? "Cursor 运行中，数据库未被修改。", CancellationToken.None);
                    return;
                }
            }

            if (fileCount > 0 && plan is not null)
            {
                fileResult = await _cleanup.ExecuteAsync(
                    plan,
                    true,
                    new CleanupOptions(AutomaticBackup, UseRecycleBin ? CleanupDisposition.Recycle : CleanupDisposition.PermanentDelete),
                    token);
                CleanedFiles = fileResult.DeletedFiles;
                CleanedBytes = fileResult.ReclaimedBytes;
                if (fileResult.Blocked)
                {
                    OperationStatus = ComposeSessionDeleteStatus("清理被阻止", fileResult, sqliteResult);
                    OperationStatusSeverity = StatusSeverity.Error;
                    if (fileResult.Items.Count > 0)
                        await RescanAfterCleanupAsync(fileResult, sqliteResult);
                    if (!_closeRequested)
                        await _dialogs.ShowErrorAsync("清理被阻止", fileResult.Error ?? "服务拒绝继续清理。", CancellationToken.None);
                    return;
                }
            }

            var outcome = fileResult?.Cancelled == true ? "清理已取消" : "清理完成";
            OperationStatus = ComposeSessionDeleteStatus(outcome, fileResult, sqliteResult);
            OperationStatusSeverity = fileResult?.Cancelled == true || (fileResult?.Items.Count(item => !item.Succeeded) > 0) || sqliteResult is { Succeeded: false }
                ? StatusSeverity.Warning
                : StatusSeverity.Success;
            await RescanAfterCleanupAsync(fileResult ?? new CleanupOperationResult(plan?.Id ?? Guid.Empty, true, false, false, []), sqliteResult);
        }
        catch (OperationCanceledException)
        {
            OperationStatus = ComposeSessionDeleteStatus("清理已取消", fileResult, sqliteResult);
            OperationStatusSeverity = StatusSeverity.Warning;
            await RescanAfterCleanupAsync(
                fileResult ?? new CleanupOperationResult(plan?.Id ?? Guid.Empty, false, false, true, [], "Cleanup was cancelled."),
                sqliteResult);
        }
        catch (Exception ex)
        {
            OperationStatus = $"清理失败：{ex.Message}；可打开日志查看详情";
            OperationStatusSeverity = StatusSeverity.Error;
            await ReportErrorAsync("ui.cleanup", "清理失败", ex);
        }
        finally
        {
            IsCleaning = false;
            IsBusy = false;
            BusyText = string.Empty;
            RaiseCommands();
        }
    }

    private Task CleanupAsync(CancellationToken token) =>
        ExecuteCleanupAsync(
            _cleanupPlan,
            "无法清理",
            "Cursor 正在运行。请关闭 Cursor 后重新生成预览。",
            "确认开始清理",
            token);

    private async Task ExecuteCleanupAsync(
        CleanupPlan? plan,
        string blockedTitle,
        string cursorMessage,
        string confirmTitle,
        CancellationToken token)
    {
        if (plan?.FileCount is not > 0) return;
        if (_process.IsCursorRunning())
        {
            await _dialogs.ShowErrorAsync(blockedTitle, cursorMessage, token);
            InvalidatePreview("Cursor 运行中，预览已失效");
            return;
        }
        var mode = UseRecycleBin ? "回收站" : "永久删除";
        var backup = AutomaticBackup ? "先自动备份" : "不创建备份";
        var confirmed = await _dialogs.ConfirmAsync(confirmTitle, $"将处理 {plan.FileCount:N0} 个文件，预计释放 {ByteSizeFormatter.Format(plan.TotalSize)}。\n模式：{backup}，{mode}。\n此清理计划只能执行一次，是否继续？", token);
        if (!confirmed) return;
        IsBusy = true;
        IsCleaning = true;
        BusyText = "正在清理，取消后可能已有部分文件完成处理";
        OperationStatus = "正在执行清理";
        OperationStatusSeverity = StatusSeverity.Normal;
        InvalidatePreview("正在执行清理，原预览已失效");
        try
        {
            var result = await _cleanup.ExecuteAsync(plan, true, new CleanupOptions(AutomaticBackup, UseRecycleBin ? CleanupDisposition.Recycle : CleanupDisposition.PermanentDelete), token);
            CleanedFiles = result.DeletedFiles;
            CleanedBytes = result.ReclaimedBytes;
            var failedFiles = result.Items.Count(item => !item.Succeeded);
            if (result.Blocked)
            {
                OperationStatus = $"清理被阻止：已删除 {CleanedFiles:N0}，失败 {failedFiles:N0}，释放 {ByteSizeFormatter.Format(CleanedBytes)}；{result.Error}；可打开日志查看详情";
                OperationStatusSeverity = StatusSeverity.Error;
                if (result.Items.Count > 0)
                    await RescanAfterCleanupAsync(result);
                if (!_closeRequested)
                    await _dialogs.ShowErrorAsync("清理被阻止", result.Error ?? "服务拒绝继续清理。", CancellationToken.None);
                return;
            }
            OperationStatus = result.Cancelled
                ? $"清理已取消：已删除 {CleanedFiles:N0}，失败 {failedFiles:N0}，释放 {ByteSizeFormatter.Format(CleanedBytes)}；可能已有部分文件完成处理，可打开日志查看详情"
                : $"清理完成：已删除 {CleanedFiles:N0}，失败 {failedFiles:N0}，释放 {ByteSizeFormatter.Format(CleanedBytes)}；正在重新扫描";
            OperationStatusSeverity = result.Cancelled || failedFiles > 0 ? StatusSeverity.Warning : StatusSeverity.Success;
            await RescanAfterCleanupAsync(result);
        }
        catch (OperationCanceledException)
        {
            OperationStatus = $"清理已取消：已删除 {CleanedFiles:N0}，释放 {ByteSizeFormatter.Format(CleanedBytes)}；可能已有部分文件完成处理，可打开日志查看详情";
            OperationStatusSeverity = StatusSeverity.Warning;
            await RescanAfterCleanupAsync(new CleanupOperationResult(plan.Id, false, false, true, [], "Cleanup was cancelled."));
        }
        catch (Exception ex)
        {
            OperationStatus = $"清理失败：{ex.Message}；可打开日志查看详情";
            OperationStatusSeverity = StatusSeverity.Error;
            await ReportErrorAsync("ui.cleanup", "清理失败", ex);
        }
        finally
        {
            IsCleaning = false;
            IsBusy = false;
            BusyText = string.Empty;
            RaiseCommands();
        }
    }

    private async Task RescanAfterCleanupAsync(CleanupOperationResult cleanupResult, SqliteChatCleanupResult? sqliteResult = null)
    {
        var failedFiles = cleanupResult.Items.Count(item => !item.Succeeded);
        var token = _lifetimeCancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await _scanner.ScanAsync(null, token);
            var snapshot = await BuildCurrentScopeSnapshotAsync(result, token);
            token.ThrowIfCancellationRequested();
            CommitSnapshot(result, snapshot, true);
            LastScanText = result.CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var outcome = cleanupResult.Blocked ? "清理被阻止" : cleanupResult.Cancelled ? "清理已取消" : "清理完成";
            OperationStatus = $"{ComposeSessionDeleteStatus(outcome, cleanupResult, sqliteResult)}，当前占用 {ByteSizeFormatter.Format(CurrentUsage)}；可打开日志查看详情";
            OperationStatusSeverity = cleanupResult.Blocked || sqliteResult?.Blocked == true
                ? StatusSeverity.Error
                : cleanupResult.Cancelled || failedFiles > 0 || sqliteResult is { Succeeded: false }
                    ? StatusSeverity.Warning
                    : StatusSeverity.Success;
        }
        catch (OperationCanceledException) when (_closeRequested || _lifetimeCancellation.IsCancellationRequested)
        {
            var outcome = cleanupResult.Blocked ? "清理被阻止" : cleanupResult.Cancelled ? "清理已取消" : "清理完成";
            OperationStatus = $"{ComposeSessionDeleteStatus(outcome, cleanupResult, sqliteResult)}；重新扫描已取消，保留上次快照；可打开日志查看详情";
            OperationStatusSeverity = StatusSeverity.Warning;
        }
        catch (Exception ex)
        {
            OperationStatus = $"{ComposeSessionDeleteStatus("清理结果", cleanupResult, sqliteResult)}；重新扫描失败，保留上次快照；可打开日志查看详情";
            OperationStatusSeverity = StatusSeverity.Error;
            await TryLogAsync("error", "ui.cleanup.rescan", ex.Message, ex);
        }
    }

    private static string ComposeSessionDeleteStatus(
        string outcome,
        CleanupOperationResult? fileResult,
        SqliteChatCleanupResult? sqliteResult)
    {
        var failedFiles = fileResult?.Items.Count(item => !item.Succeeded) ?? 0;
        var filePart = fileResult is null
            ? "未删除会话文件"
            : $"已删除文件 {fileResult.DeletedFiles:N0}，失败 {failedFiles:N0}，释放 {ByteSizeFormatter.Format(fileResult.ReclaimedBytes)}";
        var sqlitePart = sqliteResult is null
            ? "未修改 SQLite"
            : sqliteResult.Blocked
                ? $"SQLite 被阻止：{sqliteResult.Error}"
                : $"SQLite 删除 {sqliteResult.DeletedRows:N0} 行，失败库 {sqliteResult.FailedDatabases:N0}";
        if (sqliteResult is { Succeeded: false, Error: not null } && !sqliteResult.Blocked)
        {
            sqlitePart += $"：{sqliteResult.Error}";
        }

        return $"{outcome}：{filePart}；{sqlitePart}";
    }

    private async Task VacuumAsync(CancellationToken token)
    {
        var database = SelectedDatabase;
        if (database is null) return;
        if (_process.IsCursorRunning()) { await _dialogs.ShowErrorAsync("无法维护数据库", "Cursor 正在运行，SQLite 维护被阻止。", token); return; }
        var confirmed = await _dialogs.ConfirmAsync("确认 SQLite 维护", $"将对以下数据库执行完整性检查、强制备份和 VACUUM：\n{database.FullPath}\n\n操作期间请保持 Cursor 关闭。", token);
        if (!confirmed) return;
        IsBusy = true;
        BusyText = "正在检查、备份并维护数据库";
        _isVacuuming = true;
        SqliteStatus = "正在检查、备份并维护数据库";
        SqliteStatusSeverity = StatusSeverity.Normal;
        try
        {
            var result = await _sqlite.VacuumAsync(database.FullPath, GetApprovedRoots(), token);
            SqliteBefore = result.SizeBefore;
            SqliteAfter = result.SizeAfter;
            OnPropertyChanged(nameof(SqliteReclaimed));
            SqliteBackupPath = result.BackupPath;
            SqliteStatus = result.Succeeded ? $"维护完成，释放 {ByteSizeFormatter.Format(result.ReclaimedBytes)}" : $"维护失败：{result.Error}";
            SqliteStatusSeverity = result.Succeeded ? StatusSeverity.Success : StatusSeverity.Error;
            if (!result.Succeeded && !_closeRequested) await _dialogs.ShowErrorAsync("SQLite 维护失败", result.Error ?? "未知错误", CancellationToken.None);
        }
        catch (OperationCanceledException) { SqliteStatus = "数据库维护在 VACUUM 开始前已取消"; SqliteStatusSeverity = StatusSeverity.Warning; }
        catch (Exception ex)
        {
            SqliteStatus = $"维护失败：{ex.Message}";
            SqliteStatusSeverity = StatusSeverity.Error;
            if (!_closeRequested) await ReportErrorAsync("ui.sqlite", "SQLite 维护失败", ex);
            else await TryLogAsync("error", "ui.sqlite", ex.Message, ex);
        }
        finally
        {
            _isVacuuming = false;
            IsBusy = false;
            BusyText = string.Empty;
            RaiseCommands();
        }
    }

    private async Task SaveSettingsAsync(CancellationToken token)
    {
        var savedVersion = Volatile.Read(ref _settingsVersion);
        var snapshot = new OperationSettings
        {
            RetentionDays = RetentionDays,
            AutomaticBackup = AutomaticBackup,
            UseRecycleBin = UseRecycleBin,
            ScanRoamingData = ScanRoamingData,
            ScanLocalData = ScanLocalData,
            ScanUserProfile = ScanUserProfile,
            AdvancedFeaturesEnabled = AdvancedFeaturesEnabled,
            AdvancedToolsEnabled = AdvancedToolsEnabled,
            Theme = Theme
        };
        IsBusy = true;
        BusyText = "正在保存设置";
        SettingsStatusSeverity = StatusSeverity.Normal;
        try
        {
            await _settingsService.SaveAsync(snapshot, token);
            IsSettingsDirty = savedVersion != Volatile.Read(ref _settingsVersion);
            SettingsStatus = IsSettingsDirty
                ? $"已保存先前版本，仍有未保存更改：{_settingsService.SettingsPath}"
                : $"已保存：{_settingsService.SettingsPath}";
            SettingsStatusSeverity = IsSettingsDirty ? StatusSeverity.Warning : StatusSeverity.Success;
            OnPropertyChanged(nameof(SettingsStatus));
        }
        catch (OperationCanceledException) { SettingsStatus = "设置保存已取消，仍有未保存更改"; SettingsStatusSeverity = StatusSeverity.Warning; OnPropertyChanged(nameof(SettingsStatus)); }
        catch (Exception ex) { SettingsStatus = $"保存失败，仍有未保存更改：{ex.Message}"; SettingsStatusSeverity = StatusSeverity.Error; OnPropertyChanged(nameof(SettingsStatus)); await ReportErrorAsync("ui.settings", "保存设置失败", ex); }
        finally { IsBusy = false; BusyText = string.Empty; }
    }

    public void NotifySelectionChanged(IList? selectedSessions = null, IList? selectedWorkspaces = null)
    {
        if (selectedSessions is not null)
        {
            var sessions = selectedSessions.OfType<SessionInfo>().ToArray();
            SelectedSessionCount = sessions.Length;
            QueueSessionPreview(sessions.Length == 1 ? sessions[0] : null, sessions.Length);
        }
        if (selectedWorkspaces is not null) SelectedWorkspaceCount = selectedWorkspaces.OfType<WorkspaceInfo>().Count();
        DeleteSelectedSessionsCommand.NotifyCanExecuteChanged();
        GenerateSelectedWorkspacePreviewCommand.NotifyCanExecuteChanged();
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (_disposed) return true;
        if (IsSettingsDirty)
        {
            var discard = await _dialogs.ConfirmAsync("未保存的设置", "放弃未保存更改并关闭？", CancellationToken.None);
            if (!discard)
            {
                _closeRequested = false;
                ClosingStatus = string.Empty;
                RaiseCommands();
                return false;
            }
        }

        _closeRequested = true;
        ClosingStatus = _isVacuuming ? "正在完成数据库维护，完成后将关闭" : "正在取消活动操作并等待关闭";
        RaiseCommands();
        _lifetimeCancellation.Cancel();
        _scanCancellation?.Cancel();
        CancelScopeRefresh();
        ScanCommand.Cancel();
        CleanupCommand.Cancel();
        SaveSettingsCommand.Cancel();
        VacuumCommand.Cancel();

        while (true)
        {
            Task[] pending;
            lock (_activityGate) pending = _activities.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) break;
            if (_isVacuuming) ClosingStatus = "正在完成数据库维护，完成后将关闭";
            try { await Task.WhenAll(pending); } catch { }
        }
        Dispose();
        return true;
    }

    private bool FilterScanItem(object value)
    {
        if (value is not ScanItem item) return false;
        var category = SelectedCategory switch { "历史会话" => item.Category == DataCategory.ChatSession, "Workspace" => item.Category == DataCategory.Workspace, "SQLite" => item.Category == DataCategory.SQLite, "Agent Transcripts" => item.Category == DataCategory.AgentTranscript, "其他" => item.Category == DataCategory.Other, _ => true };
        return category && (string.IsNullOrWhiteSpace(SearchText) || item.FullPath.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    }
    private bool FilterSession(object value) => value is SessionInfo s && (string.IsNullOrWhiteSpace(SessionProject) || SessionProject == AllProjects || string.Equals(s.ProjectName, SessionProject, StringComparison.CurrentCultureIgnoreCase)) && (string.IsNullOrWhiteSpace(SessionSearch) || s.Title.Contains(SessionSearch, StringComparison.CurrentCultureIgnoreCase) || (s.ProjectName?.Contains(SessionSearch, StringComparison.CurrentCultureIgnoreCase) ?? false) || s.DisplayPath.Contains(SessionSearch, StringComparison.CurrentCultureIgnoreCase));
    private bool FilterWorkspace(object value) => value is WorkspaceInfo w && (string.IsNullOrWhiteSpace(WorkspaceSearch) || w.DisplayName.Contains(WorkspaceSearch, StringComparison.CurrentCultureIgnoreCase) || w.WorkspacePath.Contains(WorkspaceSearch, StringComparison.CurrentCultureIgnoreCase) || (w.ProjectPath?.Contains(WorkspaceSearch, StringComparison.CurrentCultureIgnoreCase) ?? false));

    private static bool HasSelectedSessions(object? parameter) => parameter is IList selected && selected.OfType<SessionInfo>().Any();
    private static bool HasSelectedWorkspaces(object? parameter) => parameter is IList selected && selected.OfType<WorkspaceInfo>().Any();
    private static HashSet<string> GetSelectedSessionPaths(IList? selected) => selected?.OfType<SessionInfo>().Select(s => s.FilePath).Where(path => !string.IsNullOrWhiteSpace(path)).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    private string[] GetChatDatabasePaths() =>
        _scanResult?.Items
            .Where(item => item.Category == DataCategory.SQLite && CursorChatSchema.IsChatDatabaseName(item.FullPath))
            .Select(item => item.FullPath)
            .Distinct(PathSafety.PathComparer)
            .ToArray() ?? [];
    private HashSet<string> GetSelectedWorkspacePaths(IList? selected)
    {
        var prefixes = selected?.OfType<WorkspaceInfo>().Select(w => w.WorkspacePath).ToArray() ?? [];
        return _scanResult?.Items.Where(i => prefixes.Any(p => PathSafety.IsWithin(i.FullPath, p, false))).Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    }
    private bool IsRootEnabled(ScanItem item) => IsRootEnabled(item.Root);
    private bool IsRootEnabled(CursorDataRoot root)
    {
        if (root.Kind == RootKind.RoamingData) return ScanRoamingData;
        if (root.Kind == RootKind.LocalData) return ScanLocalData;
        if (root.Kind == RootKind.UserProfile) return ScanUserProfile;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return PathSafety.IsWithin(root.Path, roaming) ? ScanRoamingData : PathSafety.IsWithin(root.Path, local) ? ScanLocalData : ScanUserProfile;
    }
    private string[] GetApprovedRoots() => _pathService.GetDataRoots().Where(IsRootEnabled).Select(r => r.Path).ToArray();
    private static bool IsDatabase(ScanItem item)
    {
        if (item.Category != DataCategory.SQLite) return false;
        var extension = Path.GetExtension(item.FullPath);
        return extension.Equals(".vscdb", StringComparison.OrdinalIgnoreCase) || extension.Equals(".db", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        if (target is BulkObservableCollection<T> bulk)
        {
            bulk.ReplaceRange(source);
            return;
        }

        target.Clear();
        foreach (var item in source) target.Add(item);
    }
    private void InvalidatePreview(string status)
    {
        _cleanupPlan = null;
        PreviewFiles = 0;
        PreviewWorkspaces = 0;
        PreviewSessions = 0;
        PreviewBytes = 0;
        PreviewStatus = status;
        OnPropertyChanged(nameof(HasCleanupPlan));
        CleanupCommand.NotifyCanExecuteChanged();
    }
    private void SettingsChanged(string property, bool refreshScope = false)
    {
        OnPropertyChanged(property);
        if (!_loadingSettings)
        {
            MarkSettingsDirty();
            InvalidatePreview("设置已更改，请保存并重新生成预览");
        }
        if (refreshScope && _scanResult is not null) StartScopeRefresh();
    }
    private void MarkSettingsDirty()
    {
        Interlocked.Increment(ref _settingsVersion);
        IsSettingsDirty = true;
        SettingsStatus = $"有未保存的设置更改：{_settingsService.SettingsPath}";
        SettingsStatusSeverity = StatusSeverity.Warning;
        OnPropertyChanged(nameof(SettingsStatus));
    }
    private void StartScopeRefresh()
    {
        CancelScopeRefresh();
        var generation = Interlocked.Increment(ref _scopeGeneration);
        var source = _scanResult;
        if (source is null) return;
        _scopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var token = _scopeCancellation.Token;
        IsScopeRefreshing = true;
        _ = TrackActivityAsync(() => RefreshScopeAsync(source, generation, token));
    }
    private async Task RefreshScopeAsync(ScanResult source, long generation, CancellationToken token)
    {
        try
        {
            var scopedItems = source.Items.Where(IsRootEnabled).ToArray();
            var scopedResult = new ScanResult(scopedItems, source.Summary, source.CompletedAtUtc);
            var workspaceTask = _workspaceAnalyzer.AnalyzeAsync(scopedResult, token);
            var sessionTask = _sessionAnalyzer.AnalyzeAsync(scopedResult, token);
            await Task.WhenAll(workspaceTask, sessionTask);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _scopeGeneration) || !ReferenceEquals(source, _scanResult) || IsScanning || _closeRequested) return;
            CommitSnapshot(source, CreateViewSnapshot(scopedItems, await workspaceTask, await sessionTask), false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { await ReportErrorAsync("ui.scope", "刷新扫描范围失败", ex); }
        finally
        {
            if (generation == Volatile.Read(ref _scopeGeneration)) IsScopeRefreshing = false;
        }
    }
    private void QueueSessionPreview(SessionInfo? session, int selectedCount)
    {
        if (session is null)
        {
            ResetSessionPreview(selectedCount > 1
                ? "已选择多项。请只选择一个会话以预览内容。"
                : "选择一个会话以预览内容");
            return;
        }

        if (ReferenceEquals(_previewedSession, session) && HasSessionPreview)
        {
            return;
        }

        CancelSessionPreview();
        var generation = Interlocked.Increment(ref _sessionPreviewGeneration);
        _previewedSession = session;
        HasSessionPreview = false;
        SessionPreviewTruncated = false;
        Replace(SessionPreviewMessages, []);
        SessionPreviewTitle = session.Title;
        SessionPreviewStatus = string.IsNullOrWhiteSpace(session.FilePath)
            ? "该会话仅存在于 SQLite，当前只读预览不解析数据库正文。"
            : "正在只读加载会话内容";
        if (string.IsNullOrWhiteSpace(session.FilePath))
        {
            HasSessionPreview = false;
            return;
        }

        _sessionPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var token = _sessionPreviewCancellation.Token;
        _ = TrackActivityAsync(() => LoadSessionPreviewAsync(session, generation, token));
    }

    private async Task LoadSessionPreviewAsync(SessionInfo session, long generation, CancellationToken token)
    {
        try
        {
            var preview = await _sessionContent.ReadAsync(session.FilePath, token);
            if (generation != Volatile.Read(ref _sessionPreviewGeneration) || _closeRequested)
            {
                return;
            }

            Replace(SessionPreviewMessages, preview.Messages);
            HasSessionPreview = preview.Messages.Count > 0 && preview.Error is null;
            SessionPreviewTruncated = preview.Truncated;
            SessionPreviewTitle = session.Title;
            SessionPreviewStatus = preview.Error
                ?? (preview.Messages.Count == 0
                    ? "未从该文件解析出可显示的对话文本。聊天内容可能保存在 SQLite 中，当前仅预览 JSON/JSONL。"
                    : preview.Truncated
                        ? $"已显示前 {preview.Messages.Count:N0} 条消息，内容已截断。"
                        : $"已显示 {preview.Messages.Count:N0} 条消息。预览为只读。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != Volatile.Read(ref _sessionPreviewGeneration) || _closeRequested)
            {
                return;
            }

            Replace(SessionPreviewMessages, []);
            HasSessionPreview = false;
            SessionPreviewTruncated = false;
            SessionPreviewStatus = $"无法预览会话内容：{ex.Message}";
            await TryLogAsync("error", "ui.session.preview", ex.Message, ex);
        }
    }

    private void ResetSessionPreview(string status)
    {
        CancelSessionPreview();
        Interlocked.Increment(ref _sessionPreviewGeneration);
        _previewedSession = null;
        HasSessionPreview = false;
        SessionPreviewTruncated = false;
        Replace(SessionPreviewMessages, []);
        SessionPreviewTitle = "选择一个会话以预览内容";
        SessionPreviewStatus = status;
    }

    private void CancelSessionPreview()
    {
        try { _sessionPreviewCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _sessionPreviewCancellation?.Dispose();
        _sessionPreviewCancellation = null;
    }

    private void CancelScopeRefresh()
    {
        try { _scopeCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _scopeCancellation?.Dispose();
        _scopeCancellation = null;
        IsScopeRefreshing = false;
    }
    private async Task TrackActivityAsync(Func<Task> operation)
    {
        var task = operation();
        lock (_activityGate) _activities.Add(task);
        try { await task; }
        finally { lock (_activityGate) _activities.Remove(task); }
    }
    private void NotifyAllSettings()
    {
        foreach (var property in new[] { nameof(RetentionDays), nameof(AutomaticBackup), nameof(UseRecycleBin), nameof(ScanRoamingData), nameof(ScanLocalData), nameof(ScanUserProfile), nameof(AdvancedFeaturesEnabled), nameof(AdvancedToolsEnabled), nameof(AdvancedToolsDisabled), nameof(Theme) }) OnPropertyChanged(property);
    }
    private void RefreshCursorState()
    {
        var running = IsCursorRunning;
        CursorStatusSeverity = running ? StatusSeverity.Warning : StatusSeverity.Success;
        OnPropertyChanged(nameof(IsCursorRunning));
        OnPropertyChanged(nameof(CursorStateText));
    }
    private void RaiseCommands()
    {
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
        GeneratePreviewCommand.NotifyCanExecuteChanged();
        GenerateSelectedWorkspacePreviewCommand.NotifyCanExecuteChanged();
        DeleteSelectedSessionsCommand.NotifyCanExecuteChanged();
        CleanupCommand.NotifyCanExecuteChanged();
        CancelCleanupCommand.NotifyCanExecuteChanged();
        VacuumCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }
    private void CancelScan() { _scanCancellation?.Cancel(); ScanCommand.Cancel(); }
    private void DebounceRefresh(ICollectionView view, ref CancellationTokenSource? cancellation, Action updateCount)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var token = cancellation.Token;
        _ = DebounceRefreshAsync(view, updateCount, token);
    }
    private static async Task DebounceRefreshAsync(ICollectionView view, Action updateCount, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            token.ThrowIfCancellationRequested();
            RefreshView(view, updateCount);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static void RefreshView(ICollectionView view, Action updateCount)
    {
        view.Refresh();
        updateCount();
    }
    private void UpdateScanVisibleCount() => ScanVisibleCount = ScanItemsView.Cast<object>().Count();
    private void UpdateSessionVisibleCount() => SessionVisibleCount = SessionsView.Cast<object>().Count();
    private void UpdateWorkspaceVisibleCount() => WorkspaceVisibleCount = WorkspacesView.Cast<object>().Count();
    private void OpenDirectory(object? parameter)
    {
        if (parameter is ScanItem item) TryShell(() => _shell.SelectFile(item.FullPath));
        else if (parameter is LargeFileInfo large) TryShell(() => _shell.SelectFile(large.FullPath));
        else if (parameter is WorkspaceInfo workspace) TryShell(() => _shell.OpenDirectory(workspace.WorkspacePath));
        else if (parameter is SessionInfo session)
        {
            var target = session.DisplayPath;
            if (string.IsNullOrWhiteSpace(target)) return;
            TryShell(() =>
            {
                if (File.Exists(target)) _shell.SelectFile(target);
                else _shell.OpenDirectory(target);
            });
        }
        else if (parameter is string path && !string.IsNullOrWhiteSpace(path)) TryShell(() => _shell.OpenDirectory(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!));
    }
    private async void TryShell(Action action) { try { action(); } catch (Exception ex) { await ReportErrorAsync("ui.shell", "无法打开路径", ex); } }
    private async Task ReportErrorAsync(string operation, string title, Exception ex)
    {
        await TryLogAsync("error", operation, ex.Message, ex);
        if (!_closeRequested) await _dialogs.ShowErrorAsync(title, ex.Message);
    }
    private async Task TryLogAsync(string level, string operation, string message, Exception? exception = null)
    {
        try { await _log.WriteAsync(level, operation, message, exception: exception); } catch { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _scanCancellation?.Cancel();
        _scanFilterCancellation?.Cancel();
        _sessionFilterCancellation?.Cancel();
        _workspaceFilterCancellation?.Cancel();
        CancelSessionPreview();
        CancelScopeRefresh();
        _scanCancellation?.Dispose();
        _scanFilterCancellation?.Dispose();
        _sessionFilterCancellation?.Dispose();
        _workspaceFilterCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed record ViewSnapshot(
        IReadOnlyList<ScanItem> Items,
        IReadOnlyList<WorkspaceInfo> Workspaces,
        IReadOnlyList<SessionInfo> Sessions,
        IReadOnlyList<LargeFileInfo> LargeFiles,
        IReadOnlyList<ScanItem> Databases,
        IReadOnlyList<string> Projects,
        long TotalFiles,
        long TotalBytes,
        long SessionBytes,
        long WorkspaceBytes,
        long SqliteBytes,
        long AgentBytes,
        long OtherBytes);
}
