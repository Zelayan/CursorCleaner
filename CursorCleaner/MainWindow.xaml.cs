using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CursorCleaner.Models;
using CursorCleaner.ViewModels;

namespace CursorCleaner;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private readonly MainViewModel _viewModel;
    private bool _closeApproved;
    private bool _closePending;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += Window_Loaded;
        SourceInitialized += (_, _) => ApplyDwmTheme(viewModel.Theme);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await _viewModel.InitializeAsync();
        ApplyDwmTheme(_viewModel.Theme);
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeApproved) return;

        e.Cancel = true;
        if (_closePending) return;

        _closePending = true;
        IsEnabled = false;
        try
        {
            if (await _viewModel.RequestCloseAsync())
            {
                _closeApproved = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
        finally
        {
            if (!_closeApproved)
            {
                _closePending = false;
                IsEnabled = true;
                Activate();
            }
        }
    }

    private void SessionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.NotifySelectionChanged(((DataGrid)sender).SelectedItems, null);

    private void WorkspaceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.NotifySelectionChanged(null, ((DataGrid)sender).SelectedItems);

    private void RetentionPolicy_Click(object sender, RoutedEventArgs e) =>
        _viewModel.CleanupPolicyMode = CleanupPolicyMode.RetentionPeriod;

    private void CutoffPolicy_Click(object sender, RoutedEventArgs e) =>
        _viewModel.CleanupPolicyMode = CleanupPolicyMode.CutoffDate;

    public void ApplyDwmTheme(CleanerTheme theme)
    {
        try
        {
            var effectiveDark = theme == CleanerTheme.Dark || theme == CleanerTheme.System && App.GetSystemThemeForWindow() == CleanerTheme.Dark;
            var value = effectiveDark ? 1 : 0;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (COMException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

public sealed class WpfDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = MessageBox.Show(Application.Current?.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MessageBox.Show(Application.Current?.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }
}
