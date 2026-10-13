using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HeicConverter.ViewModels;

public enum ConversionStatus
{
    Queued,
    Converting,
    Done,
    Failed,
}

public sealed partial class FileItemViewModel : ObservableObject
{
    // 0 = free, 1 = claimed by a worker (or being removed). Guarantees each file is converted exactly once.
    private int _claimed;

    public FileItemViewModel(string sourcePath)
    {
        SourcePath = sourcePath;
        FileName = Path.GetFileName(sourcePath);
        FolderPath = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        SourceSize = new FileInfo(sourcePath).Length;
    }

    public string SourcePath { get; }
    public string FileName { get; }
    public string FolderPath { get; }
    public long SourceSize { get; }
    public string Details => $"{Humanize.Bytes(SourceSize)}  ·  {FolderPath}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanRemove))]
    public partial ConversionStatus Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? OutputPath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; private set; }

    public long OutputSize { get; private set; }
    public TimeSpan Duration { get; private set; }

    public bool CanRemove => Status is ConversionStatus.Queued or ConversionStatus.Failed;

    public string StatusText => Status switch
    {
        ConversionStatus.Queued => "Queued",
        ConversionStatus.Converting => "Converting…",
        ConversionStatus.Done => $"{Humanize.Bytes(OutputSize)}  ·  {Humanize.Duration(Duration)}",
        ConversionStatus.Failed => "Failed",
        _ => string.Empty,
    };

    /// <summary>Atomically claims the file. Returns false if another worker (or a removal) got it first.</summary>
    public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    public void MarkConverting() => Status = ConversionStatus.Converting;

    public void MarkDone(string outputPath, long outputSize, TimeSpan duration)
    {
        OutputSize = outputSize;
        Duration = duration;
        OutputPath = outputPath;
        Status = ConversionStatus.Done;
    }

    public void MarkFailed(string error)
    {
        Error = error;
        Status = ConversionStatus.Failed;
    }

    /// <summary>Puts the file back in the queue (after a stop, or to retry a failure).</summary>
    public void Requeue()
    {
        Error = null;
        Status = ConversionStatus.Queued;
        Interlocked.Exchange(ref _claimed, 0);
    }
}
