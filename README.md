<p align="center"><img src="Resources/aBookPlayer.png" width="160" alt="aBookPlayer"></p>

# aBookPlayer

A dark-themed Windows audiobook player with chapters, synchronized subtitles and local speech-to-text transcription.

## Features

- **Formats**: MP3, M4A, M4B, AAC, MP4, WMA, WAV, FLAC, AIFF, OGG
- **Chapters**: read from ID3 (MP3), iTunes/QuickTime and Nero chapters (M4A/M4B), Vorbis comments (FLAC/OGG); chapter list, marks on the seek bar, previous/next chapter
- **Subtitles**: `.srt` files kept in sync with the audio (an `.srt` with the same name loads automatically), adjustable delay, customizable font, color, background and position
- **Transcription**: creates a synchronized `.srt` (and optionally a `.txt` with chapter headings) using [whisper.cpp](https://github.com/ggerganov/whisper.cpp) via [Whisper.net](https://github.com/sandrohanea/whisper.net). Runs entirely on the PC; the model is downloaded once, on demand
- Resumes the last file where you left off, drag & drop, keyboard shortcuts (F1)

## Requirements

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build

## Build and run

```bash
dotnet run
```

## Installers

The installers are built with [Inno Setup](https://jrsoftware.org/isinfo.php) (6.3 or later):

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

This creates two setups in `installer\out`:

| Setup | Contents |
|---|---|
| `aBookPlayer-<version>-x64-setup.exe` | Self-contained, includes the .NET runtime |
| `aBookPlayer-<version>-x64-light-setup.exe` | Smaller, requires the .NET 10 Desktop Runtime |

The version comes from `<Version>` in `aBookPlayer.csproj`.

## Data locations

- Settings: `%APPDATA%\aBookPlayer\settings.json`
- Whisper models: `%LOCALAPPDATA%\aBookPlayer\models`

## Third-party components

NAudio, NAudio.Vorbis, NVorbis, Whisper.net and whisper.cpp, all under the MIT License.
