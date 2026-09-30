<p align="center"><img src="Resources/aBookPlayer.png" width="160" alt="aBookPlayer"></p>

# aBookPlayer

[![Latest release](https://img.shields.io/github/v/release/MarcoTrombetta/aBookPlayer)](https://github.com/MarcoTrombetta/aBookPlayer/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/MarcoTrombetta/aBookPlayer/total)](https://github.com/MarcoTrombetta/aBookPlayer/releases)
[![License: MIT](https://img.shields.io/github/license/MarcoTrombetta/aBookPlayer)](LICENSE)

A dark-themed Windows audiobook player with chapters, synchronized subtitles and local speech-to-text transcription, and an Android app that plays the same library on your phone.

https://github.com/user-attachments/assets/fd6bcbe9-b2f9-492c-8e1b-bc5ff141b10a

## Features

- **Formats**: MP3, M4A, M4B, AAC, MP4, WMA, WAV, FLAC, AIFF, OGG
- **Books in several files**: a folder of chapter files (also split into "CD 1", "CD 2"… subfolders) plays as one book, with one chapter per file
- **Chapters**: read from ID3 (MP3), iTunes/QuickTime and Nero chapters (M4A/M4B), Vorbis comments (FLAC/OGG); chapter list, marks on the seek bar, previous/next chapter
- **Covers** from the file's tags or an image in its folder, shown next to the title, in the library and in Windows' media flyout
- **Library** in a panel beside the subtitles (Ctrl+L shows or hides it; drag the dividers to resize the library and chapter panels, the widths are remembered): every book with its cover, author and progress, the open one highlighted; filters and search; grouped by author or by series (click a group to fold it); scans your audiobook folders; right-click a book to mark it finished, forget it, or **edit its details** (title, author, series, cover) when the tags are wrong
- **Series**: when you finish a book, the next one of its series in your library is offered
- **Windows media controls**: title and cover in the volume/media flyout and on the lock screen; media keys and Bluetooth headset buttons work with the window in the background; play/pause and chapter buttons in the taskbar thumbnail
- **Bookmarks** with notes, shown on the seek bar; export them (with the words spoken) as Markdown
- **Smart rewind**: after a pause, playback resumes 5–30 s earlier depending on how long it was paused
- **Voice boost**: evens out the narrator's volume (quiet passages louder, loud ones softer)
- **Skip silences**: shortens the narrator's long pauses, with position and subtitles still exact
- **Sync between PCs** through a shared folder (OneDrive, Dropbox, a NAS): continue on another PC where you stopped
- **Your library on your phone** (File → Share with your phone): the Android app lists this PC's books, plays them streaming over the home network or copies them to the phone for listening away from home, and the place in each book follows you between the phone and the PC (see [Android app](#android-app))
- **Subtitles**: `.srt` files kept in sync with the audio (an `.srt` with the same name loads automatically), adjustable delay, customizable font, color, background and position; click them to play/pause, right-click (or Ctrl+C) to copy the line on screen
- **Two subtitles at once**: a second `.srt` (a translation) shown under the first, for listening in a language you are learning (File → Load second subtitles)
- **Transcription**: creates a synchronized `.srt` (and optionally a `.txt` with chapter headings) using [whisper.cpp](https://github.com/ggerganov/whisper.cpp) via [Whisper.net](https://github.com/sandrohanea/whisper.net). Runs entirely on the PC; the model is downloaded once, on demand. Uses the graphics card through Vulkan when available (NVIDIA, AMD, Intel; GPU support is also downloaded once, on demand). Several books can be queued. It can also **translate into English** (saved as `Book.en.srt` and shown under the book's own subtitles)
- **Search in the subtitles** (Ctrl+F) and jump to where a phrase is spoken
- **Sentence by sentence**: repeat the sentence just heard, go to the previous/next one, loop one (handy with a language you are learning)
- **Chapters from the transcription**: a book without chapters gets the headings the narrator reads ("Chapter 12", "Prologue"…)
- **Audible libraries**: books exported from Audible with Libation or OpenAudible are recognized by their names and tags (title, series, ASIN), play chapter by chapter, find the subtitles the export left in their folder, and sync between PCs by ASIN (aBookPlayer itself does not sign in to Audible or remove DRM: see [Audible libraries](#audible-libraries))
- **Playback speed** 0.5×–2× without pitch change; subtitles and chapters stay in sync
- **Every book remembers** its position, subtitles and sync; **Recent books** menu
- **Updates in one click**: once a day (Help menu, can be turned off) it looks for a new version, shows what is new, and downloads, installs and reopens itself
- **Sleep timer**: after 15–90 minutes or at the end of the chapter, with a fade-out
- **Skip intro and ending**: seconds skipped at the start and end of a book (credits, "This is Audible"), per book or for all (Playback menu)
- **Speed per book**: every book keeps its own playback speed
- **Mini player** (Ctrl+M) always on top, and an optional icon in the notification area
- **Listening statistics**: time per day and per book, when you will finish the current book at your pace, a daily goal and your streak of days reaching it
- **No standby while playing**, and optionally no screensaver or display off (Playback → Keep screen on while playing)
- Single window ("Open with" reuses it), drag & drop, keyboard shortcuts (F1)

## Audible libraries

aBookPlayer does **not** sign in to your Audible account and does **not** remove DRM. Audible has no public API for
third-party apps, and its `.aax`/`.aaxc` downloads are encrypted: opening them would mean circumventing a
protection measure, which the app deliberately does not do.

What it does instead is play the books you already exported to normal audio files with a tool that uses your own
account, such as Libation or OpenAudible. Export once,
then add the export folder to the library (Folders… at the top of the library). aBookPlayer understands what these tools
write:

| In the export | What aBookPlayer does |
|---|---|
| A folder per book, named `Title [ASIN]` | Shows the title without the long name and the ASIN code, and remembers the ASIN |
| One file per chapter (`… - 07 - Chapter 6.mp3`) | Plays them as one book, one chapter per file, named after the chapter |
| The **whole book** kept next to the chapter files (and the `.mp4` it came from) | Leaves it out: the chapter files are played, the book is not played a second time |
| Audible tags written by AAXClean (`AUDIBLE_ASIN`, `SERIES`, `PART`, `TIT3`, narrator) | Author, series ("Dungeon Crawler Carl, Book 1") in the library, and who reads the book under the title |
| The subtitles the exporter left in the folder | Loaded automatically (the per-chapter ones are not, they cover only part of the book) |
| The ASIN | Used to recognize the same book on another PC when syncing positions, even if it sits in another folder |

The library can list the books `By author` or `By series`, so a series stays together and in reading order.
Transcription (Ctrl+R) works on exported files like on any other book — they are ordinary `.mp3`/`.m4b` files.

## Android app

aBookPlayer for Android (Android 6.0 and later) plays your books on the phone with the same position, bookmarks,
chapters and subtitles as on the PC. It does not transcribe: the subtitles come from the Windows app.

**Install**: the easiest way is from the PC. Open File → Share with your phone and scan the code with the phone's
camera: on a phone without the app, the page it opens downloads the app from the PC (which fetches the version
matching its own from GitHub). Or download `aBookPlayer-<version>.apk` from the [latest release](https://github.com/MarcoTrombetta/aBookPlayer/releases/latest)
on the phone. Open the downloaded file to install it: Android asks once to allow installing apps from the browser or
the file manager, and the browser may warn about a download from a plain HTTP address (the PC's), which is expected.

**Updates**: when a connected PC runs a newer version, the phone's library offers to update the app from that PC in
one tap (Android asks once to allow aBookPlayer to install apps).

**Play the PC's books**: on the PC, turn on File → Share with your phone. On the phone, either scan the code shown
there with the camera, or open Library → ⋮ → Connect to a PC, which finds the PCs on the network, and type the access
key. The PC's books appear in the library ("on MYPC") and play streaming while aBookPlayer is open on the PC and the
phone is on the same network; Download to this phone (in the player's ⋮ menu) copies one to listen away from home.
The phone tells the PC where it stopped, and the PC tells the phone.

**Or a folder on the phone**: books copied to the phone (for example a folder kept in sync with the PC by Syncthing or
FolderSync) can be added with Add folder; with ⋮ → Sync pointing at the same shared folder as the PCs, positions follow
through it too.

The phone and the PC talk plain HTTP on the home network, each request carrying the access key; a new key (on the PC)
disconnects the phones that had the old one. On Android 17 a browser asks for access to devices nearby before it can
open the page of the PC's code.

**Windows Firewall**: phones connect to the PC on the sharing port (TCP, 52780 by default) and find it by a search on
UDP port 52780. The installer, when installing for all users, adds a rule letting aBookPlayer accept connections from
local network addresses only (not from the internet), unless the app already has one; uninstalling removes it. The
Share with your phone window tells whether the firewall lets phones connect and find the PC, and offers
**Allow through Windows Firewall** (it asks for administrator approval) when it may not, for example with the
portable version or an installation just for the current user.

Long MP3 files (a whole book in one file, often with a variable bitrate) are served to the phone in parts of about
three minutes, cut in pauses between frames: the phone then lands exactly where the PC is. The phone plays its own
long MP3 files (in a folder, or copied before version 1.11.1) the same way.

## Requirements

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build; the Android app also needs the MAUI workload (`dotnet workload install maui-android`)

## Project layout

| Folder | What it is |
|---|---|
| `Core/` (`aBookPlayer.Core`) | Shared library, no UI and no audio decoding: tags, chapters and covers (ID3, MP4/M4B, FLAC, OGG), subtitles, Audible exports, the library's logic (scan, sorting, groups, series), per-book state, listening statistics, the sync format, and the small HTTP server and client through which a phone plays the PC's library. Used by the Android app too. |
| root (`aBookPlayer`) | The Windows app (WinForms): UI, playback with NAudio, Whisper transcription, installer and updates. |
| `Android/` (`aBookPlayer.Android`) | The Android app (.NET MAUI, Android 6.0 and later): library, background playback with Media3 (notification with the position in the whole book, lock screen, headset buttons), chapters and subtitles, bookmarks, sleep timer, skipping silences, intro and ending, the next book of a series, and positions synced with the PCs through a folder kept in sync with them. It also plays the books of a PC streaming, over the home network (on the PC: File → Share with your phone), and copies them to the phone to listen away from home. No transcription: the subtitles come from the Windows app. |
| `tests/` | xUnit tests for both. |

## Build and run

```bash
dotnet run --project aBookPlayer.csproj
```

## Tests

```bash
dotnet test tests/aBookPlayer.Tests
```

They cover subtitles, metadata/chapter/cover readers (MP3, M4B, FLAC), time stretching and sync, channel downmixing, WAV decoding, books made of several files, Audible exports (Libation/OpenAudible names, tags and subtitles), the per-book history and bookmarks, smart rewind, sync between PCs, voice boost and the library scan. GitHub Actions builds and runs them on every push to `main` (`.github/workflows/ci.yml`).

## Installers

The installers are built with [Inno Setup](https://jrsoftware.org/isinfo.php) (6.3 or later):

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

This creates in `installer\out`:

| File | Contents |
|---|---|
| `aBookPlayer-<version>-x64-setup.exe` | Self-contained, includes the .NET runtime |
| `aBookPlayer-<version>-x64-light-setup.exe` | Smaller, requires the .NET 10 Desktop Runtime |
| `aBookPlayer-<version>-x64-portable.zip` | Self-contained, no installation: unzip and run (for example from a USB stick) |
| `aBookPlayer-<version>.apk` | The Android app, signed with the app's key (built and attached apart from the workflow) |

The version comes from `<Version>` in `aBookPlayer.csproj`.

Publishing a GitHub release (tag `v<version>`) runs `.github/workflows/release.yml`, which tests, builds these three files
and attaches them to the release.

## Data locations

- Settings: `%APPDATA%\aBookPlayer\settings.json`
- Whisper models: `%LOCALAPPDATA%\aBookPlayer\models`
- GPU support (Vulkan build of whisper.cpp): `%LOCALAPPDATA%\aBookPlayer\gpu`
- Library cover thumbnails (and covers chosen with Edit details): `%LOCALAPPDATA%\aBookPlayer\covers`
- Synced positions: `<shared folder>\aBookPlayer sync\<PC name>.json` (one file per PC)

The portable version (a `portable.txt` next to `aBookPlayer.exe`) keeps all of this in a `Data` folder next to it instead.

## Third-party components

NAudio, NAudio.Vorbis, NVorbis, Whisper.net, whisper.cpp, QRCoder and .NET MAUI, under the MIT License; AndroidX Media3 (the Android player), under the Apache License 2.0.

## License

aBookPlayer is released under the [MIT License](LICENSE).
