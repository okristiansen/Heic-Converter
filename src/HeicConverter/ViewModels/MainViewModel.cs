using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeicConverter.Services;
using Microsoft.Win32;

namespace HeicConverter.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly HeicImageConverter _converter = new();
    private readonly OutputPathAllocator _paths = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FileItemViewModel> _pending = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _runStopwatch = new();
    private CancellationTokenSource? _cts;
    private Task _runTask = Task.CompletedTask;
    private int _processedThisRun;
    private string? _lastOutputFolder;

    public MainViewModel(AppSettings settings)
    {
        Format = settings.Format;
        JpegQuality = Math.Clamp(settings.JpegQuality, 50, 100);
        CustomFolder = settings.CustomFolder;
        UseCustomFolder = settings.UseCustomFolder && !string.IsNullOrWhiteSpace(settings.CustomFolder);
        KeepMetadata = settings.KeepMetadata;
        MaxParallel = Math.Clamp(settings.MaxParallel, 1, MaxParallelLimit);

        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateSummary(), _dispatcher);
        Files.CollectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public int MaxParallelLimit { get; } = Math.Max(Environment.ProcessorCount, 2);
    public string CoreCountText => $"Your PC has {Environment.ProcessorCount} logical processors.";

    // ---- Settings ---------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpeg), nameof(IsPng))]
    public partial OutputFormat Format { get; set; }

    public bool IsJpeg
    {
        get => Format == OutputFormat.Jpeg;
        set { if (value) Format = OutputFormat.Jpeg; }
    }

    public bool IsPng
    {
        get => Format == OutputFormat.Png;
        set { if (value) Format = OutputFormat.Png; }
    }

    [ObservableProperty]
    public partial int JpegQuality { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveNextToOriginal))]
    public partial bool UseCustomFolder { get; set; }

    public bool SaveNextToOriginal
    {
        get => !UseCustomFolder;
        set => UseCustomFolder = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomFolderDisplay))]
    public partial string? CustomFolder { get; set; }

    public string CustomFolderDisplay => string.IsNullOrWhiteSpace(CustomFolder) ? "No folder selected" : CustomFolder;

    [ObservableProperty]
    public partial bool KeepMetadata { get; set; }

    [ObservableProperty]
    public partial int MaxParallel { get; set; }

    // ---- Progress / summary -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ClearAllCommand))]
    public partial bool IsRunning { get; private set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty] public partial bool HasFiles { get; private set; }
    [ObservableProperty] public partial int PendingCount { get; private set; }
    [ObservableProperty] public partial int DoneCount { get; private set; }
    [ObservableProperty] public partial int FailedCount { get; private set; }
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty] public partial string HeaderText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string TimeText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string StartButtonText { get; private set; } = "Convert";

    [ObservableProperty] public partial bool IsSummaryOpen { get; set; }
    [ObservableProperty] public partial string SummaryTitle { get; private set; } = string.Empty;
    [ObservableProperty] public partial string SummaryMessage { get; private set; } = string.Empty;
    [ObservableProperty] public partial Wpf.Ui.Controls.InfoBarSeverity SummarySeverity { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    public partial bool CanOpenOutputFolder { get; private set; }

    public AppSettings ToSettings() => new()
    {
        Format = Format,
        JpegQuality = JpegQuality,
        UseCustomFolder = UseCustomFolder,
        CustomFolder = CustomFolder,
        KeepMetadata = KeepMetadata,
        MaxParallel = MaxParallel,
    };

    // ---- Adding / removing files ------------------------------------------------------------------

    /// <summary>Adds HEIC files. Folders are searched recursively; other file types and duplicates are ignored.</summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var file in ExpandPaths(paths))
        {
            if (!_knownPaths.Add(file))
                continue;

            FileItemViewModel item;
            try
            {
                item = new FileItemViewModel(file);
            }
            catch (IOException)
            {
                _knownPaths.Remove(file);
                continue;
            }

            Files.Add(item);
            added++;

            // Files dropped in while a run is going join the queue straight away.
            if (IsRunning)
                _pending.Enqueue(item);
        }

        if (added > 0)
        {
            IsSummaryOpen = false;
            StartCommand.NotifyCanExecuteChanged();
        }
        return added;
    }

    public static bool ContainsSupportedPaths(IEnumerable<string> paths) =>
        paths.Any(p => Directory.Exists(p) || HeicImageConverter.IsSupported(p));

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(path, "*", options).Where(HeicImageConverter.IsSupported).Order())
                    yield return Path.GetFullPath(file);
            }
            else if (File.Exists(path) && HeicImageConverter.IsSupported(path))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select HEIC photos",
            Filter = "HEIC photos (*.heic;*.heif;*.hif)|*.heic;*.heif;*.hif|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true)
            AddPaths(dialog.FileNames);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Add all HEIC photos in a folder", Multiselect = true };
        if (dialog.ShowDialog() == true)
            AddPaths(dialog.FolderNames);
    }

    [RelayCommand]
    private void Remove(FileItemViewModel item)
    {
        // Failed items are never picked up by workers; queued ones must be claimed so a worker can't grab them mid-removal.
        if (item.Status == ConversionStatus.Failed || item.TryClaim())
        {
            Files.Remove(item);
            _knownPaths.Remove(item.SourcePath);
        }
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Files.Where(f => f.Status == ConversionStatus.Done).ToList())
        {
            Files.Remove(item);
            _knownPaths.Remove(item.SourcePath);
        }
        IsSummaryOpen = false;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ClearAll()
    {
        Files.Clear();
        _knownPaths.Clear();
        IsSummaryOpen = false;
    }

    [RelayCommand]
    private static void ShowInFolder(FileItemViewModel item)
    {
        if (item.OutputPath is { } path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private void OpenOutputFolder()
    {
        if (_lastOutputFolder is { } folder && Directory.Exists(folder))
            Process.Start("explorer.exe", $"\"{folder}\"");
    }

    [RelayCommand]
    private void ChooseOutputFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Save converted photos to" };
        if (!string.IsNullOrWhiteSpace(CustomFolder) && Directory.Exists(CustomFolder))
            dialog.InitialDirectory = CustomFolder;

        if (dialog.ShowDialog() == true)
        {
            CustomFolder = dialog.FolderName;
            UseCustomFolder = true;
        }
    }

    partial void OnUseCustomFolderChanged(bool value)
    {
        // Picking "custom folder" without a folder makes no sense: ask for one, or fall back.
        if (value && string.IsNullOrWhiteSpace(CustomFolder))
        {
            _dispatcher.InvokeAsync(() =>
            {
                ChooseOutputFolder();
                if (string.IsNullOrWhiteSpace(CustomFolder))
                    UseCustomFolder = false;
            });
        }
    }

    // ---- The conversion queue ---------------------------------------------------------------------

    private bool CanStart() => !IsRunning && Files.Any(f => f.Status is ConversionStatus.Queued or ConversionStatus.Failed);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Start()
    {
        _runTask = RunQueueAsync();
        return _runTask;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    /// <summary>Stops the run and waits until in-flight conversions have wound down (used when closing the window).</summary>
    public Task StopAndWaitAsync()
    {
        _cts?.Cancel();
        return _runTask;
    }

    private async Task RunQueueAsync()
    {
        foreach (var failed in Files.Where(f => f.Status == ConversionStatus.Failed))
            failed.Requeue();

        var options = new ConversionOptions(Format, JpegQuality, KeepMetadata);
        var outputFolder = UseCustomFolder ? CustomFolder : null;
        var workerCount = MaxParallel;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        _processedThisRun = 0;
        IsSummaryOpen = false;
        IsRunning = true;
        _runStopwatch.Restart();
        _clock.Start();

        try
        {
            // A fixed pool of workers pulls from the queue: never more than MaxParallel conversions at once,
            // and the moment one finishes the next file starts. Loop again if files were added as the last workers exited.
            do
            {
                foreach (var item in Files.Where(f => f.Status == ConversionStatus.Queued))
                    _pending.Enqueue(item);

                var workers = Enumerable.Range(0, workerCount)
                    .Select(_ => Task.Run(() => WorkerAsync(options, outputFolder, cts.Token)));
                await Task.WhenAll(workers);
            }
            while (!cts.IsCancellationRequested && Files.Any(f => f.Status == ConversionStatus.Queued));
        }
        finally
        {
            _pending.Clear();
            _runStopwatch.Stop();
            _clock.Stop();
            _cts = null;
            IsRunning = false;
            UpdateSummary();
            ShowRunSummary(cts.IsCancellationRequested);
        }
    }

    private async Task WorkerAsync(ConversionOptions options, string? outputFolder, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _pending.TryDequeue(out var item))
        {
            if (!item.TryClaim())
                continue; // Already taken, or removed from the list.

            await OnUi(item.MarkConverting);
            var stopwatch = Stopwatch.StartNew();
            string? destination = null;
            try
            {
                destination = _paths.Reserve(item.SourcePath, outputFolder, options.Extension);
                _converter.Convert(item.SourcePath, destination, options, ct);
                var size = new FileInfo(destination).Length;
                await OnUi(() =>
                {
                    item.MarkDone(destination, size, stopwatch.Elapsed);
                    _lastOutputFolder = Path.GetDirectoryName(destination);
                    _processedThisRun++;
                });
            }
            catch (OperationCanceledException)
            {
                ReleaseReservation(destination);
                await OnUi(item.Requeue);
            }
            catch (Exception ex)
            {
                ReleaseReservation(destination);
                await OnUi(() =>
                {
                    item.MarkFailed(FriendlyError(ex));
                    _processedThisRun++;
                });
            }
        }
    }

    private void ReleaseReservation(string? destination)
    {
        if (destination is not null)
            _paths.Release(destination);
    }

    private async Task OnUi(Action action)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            action();
            UpdateSummary();
        });
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied. Check that you can write to the output folder.",
        FileNotFoundException => "The file no longer exists.",
        ImageMagick.MagickException => $"Could not read this image. It may be damaged or not a real HEIC file.\n\n{ex.Message}",
        _ => ex.Message,
    };

    private void UpdateSummary()
    {
        int queued = 0, converting = 0, done = 0, failed = 0;
        long totalBytes = 0;
        foreach (var f in Files)
        {
            totalBytes += f.SourceSize;
            switch (f.Status)
            {
                case ConversionStatus.Queued: queued++; break;
                case ConversionStatus.Converting: converting++; break;
                case ConversionStatus.Done: done++; break;
                case ConversionStatus.Failed: failed++; break;
            }
        }

        var total = Files.Count;
        HasFiles = total > 0;
        PendingCount = queued + failed;
        DoneCount = done;
        FailedCount = failed;
        Progress = total == 0 ? 0 : 100.0 * (done + failed) / total;
        HeaderText = total == 0 ? "No photos added yet" : $"{Plural(total, "photo")}  ·  {Humanize.Bytes(totalBytes)}";
        StartButtonText = PendingCount > 0 ? $"Convert {Plural(PendingCount, "photo")}" : "Convert";
        CanOpenOutputFolder = !IsRunning && _lastOutputFolder is not null;

        StatusText = total == 0
            ? "Add photos to get started"
            : failed > 0 ? $"{done} of {total} converted  ·  {failed} failed" : $"{done} of {total} converted";

        if (IsRunning)
        {
            var elapsed = _runStopwatch.Elapsed;
            var remaining = queued + converting;
            TimeText = _processedThisRun > 0 && remaining > 0
                ? $"{Humanize.Duration(elapsed)}  ·  about {Humanize.Duration(elapsed / _processedThisRun * remaining)} left"
                : Humanize.Duration(elapsed);
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    private void ShowRunSummary(bool cancelled)
    {
        var elapsed = Humanize.Duration(_runStopwatch.Elapsed);
        var failedThisRun = Files.Count(f => f.Status == ConversionStatus.Failed);
        var doneThisRun = _processedThisRun - failedThisRun;

        if (cancelled)
        {
            SummarySeverity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            SummaryTitle = "Stopped";
            SummaryMessage = $"Converted {Plural(doneThisRun, "photo")} before stopping. Press Convert to continue with the rest.";
        }
        else if (failedThisRun > 0)
        {
            SummarySeverity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            SummaryTitle = "Finished with errors";
            SummaryMessage = $"Converted {Plural(doneThisRun, "photo")} in {elapsed}. {Plural(failedThisRun, "photo")} could not be converted. Hover over them to see why.";
        }
        else
        {
            SummarySeverity = Wpf.Ui.Controls.InfoBarSeverity.Success;
            SummaryTitle = "All done";
            SummaryMessage = $"Converted {Plural(doneThisRun, "photo")} in {elapsed}.";
        }

        TimeText = elapsed;
        IsSummaryOpen = true;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
