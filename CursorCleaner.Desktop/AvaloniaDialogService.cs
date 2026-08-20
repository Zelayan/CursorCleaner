using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CursorCleaner.Models;

namespace CursorCleaner;

public sealed class AvaloniaDialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = GetMainWindow();
        if (window is null)
        {
            return false;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var yes = false;
        var text = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(20, 20, 20, 12) };
        var yesButton = new Button { Content = "确定", Classes = { "primary" }, MinWidth = 88 };
        var noButton = new Button { Content = "取消", MinWidth = 88 };
        yesButton.Click += (_, _) => { yes = true; dialog.Close(); };
        noButton.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Children =
            {
                text,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Margin = new Thickness(20, 0, 20, 16),
                    Spacing = 8,
                    Children = { noButton, yesButton }
                }
            }
        };
        await dialog.ShowDialog(window);
        return yes;
    }

    public async Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = GetMainWindow();
        if (window is null)
        {
            return;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var close = new Button { Content = "关闭", Classes = { "primary" }, MinWidth = 88, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16) };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(20) },
                close
            }
        };
        await dialog.ShowDialog(window);
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
