# Ambient Now Playing — build spec

A fullscreen now-playing display for Windows 11, in the style of the Apple Music
fullscreen player: album art on a slow-drifting colour gradient derived from the
art itself, with track title, artist, album and a progress bar.

Personal tool, single user, single machine. Prioritise "looks right and doesn't
break during an album" over configurability, extensibility or packaging.

---

## 1. Context — read this before designing anything

The metadata source is **ShairportQt**, an AirPlay receiver running on the same
PC. Music is streamed from an iPhone (Apple Music) to ShairportQt, which plays
it out of the PC and registers with Windows **SMTC** (System Media Transport
Controls). This app does **not** fork or modify ShairportQt. It reads SMTC like
any other observer.

Three findings from a prototyping session drive the whole design. They are
already validated against real listening data — do not re-litigate them:

1. **SMTC artwork from ShairportQt is 125×126 px.** Useless at display size.
   (For contrast, Apple Music running natively on the PC publishes 800×800 — so
   the limitation is ShairportQt's, not SMTC's.)
2. **High-res artwork must come from the iTunes Search API**, matched on artist
   plus track. This was tested across a varied listening session and matched
   essentially every track that exists in the Apple catalogue.
3. **ShairportQt publishes no album name at all**, and emits half-populated
   metadata for ~1s on every track change, using whitespace-filled fields rather
   than absent ones. Both must be handled or you get double lookups and junk.

Two reference Python scripts accompany this spec and contain **working,
tested logic** for the matching and the SMTC access. Port from them rather than
reinventing:

- `smtc_dump.py` — minimal SMTC session read, metadata and thumbnail extraction.
- `artwork_watch.py` — the full lookup pipeline: normalisation, scoring,
  fallbacks, caching, rate limiting. **This is the reference implementation for
  section 4.**

---

## 2. Stack

- **C# / .NET 8 / WPF**, single self-contained executable.
- SMTC via `Windows.Media.Control` (CsWinRT — add
  `<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>` so the WinRT
  projections are available).
- No external UI framework. WPF's `RadialGradientBrush` and `DoubleAnimation`
  are enough for the background; a `ShaderEffect` is optional polish, not v1.
- JSON via `System.Text.Json`.

Rationale for WPF over a web stack: no runtime to ship, native SMTC bindings,
and rounded corners plus soft shadows come free.

---

## 3. Architecture

Four pieces, loosely coupled, each independently testable:

```
MetadataService  -> raises TrackChanged(artist, album, title)
                    and exposes Position / Duration / PlaybackStatus
ArtworkService   -> given (artist, album, title) returns a local image path
PaletteService   -> given an image returns 5-6 display colours
MainWindow       -> renders everything
```

`MainWindow` should never call the iTunes API or touch SMTC directly.

---

## 4. MetadataService

### Session selection

Enumerate `GetSessions()`, not just `GetCurrentSession()`. Filter to the session
whose `SourceAppUserModelId` contains a configurable substring, defaulting to
`ShairportQt`. Fall back to the current session if no match.

Expose the app id so it can be logged — the user needs to see which session is
being tracked when debugging.

### Field cleaning — REQUIRED

Every metadata string must pass through a cleaner that:

- returns empty for null,
- trims whitespace,
- strips zero-width characters (`\u200b \u200c \u200d \ufeff`),
- treats the result as **absent** if empty.

A whitespace-only string is truthy in most languages and will otherwise fire a
lookup against junk. This was the single biggest source of wasted API calls in
the prototype.

Artist resolution order: `AlbumArtist` → `Artist`.

### Settle debounce — REQUIRED

On detecting a change, **wait 1.5 s, re-read, and only emit `TrackChanged` if
the values are identical**. If they differ, discard and let the next poll pick
up the settled state.

Without this you get two lookups per track — one against half-filled fields.

### Polling

2 s interval is fine; SMTC change events exist but fire inconsistently for this
sender, so poll. Position comes from `GetTimelineProperties()`, playback state
from `GetPlaybackInfo()`.

---

## 5. ArtworkService

### Resolution chain

1. **iTunes Search API** lookup (below).
2. **SMTC thumbnail** at 125 px — ugly but honest; blur it heavily or render it
   small rather than upscaling sharp.
3. **No art** — background gradient only, from a neutral default palette.

### Lookup

Endpoint, no key or auth required:

```
https://itunes.apple.com/search?term={url-encoded}&entity={album|song}&limit=8&country=GB
```

Attempt order:

- album search, if an album name exists (it won't, with ShairportQt, but keep it
  for other senders),
- **song search on `"{artist} {title}"` — this is the path that actually runs.**

### Scoring — port this exactly

Getting this wrong produces confidently wrong album art, which is worse than no
art because it also poisons the background palette.

- Normalise both sides: lowercase, strip accents (NFKD + drop combining marks),
  `&` → `and`, remove edition/remaster/deluxe/anniversary/feat. suffixes in
  brackets or after a dash, strip remaining punctuation, collapse whitespace.
- Similarity: exact match 1.0; one string containing the other 0.92; otherwise
  a Levenshtein-style ratio.
- **Compare like with like.** On an album search, compare album to
  `collectionName`. On a song search, compare title to `trackName`. Comparing a
  track title against a collection name was a real bug in the prototype — it
  rejected correct matches at 0.62.
- Weighted score: `artist × 0.55 + work × 0.45`.
- **Threshold 0.72.** Below it, return no match rather than guessing.
- Rewrite the returned `artworkUrl100` with a regex on `/\d+x\d+bb\.(jpg|png)/`
  → `/1000x1000bb.jpg`.

Capture `collectionName` from the winning result and expose it as the **resolved
album name** — ShairportQt never sends one, and this is the only way to render
the "Artist — Album" line.

### Caching and rate limiting

- Minimum 3 s between API calls.
- Persist a JSON cache keyed `artist|||album` or `artist|||track:title` when no
  album. Store URL, local file path, confidence, resolved album. Cache negative
  results too, so an unmatched track isn't retried every play.
- Add a `version` field to the cache and discard the file wholesale on mismatch,
  so changes to the matching logic don't leave stale verdicts behind.
- Store images under `%LOCALAPPDATA%\AmbientPlayer\artwork\`.

### Known-acceptable imperfection

When a track appears on several releases, the API may return a reissue or
remaster cover rather than the original. Observed in testing with Snarky Puppy,
Bobby Brown and Bowie. Without an album name there is nothing to disambiguate
with. **Do not add heuristics to fight this** — picking "earliest release" is
wrong as often as it's right.

---

## 6. PaletteService

Extract 5–6 colours for the background gradient.

- Downscale to 32×32 first. The 125 px fallback thumbnail is plenty for this,
  so palette quality never depends on the lookup succeeding.
- Median-cut or k-means in Lab space.
- Weight toward saturated colours **but degrade gracefully**: monochrome covers
  are common in this user's library (the reference screenshot is a black-and-
  white cover). When saturation is uniformly low, fall back to spreading by
  luminance instead, or the gradient collapses to a flat grey wash.
- Reject clusters within a small Lab distance of one another for the same
  reason.
- Darken and desaturate the final palette for background use — full-strength
  albumSave colours are far too loud behind white text.

---

## 7. Visual specification

Reference: Apple Music's fullscreen player. Match the proportions rather than
pixel-copying.

### Layout

Vertically stacked, centred, sitting slightly above true centre:

- **Album art** — square, roughly 45% of window height, corner radius ~1.5% of
  its width, large soft drop shadow (heavily blurred, low opacity, small
  downward offset).
- **Title** — bold, ~2.2% of window height.
- **Subtitle** — `Artist — Album`, regular weight, ~70% opacity, ~1.6% of height.
  Use the resolved album from the lookup. If unresolved, show artist alone with
  no dash.
- **Progress row** — elapsed left, remaining (negative, e.g. `-2:04`) right, thin
  rounded track between them at ~30% opacity with a solid fill.
- **Transport row** — previous / play-pause / next, centred.

Typography: SF Pro is not on Windows. Use **Segoe UI Variable Display**, or
bundle Inter for a closer match to the reference.

### Background

A **mesh gradient**, not noise: 5–6 colour blobs positioned in normalised space,
each drifting on its own slow sine cycle. Weight contributions by inverse
squared distance and normalise.

- **Cycle periods 20–60 s.** Anything faster reads as a screensaver.
- Blob drift radius ~0.15 in normalised units.
- Add a subtle vignette.
- **Dithering:** apply a per-pixel hash offset of roughly ±1.5/255 as the final
  step. Large smooth gradients band badly at 8-bit, and it is very visible on
  near-monochrome palettes. This is what reads as fine grain in the reference.

WPF implementation without shaders: layer several `RadialGradientBrush`
rectangles with animated `Center` points, over a base fill. This is genuinely
sufficient. A `ShaderEffect` gets cleaner blending and easier dithering — treat
it as a later refinement.

### Transitions

- Artwork crossfade 400 ms on track change.
- Palette transition ~2 s, interpolating in Lab, so the background drifts to the
  new colours rather than cutting.
- Never reset blob positions on track change — continuity is the whole effect.

---

## 8. Progress bar behaviour

SMTC timeline updates are **sparse and irregular** from this sender. Naive
binding produces a bar that jumps.

- Interpolate locally with a render-loop timer from the last known position.
- Resync only when a new timeline value arrives and differs from the
  interpolated value by more than ~1.5 s.
- Freeze interpolation when `PlaybackStatus` is not `Playing`.
- If duration is zero or absent, hide the bar rather than rendering an empty one.

---

## 9. Controls and window behaviour

Transport buttons call SMTC (`TryPlayPauseAsync`, `TrySkipNextAsync`,
`TrySkipPreviousAsync`). Note these control the **sending iPhone**, not local
playback, and will be unavailable when nothing is connected — disable rather
than hide them.

Keyboard: `Esc` exit, `Space` play/pause, `←`/`→` previous/next, `F` toggle
fullscreen.

- `WindowStyle=None`, `WindowState=Maximized`, topmost off.
- Hide the cursor after 3 s idle, restore on movement.
- Call `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED)` so the
  screen doesn't blank mid-album. Clear it on exit.
- Remember which monitor it was last on.

---

## 10. Out of scope for v1

Do not build: settings UI, squircle corner geometry (plain rounded corners are
fine), lyrics, queue display, multi-monitor spanning, installer, telemetry,
any abstraction for "other metadata sources".

---

## 11. Acceptance checks

1. Start playback on the phone, launch the app — art, metadata and a moving
   progress bar appear within ~4 s.
2. Skip tracks rapidly five times — exactly one lookup per settled track, no
   lookups against blank metadata, no flicker of wrong art.
3. Play an album straight through — background drifts continuously, never
   resets, no visible banding.
4. Play something not in the Apple catalogue — falls back cleanly, no crash, no
   wrong cover, palette still derived from the small thumbnail.
5. Pause for two minutes — progress freezes, display stays up, screen does not
   blank.
6. Kill and restart — cached art loads without new API calls.
7. Disconnect AirPlay entirely — app stays alive and idles rather than throwing.

---

## 12. Gotchas

- SMTC sessions do not appear until audio is actually playing.
- `TryGetMediaPropertiesAsync` throws intermittently during session teardown —
  catch and continue rather than propagating.
- The thumbnail stream must be fully read before use; partial reads yield
  truncated JPEG data.
- iTunes returns HTTP 403 under aggressive polling — the 3 s gap plus caching
  keeps well clear, but handle it rather than crashing.
- Artist names in this library include heavy Unicode (stylised names, combining
  marks, non-Latin scripts). Normalisation must not throw on them, and filenames
  derived from them must be sanitised. Both are handled in `artwork_watch.py`.
