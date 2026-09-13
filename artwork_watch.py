"""
artwork_watch.py - watch SMTC and resolve high-res artwork via the iTunes Search API.

Sits in a loop, notices track changes, looks up the album on the iTunes
catalogue, verifies the match is actually right, and saves 1000x1000 artwork.
Logs every lookup so you can judge the hit rate over a real listening session
before committing to building a player on top of this.

Setup:
    pip install winsdk pillow

Usage:
    python artwork_watch.py                  # watches the current session
    python artwork_watch.py --list           # show session app ids and exit
    python artwork_watch.py --app ShairportQt  # filter to one app id (substring)

Artwork lands in ./artwork/, the lookup cache in ./artwork/cache.json,
and a running log of hits and misses in ./artwork/lookups.log
"""

import argparse
import asyncio
import json
import re
import sys
import time
import unicodedata
import urllib.parse
import urllib.request
from difflib import SequenceMatcher
from pathlib import Path

OUT_DIR = Path("artwork")
CACHE_PATH = OUT_DIR / "cache.json"
LOG_PATH = OUT_DIR / "lookups.log"

STOREFRONT = "GB"          # iTunes storefront to search
ART_SIZE = 1000            # px, substituted into the artwork URL
POLL_SECONDS = 2           # how often to check for a track change
SETTLE_SECONDS = 1.5       # wait for metadata to stop changing before looking up
MIN_REQUEST_GAP = 3.0      # seconds between API calls, keeps well under the limit
MATCH_THRESHOLD = 0.72     # below this the match is rejected as wrong

_last_request = 0.0


# --------------------------------------------------------------------------
# text normalisation and matching
# --------------------------------------------------------------------------

# Suffixes publishers add that stop album titles matching cleanly.
NOISE_PATTERNS = [
    r"\(.*?(deluxe|remaster|remastered|expanded|anniversary|edition|version|bonus|explicit|mono|stereo).*?\)",
    r"\[.*?(deluxe|remaster|remastered|expanded|anniversary|edition|version|bonus|explicit|mono|stereo).*?\]",
    r"\s*-\s*(deluxe|remaster(ed)?|expanded|anniversary|single|ep)\b.*$",
    r"\(feat\..*?\)",
    r"\[feat\..*?\]",
    r"\bfeat\..*$",
    r"\bft\..*$",
]


def normalise(text):
    """Lowercase, strip accents, drop edition suffixes and punctuation."""
    if not text:
        return ""
    text = unicodedata.normalize("NFKD", text)
    text = "".join(c for c in text if not unicodedata.combining(c))
    text = text.lower()
    text = text.replace("&", "and")
    for pattern in NOISE_PATTERNS:
        text = re.sub(pattern, " ", text)
    text = re.sub(r"[^a-z0-9]+", " ", text)
    return " ".join(text.split())


def similarity(a, b):
    a, b = normalise(a), normalise(b)
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0
    # One containing the other is a strong signal (e.g. "paths" in "paths ep").
    if a in b or b in a:
        return 0.92
    return SequenceMatcher(None, a, b).ratio()


def score_candidate(want_artist, want_work, cand_artist, cand_work):
    """
    Weighted confidence that a search result is what we asked for.

    'work' is the album title on an album search and the track title on a
    song search - compare like with like, or a correct hit scores as a miss.
    """
    artist_score = similarity(want_artist, cand_artist)
    work_score = similarity(want_work, cand_work)
    # Artist matters slightly more - a wrong artist is always wrong, whereas
    # titles get mangled by editions, reissues and remixes.
    return artist_score * 0.55 + work_score * 0.45


# --------------------------------------------------------------------------
# iTunes Search API
# --------------------------------------------------------------------------

def api_get(url):
    """Fetch JSON, respecting a minimum gap between requests."""
    global _last_request
    wait = MIN_REQUEST_GAP - (time.monotonic() - _last_request)
    if wait > 0:
        time.sleep(wait)
    _last_request = time.monotonic()

    req = urllib.request.Request(url, headers={"User-Agent": "artwork-watch/1.0"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        return json.loads(resp.read().decode("utf-8"))


def upscale_art_url(url, size=ART_SIZE):
    """Rewrite the 100x100 artwork URL the API returns to a larger one."""
    if not url:
        return None
    return re.sub(r"/\d+x\d+bb\.(jpg|png)", f"/{size}x{size}bb.jpg", url)


def search(term, entity, limit=8):
    url = (
        "https://itunes.apple.com/search?"
        + urllib.parse.urlencode(
            {"term": term, "entity": entity, "limit": limit, "country": STOREFRONT}
        )
    )
    try:
        return api_get(url).get("results", [])
    except Exception as exc:
        print(f"    lookup failed: {exc}")
        return []


def find_artwork(artist, album, title):
    """
    Try an album search when we have an album name, otherwise fall back to a
    track search. Compares against the matching field for each mode.

    Returns (art_url, confidence, how, resolved_album).
    """
    attempts = []

    if artist and album:
        attempts.append(("album", f"{artist} {album}", album))
    if album and not artist:
        attempts.append(("album", album, album))
    # Track search is the fallback, and the only option when the sender
    # publishes no album name at all - which is what ShairportQt does.
    if artist and title:
        attempts.append(("song", f"{artist} {title}", title))
    if title and not artist:
        attempts.append(("song", title, title))

    best = (None, 0.0, "no candidates", "")

    for entity, term, want_work in attempts:
        results = search(term, entity)
        for r in results:
            cand_artist = r.get("artistName", "")
            cand_album = r.get("collectionName", "")
            # Album search compares album to album, song search track to track.
            cand_work = cand_album if entity == "album" else r.get("trackName", "")
            conf = score_candidate(artist, want_work, cand_artist, cand_work)
            if conf > best[1]:
                art = upscale_art_url(r.get("artworkUrl100"))
                best = (art, conf, f"{entity}: {cand_artist} - {cand_work}", cand_album)
        if best[1] >= MATCH_THRESHOLD:
            break

    if best[1] < MATCH_THRESHOLD:
        return None, best[1], f"best was {best[1]:.2f} ({best[2]})", ""
    return best


def download(url, path):
    req = urllib.request.Request(url, headers={"User-Agent": "artwork-watch/1.0"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        data = resp.read()
    path.write_bytes(data)
    return len(data)


# --------------------------------------------------------------------------
# cache and logging
# --------------------------------------------------------------------------

def load_cache():
    if CACHE_PATH.exists():
        try:
            return json.loads(CACHE_PATH.read_text(encoding="utf-8"))
        except Exception:
            return {}
    return {}


def save_cache(cache):
    OUT_DIR.mkdir(exist_ok=True)
    CACHE_PATH.write_text(json.dumps(cache, indent=2), encoding="utf-8")


def log(line):
    OUT_DIR.mkdir(exist_ok=True)
    stamp = time.strftime("%H:%M:%S")
    with LOG_PATH.open("a", encoding="utf-8") as f:
        f.write(f"{stamp}  {line}\n")


def safe_name(text):
    return re.sub(r"[^A-Za-z0-9._-]", "_", text or "")[:70] or "unknown"


def clean_field(value):
    """
    Treat whitespace-only metadata as absent.

    Senders often publish a present-but-empty field rather than omitting it,
    and a string of spaces is truthy in Python - which is enough to fire a
    lookup against junk and cache a phantom miss.
    """
    if not value:
        return ""
    # Strip normal whitespace plus the zero-width characters that occasionally
    # turn up in stylised track metadata.
    return value.strip().strip("\u200b\u200c\u200d\ufeff").strip()


# --------------------------------------------------------------------------
# SMTC polling
# --------------------------------------------------------------------------

async def get_session(mgr, app_filter):
    sessions = list(mgr.get_sessions())
    if not sessions:
        return None
    if app_filter:
        for s in sessions:
            if app_filter.lower() in (s.source_app_user_model_id or "").lower():
                return s
        return None
    return mgr.get_current_session()


async def read_track(session):
    """Return (artist, album, title) with whitespace-only fields normalised out."""
    props = await session.try_get_media_properties_async()
    artist = clean_field(props.album_artist) or clean_field(props.artist)
    album = clean_field(props.album_title)
    title = clean_field(props.title)
    return artist, album, title


async def watch(app_filter):
    from winsdk.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager as MediaManager,
    )

    mgr = await MediaManager.request_async()
    cache = load_cache()
    last_key = None
    hits = misses = 0

    print("Watching for track changes. Ctrl+C to stop.\n")

    while True:
        session = await get_session(mgr, app_filter)
        if session is None:
            await asyncio.sleep(POLL_SECONDS)
            continue

        try:
            artist, album, title = await read_track(session)
        except Exception:
            await asyncio.sleep(POLL_SECONDS)
            continue

        # With no album name there is nothing album-shaped to cache against,
        # so fall back to caching per track.
        key = f"{artist}|||{album}" if album else f"{artist}|||track:{title}"

        if not title or key == last_key:
            await asyncio.sleep(POLL_SECONDS)
            continue

        # Senders publish metadata field by field, so the first read after a
        # track change is often half filled. Wait, re-read, and only act once
        # the same values come back twice.
        await asyncio.sleep(SETTLE_SECONDS)
        try:
            artist2, album2, title2 = await read_track(session)
        except Exception:
            continue

        if (artist2, album2, title2) != (artist, album, title):
            # Still changing - let the next loop pick up the settled version.
            continue

        last_key = key
        print(f"\n{title}")
        print(f"  {artist or '(no artist)'} - {album or '(no album)'}")

        if key in cache:
            entry = cache[key]
            if entry.get("url"):
                print(f"  cached: {Path(entry['file']).name}")
            else:
                print(f"  cached miss: {entry.get('why', '')}")
            await asyncio.sleep(POLL_SECONDS)
            continue

        url, conf, how, resolved_album = find_artwork(artist, album, title)

        if url:
            OUT_DIR.mkdir(exist_ok=True)
            path = OUT_DIR / f"{safe_name(artist)}__{safe_name(album or title)}.jpg"
            try:
                size = download(url, path)
                hits += 1
                print(f"  matched {conf:.2f} via {how}")
                if not album and resolved_album:
                    print(f"  album resolved as: {resolved_album}")
                print(f"  saved {size:,} bytes -> {path.name}")
                cache[key] = {
                    "url": url,
                    "file": str(path),
                    "conf": conf,
                    "album": resolved_album,
                }
                log(f"HIT  {conf:.2f}  {artist} / {title}  [{how}]")
            except Exception as exc:
                misses += 1
                print(f"  download failed: {exc}")
                log(f"FAIL download  {artist} - {album}  {exc}")
        else:
            misses += 1
            print(f"  no confident match ({how})")
            cache[key] = {"url": None, "why": how}
            log(f"MISS  {artist} / {title}  [{how}]")

        save_cache(cache)
        total = hits + misses
        if total:
            print(f"  running: {hits}/{total} matched")

        await asyncio.sleep(POLL_SECONDS)


async def list_sessions():
    from winsdk.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager as MediaManager,
    )

    mgr = await MediaManager.request_async()
    sessions = list(mgr.get_sessions())
    if not sessions:
        print("No sessions. Start playback first.")
        return
    current = mgr.get_current_session()
    current_id = current.source_app_user_model_id if current else None
    for s in sessions:
        app_id = s.source_app_user_model_id or "unknown"
        mark = "  <- current" if app_id == current_id else ""
        print(f"{app_id}{mark}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--list", action="store_true", help="list session app ids and exit")
    parser.add_argument("--app", help="only watch sessions whose app id contains this")
    args = parser.parse_args()

    try:
        import winsdk  # noqa: F401
    except ImportError:
        sys.exit("winsdk not found. Run: pip install winsdk pillow")

    try:
        if args.list:
            asyncio.run(list_sessions())
        else:
            asyncio.run(watch(args.app))
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()
