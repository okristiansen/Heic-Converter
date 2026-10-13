using System.ComponentModel;
using System.Windows;
using HeicConverter.Services;
using HeicConverter.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace HeicConverter;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private bool _stoppingForClose;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        ApplicationThemeManager.ApplySystemTheme();
        InitializeComponent();
        Loaded += (_, _) => SystemThemeWatcher.Watch(this);
    }

    // ---- Drag & drop ------------------------------------------------------------------------------
    // The overlay covers the whole window while dragging, so DragLeave only fires when the cursor really leaves.

    private void OnWindowPreviewDragEnter(object sender, DragEventArgs e)
    {
        if (GetDroppedPaths(e) is { } paths && MainViewModel.ContainsSupportedPaths(paths))
            DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnOverlayDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnOverlayDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void OnOverlayDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (GetDroppedPaths(e) is { } paths)
            _viewModel.AddPaths(paths);
        e.Handled = true;
    }

    private static string[]? GetDroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    // ---- Closing ----------------------------------------------------------------------------------

    protected override async void OnClosing(CancelEventArgs e)
    {
        SettingsStore.Save(_viewModel.ToSettings());

        // Let in-flight conversions finish cleanly (no half-written files), then close for real.
        if (_viewModel.IsRunning && !_stoppingForClose)
        {
            e.Cancel = true;
            _stoppingForClose = true;
            await _viewModel.StopAndWaitAsync();
            Close();
            return;
        }

        base.OnClosing(e);
    }
}
