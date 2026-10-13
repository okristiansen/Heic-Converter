using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeicConverter.Services;

public sealed class AppSettings
{
    public OutputFormat Format { get; set; } = OutputFormat.Jpeg;
    public int JpegQuality { get; set; } = 90;
    public bool UseCustomFolder { get; set; }
    public string? CustomFolder { get; set; }
    public bool KeepMetadata { get; set; } = true;
    public int MaxParallel { get; set; } = DefaultParallel;

    // Throughput levels off around 8 workers in benchmarks; leave a couple of cores free so the PC stays responsive.
    public static int DefaultParallel => Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
}

/// <summary>Persists settings as JSON in %AppData%\HeicConverter.</summary>
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HeicConverter", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings shouldn't stop the app from starting.
        }
        return new();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
