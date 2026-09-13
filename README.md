# Pear Music Player — Ambient Now Playing

Fullscreen now-playing display for Windows, styled after Apple Music's
fullscreen player. Watches SMTC for a ShairportQt AirPlay session, pulls
high-res art from the iTunes Search API (ShairportQt only exposes a 125×125
thumbnail and no album name), and renders it on an animated Perlin-noise
background derived from the art's colour palette.

Design details, matching/scoring algorithm, and acceptance checks:
[`AMBIENT_PLAYER_SPEC.md`](AMBIENT_PLAYER_SPEC.md).

## Requirements

- Windows 10 (1809+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Windows
  desktop workload — must build/run on Windows (WPF + SMTC WinRT APIs)
- ShairportQt (or another AirPlay receiver) registered with SMTC and playing

## Build & run

```powershell
dotnet build AmbientPlayer.sln -c Debug
dotnet run --project src/AmbientPlayer/AmbientPlayer.csproj
```

## Publish

```powershell
dotnet publish src/AmbientPlayer/AmbientPlayer.csproj -c Release -r win-x64 --self-contained true
```

Self-contained single-file exe, output under
`src/AmbientPlayer/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.

## Layout

```
src/AmbientPlayer/
  App.xaml(.cs), MainWindow.xaml(.cs)
  Services/    MetadataService, ArtworkService, PaletteService, AppSettings
  Rendering/   MeshGradientBackground, PerlinNoise, ProgressInterpolator
  Native/      DisplayRequest, MonitorInterop, CursorAutoHide
  Models/      TrackInfo, PlaybackSnapshot, ArtworkResult
  Utilities/   text normalisation/scoring, Lab colour math, metadata cleaning
```

`smtc_dump.py` and `artwork_watch.py` are the original Python prototypes
`MetadataService`/`ArtworkService` were ported from.

## Configuration

No settings UI (see spec §10). To point at a different AirPlay receiver than
ShairportQt, change the filter string passed to `new MetadataService(...)`
in `MainWindow`'s constructor.

## Controls

| Key | Action |
| --- | --- |
| `Esc` | Exit |
| `Space` | Play / pause |
| `←` / `→` | Previous / next track |
| `F` | Toggle fullscreen |

Transport controls act on the sending device via SMTC, not local playback,
and disable when no session is tracked.
