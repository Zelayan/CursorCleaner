using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CursorCleaner.Models;
using CursorCleaner.Services;
using CursorCleaner.ViewModels;

namespace CursorCleaner;

public partial class App : Application
{
    private ILogService? _log;
    private AvaloniaThemeService? _theme;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var pathService = new CursorPathService();
            var scanner = new CursorScannerService(pathService);
            var workspaceAnalyzer = new WorkspaceAnalyzerService();
            var store = new ScanResultStore();
            var process = new ProcessService();
            _log = new LogService();
            var sessionAnalyzer = new SessionAnalyzerService(_log);
            var settings = new SettingsService(_log);
            var roots = pathService.GetDataRoots().Select(root => root.Path).ToArray();
            var pathGuard = new PathGuard(roots);
            var planner = new CleanupPlannerService(pathGuard);
            var backup = new BackupService(_log);
            var recycleBin = RecycleBinService.CreateDefault();
            var cleanup = new CleanupService(process, pathGuard, backup, recycleBin, _log);
            var shell = new ShellService(_log);
            var sqlite = new SqliteService(process, pathGuard, backup, _log);
            var sessionContent = new SessionContentService(pathGuard);
            var dialogs = new AvaloniaDialogService();
            _theme = new AvaloniaThemeService(this);
            var viewModel = new MainViewModel(
                pathService, scanner, workspaceAnalyzer, sessionAnalyzer,
                store, process, _log, settings, planner, cleanup, shell, sqlite, dialogs, sessionContent, _theme);
            desktop.MainWindow = new MainWindow(viewModel);
            desktop.ShutdownRequested += (_, e) =>
            {
                _ = TryLogAsync("app.shutdown", null);
            };
        }

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _ = TryLogAsync("app.unobserved_task", e.Exception);
            e.SetObserved();
        };

        base.OnFrameworkInitializationCompleted();
    }

    private async Task TryLogAsync(string operation, Exception? exception)
    {
        if (_log is null) return;
        try
        {
            await _log.WriteAsync("error", operation, exception?.Message ?? operation, exception: exception);
        }
        catch
        {
        }
    }
}

public sealed class AvaloniaThemeService : IThemeService
{
    private readonly Application _app;

    public AvaloniaThemeService(Application app)
    {
        _app = app;
    }

    public void Apply(CleanerTheme theme)
    {
        if (theme == CleanerTheme.System)
        {
            _app.RequestedThemeVariant = ThemeVariant.Default;
        }
        else
        {
            _app.RequestedThemeVariant = theme == CleanerTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        var effective = theme == CleanerTheme.System ? GetSystemTheme() : theme;
        var dark = effective == CleanerTheme.Dark;
        SetBrush("WindowBrush", dark ? "#FF171717" : "#FFF4F4F4");
        SetBrush("SurfaceBrush", dark ? "#FF242424" : "#FFFFFFFF");
        SetBrush("SurfaceAltBrush", dark ? "#FF303030" : "#FFECECEC");
        SetBrush("SurfaceHoverBrush", dark ? "#FF3A3A3A" : "#FFE5E5E5");
        SetBrush("SurfacePressedBrush", dark ? "#FF454545" : "#FFDADADA");
        SetBrush("BorderBrush", dark ? "#FF626262" : "#FFB8B8B8");
        SetBrush("StrongBorderBrush", dark ? "#FF919191" : "#FF7A7A7A");
        SetBrush("TextBrush", dark ? "#FFF2F2F2" : "#FF1A1A1A");
        SetBrush("MutedTextBrush", dark ? "#FFC4C4C4" : "#FF565656");
        SetBrush("DisabledTextBrush", dark ? "#FF858585" : "#FF8A8A8A");
        SetBrush("AccentBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("AccentHoverBrush", dark ? "#FF7AD6FF" : "#FF005A9E");
        SetBrush("AccentPressedBrush", dark ? "#FF45B8EC" : "#FF004578");
        SetBrush("AccentSoftBrush", dark ? "#FF183B4D" : "#FFDCEEFF");
        SetBrush("AccentTextBrush", dark ? "#FF102027" : "#FFFFFFFF");
        SetBrush("WarningBrush", dark ? "#FFFFCC4D" : "#FF8A4B00");
        SetBrush("WarningBackgroundBrush", dark ? "#FF3D3215" : "#FFFFF4CE");
        SetBrush("ErrorBrush", dark ? "#FFFF8A80" : "#FFB10E1C");
        SetBrush("ErrorBackgroundBrush", dark ? "#FF481F24" : "#FFFDE7E9");
        SetBrush("SuccessBrush", dark ? "#FF79D279" : "#FF0F6B0F");
    }

    private void SetBrush(string key, string color)
    {
        _app.Resources[key] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }

    private static CleanerTheme GetSystemTheme()
    {
        try
        {
            if (Application.Current?.ActualThemeVariant == ThemeVariant.Dark)
            {
                return CleanerTheme.Dark;
            }
        }
        catch
        {
        }

        return CleanerTheme.Light;
    }
}
