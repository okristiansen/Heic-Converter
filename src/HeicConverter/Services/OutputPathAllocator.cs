using System.IO;

namespace HeicConverter.Services;

/// <summary>
/// Hands out unique output paths. Parallel workers can otherwise pick the same name at the same time,
/// e.g. two IMG_0001.HEIC from different folders converted into one output folder.
/// </summary>
public sealed class OutputPathAllocator
{
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>Returns "name.jpg", or "name (1).jpg", "name (2).jpg"… if that already exists.</summary>
    public string Reserve(string sourcePath, string? outputFolder, string extension)
    {
        var folder = outputFolder ?? Path.GetDirectoryName(sourcePath)!;
        var name = Path.GetFileNameWithoutExtension(sourcePath);

        lock (_lock)
        {
            for (var n = 0; ; n++)
            {
                var candidate = Path.Combine(folder, n == 0 ? $"{name}{extension}" : $"{name} ({n}){extension}");
                if (!_reserved.Contains(candidate) && !File.Exists(candidate))
                {
                    _reserved.Add(candidate);
                    return candidate;
                }
            }
        }
    }

    public void Release(string path)
    {
        lock (_lock)
            _reserved.Remove(path);
    }
}
