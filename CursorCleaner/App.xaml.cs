using System.Windows;
using System.Windows.Threading;
using CursorCleaner.Models;
using CursorCleaner.Services;
using CursorCleaner.ViewModels;
using Microsoft.Win32;

namespace CursorCleaner;

public partial class App : Application
{
    private ILogService? _log;
    private CleanerTheme _selectedTheme = CleanerTheme.System;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        var pathService = new CursorPathService();
        var scanner = new CursorScannerService(pathService);
        var workspaceAnalyzer = new WorkspaceAnalyzerService();
        var sessionAnalyzer = new SessionAnalyzerService();
        var store = new ScanResultStore();
        var process = new ProcessService();
        _log = new LogService();
        var settings = new SettingsService(_log);
        var roots = pathService.GetDataRoots().Select(root => root.Path).ToArray();
        var pathGuard = new PathGuard(roots);
        var planner = new CleanupPlannerService(pathGuard);
        var backup = new BackupService(_log);
        var recycleBin = new WindowsRecycleBinService();
        var cleanup = new CleanupService(process, pathGuard, backup, recycleBin, _log);
        var shell = new ShellService(_log);
        var sqlite = new SqliteService(process, pathGuard, backup, _log);
        var sessionContent = new SessionContentService(pathGuard);
        var dialogs = new WpfDialogService();
        var viewModel = new MainViewModel(pathService, scanner, workspaceAnalyzer, sessionAnalyzer,
            store, process, _log, settings, planner, cleanup, shell, sqlite, dialogs, sessionContent);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }

    public static void ApplyTheme(CleanerTheme theme)
    {
        if (Current is not App app) return;
        app._selectedTheme = theme;
        var effective = theme == CleanerTheme.System ? GetSystemTheme() : theme;
        var dictionaries = Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Resources/", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = new Uri(effective == CleanerTheme.Dark ? "Resources/Dark.xaml" : "Resources/Light.xaml", UriKind.Relative) };
        if (existing is null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(existing)] = replacement;
        ApplyHighContrastResources();
        if (Current.MainWindow is MainWindow window) window.ApplyDwmTheme(theme);
    }

    internal static CleanerTheme GetSystemThemeForWindow() => GetSystemTheme();

    private static void ApplyHighContrastResources()
    {
        if (Current is null) return;
        var keys = new[]
        {
            "WindowBrush", "SurfaceBrush", "SurfaceAltBrush", "SurfaceHoverBrush", "SurfacePressedBrush",
            "BorderBrush", "StrongBorderBrush", "TextBrush", "MutedTextBrush", "DisabledTextBrush",
            "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentSoftBrush", "AccentTextBrush",
            "WarningBrush", "WarningBackgroundBrush", "ErrorBrush", "ErrorBackgroundBrush", "SuccessBrush"
        };
        foreach (var key in keys) Current.Resources.Remove(key);
        if (!SystemParameters.HighContrast) return;
        Current.Resources["WindowBrush"] = SystemColors.WindowBrush;
        Current.Resources["SurfaceBrush"] = SystemColors.WindowBrush;
        Current.Resources["SurfaceAltBrush"] = SystemColors.ControlBrush;
        Current.Resources["SurfaceHoverBrush"] = SystemColors.HighlightBrush;
        Current.Resources["SurfacePressedBrush"] = SystemColors.HighlightBrush;
        Current.Resources["BorderBrush"] = SystemColors.WindowTextBrush;
        Current.Resources["StrongBorderBrush"] = SystemColors.WindowTextBrush;
        Current.Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Current.Resources["MutedTextBrush"] = SystemColors.GrayTextBrush;
        Current.Resources["DisabledTextBrush"] = SystemColors.GrayTextBrush;
        Current.Resources["AccentBrush"] = SystemColors.HighlightBrush;
        Current.Resources["AccentHoverBrush"] = SystemColors.HighlightBrush;
        Current.Resources["AccentPressedBrush"] = SystemColors.HighlightBrush;
        Current.Resources["AccentSoftBrush"] = SystemColors.HighlightBrush;
        Current.Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        Current.Resources["WarningBrush"] = SystemColors.WindowTextBrush;
        Current.Resources["WarningBackgroundBrush"] = SystemColors.InfoBrush;
        Current.Resources["ErrorBrush"] = SystemColors.WindowTextBrush;
        Current.Resources["ErrorBackgroundBrush"] = SystemColors.ControlBrush;
        Current.Resources["SuccessBrush"] = SystemColors.WindowTextBrush;
    }

    private static CleanerTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? CleanerTheme.Dark : CleanerTheme.Light;
        }
        catch { return CleanerTheme.Light; }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(() => ApplyTheme(_selectedTheme));
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = HandleDispatcherExceptionAsync(e.Exception);
    }

    private async Task HandleDispatcherExceptionAsync(Exception exception)
    {
        await TryLogAsync("app.dispatcher", exception);
        MessageBox.Show(MainWindow, $"应用发生未处理错误：\n{exception.Message}\n\n详细信息已尝试写入日志。", "Cursor Cleaner", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _ = TryLogAsync("app.unobserved_task", e.Exception);
        e.SetObserved();
    }

    private async Task TryLogAsync(string operation, Exception exception)
    {
        if (_log is null) return;
        try { await _log.WriteAsync("error", operation, exception.Message, exception: exception); } catch { }
    }
}
