# Pear Music Player — Ambient Now Playing

A fullscreen now-playing display for Windows 11, styled after Apple Music's
fullscreen player: album art on a slow-drifting colour gradient derived from
the art itself, with track title, artist, album and a progress bar.

It watches Windows System Media Transport Controls (SMTC) for a
**ShairportQt** AirPlay receiver session and displays whatever is streaming
to it, resolving high-resolution artwork from the iTunes Search API since
ShairportQt only publishes a 125×125 thumbnail and no album name.

Full design rationale, the exact matching/scoring algorithm, caching rules
and acceptance checks live in [`AMBIENT_PLAYER_SPEC.md`](AMBIENT_PLAYER_SPEC.md) -
read that first if you're changing behaviour, not just this file.

This is a personal, single-user tool. It prioritises "looks right and
doesn't break during an album" over configurability or packaging.

## Requirements

- Windows 10 (1809+) or Windows 11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) with the
  Windows desktop workload, built/run **on Windows** - the project targets
  `net8.0-windows10.0.19041.0` for WPF and the SMTC (`Windows.Media.Control`)
  WinRT projections, neither of which exist outside Windows.
- ShairportQt (or any AirPlay receiver) running and registered with SMTC,
  streaming from an iPhone/Apple Music.

## Build & run

```powershell
dotnet build AmbientPlayer.sln -c Debug
dotnet run --project src/AmbientPlayer/AmbientPlayer.csproj
```

## Publish a single-file executable

```powershell
dotnet publish src/AmbientPlayer/AmbientPlayer.csproj -c Release -r win-x64 --self-contained true
```

The executable lands under
`src/AmbientPlayer/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.
It's self-contained (no separate .NET runtime install needed on the target
machine).

## Project layout

```
src/AmbientPlayer/
  App.xaml(.cs)              Startup, global exception safety net
  MainWindow.xaml(.cs)       Renders everything; never touches SMTC or iTunes directly
  Services/
    MetadataService.cs       SMTC polling, field cleaning, settle debounce, transport controls
    ArtworkService.cs        iTunes Search API lookup, scoring, caching, rate limiting
    PaletteService.cs        Median-cut colour extraction in Lab space
    AppSettings.cs           The one thing persisted across runs: last monitor
  Rendering/
    MeshGradientBackground.cs  Animated mesh-gradient background control
    ProgressInterpolator.cs    Smooths sparse SMTC timeline updates
  Native/
    DisplayRequest.cs        SetThreadExecutionState wrapper (keeps the screen awake)
    MonitorInterop.cs        Win32 monitor enumeration ("remember last monitor")
    CursorAutoHide.cs        Hides the cursor after 3s idle
  Models/                    TrackInfo, PlaybackSnapshot, ArtworkResult
  Utilities/                 Text normalisation/scoring, Lab colour math, metadata cleaning
```

`smtc_dump.py` and `artwork_watch.py` at the repo root are the original
prototyping scripts this app's `MetadataService` and `ArtworkService` were
ported from - kept as reference, not part of the build.

## Configuration

There is deliberately no settings UI (out of scope for v1 - see spec section
10). The one thing that's configurable is the SMTC sender filter, which
defaults to `"ShairportQt"`; if you ever need to point this at a different
AirPlay receiver, pass a different substring into `new MetadataService(...)`
in `MainWindow`'s constructor.

## Controls

| Key | Action |
| --- | --- |
| `Esc` | Exit |
| `Space` | Play / pause (controls the sending iPhone via SMTC) |
| `←` / `→` | Previous / next track |
| `F` | Toggle fullscreen |

Transport buttons and keyboard media controls act on the **sending device**,
not local playback, and are disabled whenever no session is being tracked.
