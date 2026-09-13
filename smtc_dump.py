"""
smtc_dump.py - inspect Windows System Media Transport Controls sessions.

Dumps now-playing metadata and saves the artwork thumbnail for every active
session, then reports the pixel dimensions so you can judge whether SMTC
artwork is big enough to render full screen.

Setup:
    pip install winsdk pillow

Usage:
    Start playback first (AirPlay to ShairportQt, Apple Music, browser, etc.)
    then run:

        python smtc_dump.py

Artwork is written to ./smtc_art/ as <index>_<app-id>.jpg
"""

import asyncio
import re
import sys
from pathlib import Path

try:
    from winsdk.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager as MediaManager,
    )
    from winsdk.windows.storage.streams import DataReader
except ImportError:
    sys.exit("winsdk not found. Run: pip install winsdk pillow")

try:
    from PIL import Image
except ImportError:
    Image = None

OUT_DIR = Path("smtc_art")


def safe_name(text):
    """Make an app id usable as a filename."""
    return re.sub(r"[^A-Za-z0-9._-]", "_", text)[:60] or "unknown"


def fmt_time(td):
    """Format a timedelta-ish duration as m:ss."""
    try:
        total = int(td.total_seconds())
    except AttributeError:
        return "?"
    return f"{total // 60}:{total % 60:02d}"


async def read_thumbnail(thumb):
    """Pull the raw bytes out of a SMTC thumbnail reference."""
    stream = await thumb.open_read_async()
    reader = DataReader(stream)
    await reader.load_async(stream.size)
    return bytes(reader.read_buffer(stream.size)), stream.content_type


async def dump_session(index, session):
    app_id = session.source_app_user_model_id or "unknown"
    print(f"\n[{index}] {app_id}")
    print("-" * (len(app_id) + 6))

    try:
        props = await session.try_get_media_properties_async()
    except Exception as exc:
        print(f"  could not read media properties: {exc}")
        return

    print(f"  title    : {props.title or '(none)'}")
    print(f"  artist   : {props.artist or '(none)'}")
    print(f"  album    : {props.album_title or '(none)'}")
    if props.album_artist:
        print(f"  alb.art. : {props.album_artist}")
    if props.track_number:
        print(f"  track    : {props.track_number}")

    # Playback + timeline, so you can see whether progress is usable.
    try:
        info = session.get_playback_info()
        print(f"  status   : {info.playback_status}")
    except Exception:
        pass

    try:
        tl = session.get_timeline_properties()
        print(f"  position : {fmt_time(tl.position)} / {fmt_time(tl.end_time)}")
    except Exception:
        pass

    if props.thumbnail is None:
        print("  artwork  : none on this session")
        return

    try:
        data, content_type = await read_thumbnail(props.thumbnail)
    except Exception as exc:
        print(f"  artwork  : failed to read ({exc})")
        return

    OUT_DIR.mkdir(exist_ok=True)
    path = OUT_DIR / f"{index}_{safe_name(app_id)}.jpg"
    path.write_bytes(data)

    print(f"  artwork  : {len(data):,} bytes, {content_type}")
    print(f"             saved to {path}")

    if Image is None:
        print("             (install pillow to see dimensions)")
        return

    try:
        with Image.open(path) as img:
            w, h = img.size
            print(f"             {w} x {h} px, mode {img.mode}")
            verdict(w, h)
    except Exception as exc:
        print(f"             could not open as image: {exc}")


def verdict(w, h):
    smallest = min(w, h)
    if smallest >= 600:
        note = "plenty for a full screen player"
    elif smallest >= 300:
        note = "workable on 1080p if you don't fill the frame"
    else:
        note = "likely too soft blown up - consider the metadata-feed route"
    print(f"             -> {note}")


async def main():
    try:
        mgr = await MediaManager.request_async()
    except Exception as exc:
        sys.exit(f"Could not reach the SMTC session manager: {exc}")

    sessions = list(mgr.get_sessions())

    if not sessions:
        sys.exit(
            "No media sessions found.\n"
            "Start playback first, then run this again. Note that some apps "
            "only register with SMTC once audio is actually playing."
        )

    print(f"Found {len(sessions)} session(s).")

    current = mgr.get_current_session()
    current_id = current.source_app_user_model_id if current else None

    for i, session in enumerate(sessions):
        await dump_session(i, session)
        if current_id and session.source_app_user_model_id == current_id:
            print("             (this is the current session)")

    print(
        "\nKeep the app id of the session you care about - that's how you "
        "filter to it later instead of grabbing whatever is 'current'."
    )


if __name__ == "__main__":
    asyncio.run(main())
