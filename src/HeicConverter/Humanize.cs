namespace HeicConverter;

internal static class Humanize
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    public static string Duration(TimeSpan t) => t.TotalSeconds switch
    {
        < 10 => $"{t.TotalSeconds:0.0} s",
        < 60 => $"{t.TotalSeconds:0} s",
        _ => $"{(int)t.TotalMinutes}:{t.Seconds:00} min",
    };
}
