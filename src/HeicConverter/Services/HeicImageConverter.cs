using System.IO;
using ImageMagick;

namespace HeicConverter.Services;

/// <summary>Converts a single HEIC/HEIF file to JPEG or PNG. Thread-safe: each call works on its own image.</summary>
public sealed class HeicImageConverter
{
    public static readonly string[] SupportedExtensions = [".heic", ".heif", ".hif"];

    // PNG "quality" in ImageMagick is two digits: zlib level (tens) and filter (ones).
    // 15 = zlib level 1 + adaptive filtering: ~45% faster than the default 75, only ~1% larger for photos.
    private const uint PngQuality = 15;

    static HeicImageConverter()
    {
        // We parallelise across files instead. ImageMagick's own threading made single files slower in benchmarks.
        ResourceLimits.Thread = 1;
    }

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Converts <paramref name="sourcePath"/> and writes it to <paramref name="destinationPath"/>.</summary>
    /// <remarks>Writes to a temporary file first, so a cancelled or failed conversion never leaves a half-written image.</remarks>
    public void Convert(string sourcePath, string destinationPath, ConversionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var image = new MagickImage(sourcePath);
        // Last chance to bail out. Once encoding starts we finish, so Stop never throws away nearly-done work.
        cancellationToken.ThrowIfCancellationRequested();

        // libheif already applies the HEIC rotation; this also covers files that only carry an EXIF orientation flag.
        image.AutoOrient();

        if (!options.KeepMetadata)
        {
            // Keep the ICC colour profile (iPhones shoot in Display P3), drop everything that identifies the photo.
            image.RemoveProfile("exif");
            image.RemoveProfile("xmp");
            image.RemoveProfile("iptc");
        }

        MagickFormat format;
        if (options.Format == OutputFormat.Png)
        {
            format = MagickFormat.Png;
            image.Quality = PngQuality;
        }
        else
        {
            format = MagickFormat.Jpeg;
            image.Quality = (uint)Math.Clamp(options.JpegQuality, 1, 100);
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".partial";
        try
        {
            image.Write(tempPath, format);
            File.Move(tempPath, destinationPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
