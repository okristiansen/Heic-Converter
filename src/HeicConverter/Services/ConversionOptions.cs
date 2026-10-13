namespace HeicConverter.Services;

public enum OutputFormat
{
    Jpeg,
    Png,
}

/// <summary>Immutable snapshot of the settings used for one conversion run.</summary>
public sealed record ConversionOptions(OutputFormat Format, int JpegQuality, bool KeepMetadata)
{
    public string Extension => Format == OutputFormat.Png ? ".png" : ".jpg";
}
