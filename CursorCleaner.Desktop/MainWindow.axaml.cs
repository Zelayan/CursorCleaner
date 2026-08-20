using System.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CursorCleaner.ViewModels;

namespace CursorCleaner;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeApproved;
    private bool _closePending;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = null!;
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
        SessionsGrid.SelectionChanged += SessionsGrid_SelectionChanged;
        WorkspaceGrid.SelectionChanged += WorkspaceGrid_SelectionChanged;
        DeleteSelectedButton.CommandParameter = SessionsGrid.SelectedItems;
        GenerateSelectedWorkspaceButton.CommandParameter = WorkspaceGrid.SelectedItems;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        e.Cancel = true;
        if (_closePending)
        {
            return;
        }

        _closePending = true;
        IsEnabled = false;
        try
        {
            if (await _viewModel.RequestCloseAsync())
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            if (!_closeApproved)
            {
                _closePending = false;
                IsEnabled = true;
            }
        }
    }

    private void SessionsGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = SessionsGrid.SelectedItems;
        DeleteSelectedButton.CommandParameter = selected;
        _viewModel.NotifySelectionChanged(selected as IList ?? selected.Cast<object>().ToList(), null);
        _viewModel.DeleteSelectedSessionsCommand.NotifyCanExecuteChanged();
    }

    private void WorkspaceGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = WorkspaceGrid.SelectedItems;
        GenerateSelectedWorkspaceButton.CommandParameter = selected;
        _viewModel.NotifySelectionChanged(null, selected as IList ?? selected.Cast<object>().ToList());
        _viewModel.GenerateSelectedWorkspacePreviewCommand.NotifyCanExecuteChanged();
    }

    private void OpenSelectedLocation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || menu.Parent is not ContextMenu context || context.PlacementTarget is not DataGrid grid)
        {
            return;
        }

        _viewModel.OpenDirectoryCommand.Execute(grid.SelectedItem);
    }
}
