# HEIC Converter

A fast Windows desktop app for converting HEIC photos to JPEG or PNG. Drop in files or folders. Several photos are converted at once and the rest wait in a queue.

Built with .NET 10, WPF, [WPF-UI](https://github.com/lepoco/wpfui) and [Magick.NET](https://github.com/dlemstra/Magick.NET).

## Run

```bash
dotnet run --project src/HeicConverter
```

## Build a standalone .exe

```bash
dotnet publish src/HeicConverter -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

This creates `publish/HeicConverter.exe` (about 70 MB). It runs on 64-bit Windows 10/11 without .NET installed.
