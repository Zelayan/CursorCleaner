using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CursorCleaner.Models;

namespace CursorCleaner;

public sealed class AvaloniaFolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string title, string? suggestedPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (window is null)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };
        if (!string.IsNullOrWhiteSpace(suggestedPath) && Directory.Exists(suggestedPath))
        {
            options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(suggestedPath);
        }

        var results = await window.StorageProvider.OpenFolderPickerAsync(options);
        cancellationToken.ThrowIfCancellationRequested();
        var chosen = results.FirstOrDefault();
        return chosen?.TryGetLocalPath() is { Length: > 0 } localPath ? localPath : null;
    }
}
