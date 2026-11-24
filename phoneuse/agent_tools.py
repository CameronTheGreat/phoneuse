"""Find visible controls, act once, and report what the mirror showed."""

import argparse
import contextlib
import io
import json
import tempfile
import time
from pathlib import Path

from . import cli, commands, unlock


ROW_TOLERANCE = 0.025  # fraction of screen height between OCR line centers


class ScreenStop(Exception):
    def __init__(self, status, **details):
        self.result = {"status": status, **details}


def mirror_ready():
    try:
        state = cli.picture_state()
    except (OSError, ValueError):
        raise ScreenStop("mirror_unavailable") from None
    if state.get("paused"):
        raise ScreenStop("paused")
    if not state.get("live"):
        raise ScreenStop("mirror_unavailable")
    return not state.get("stale")


def matches(lines, label):
    want = " ".join(label.casefold().split())
    exact, prefix = [], []
    for x, y, text in lines:
        value = " ".join(text.casefold().split())
        if value == want:
            exact.append({"text": text, "x": x, "y": y})
        elif value.startswith(want + " "):
            prefix.append({"text": text, "x": x, "y": y})
    return exact or prefix


def wait_screen(check, deadline, *, read=True, show_all=False, interval=0.2):
    """Only run OCR for new screen states. An unchanged screen can remain live."""
    stale_since = None
    seq_seen = None
    while (now := time.monotonic()) < deadline:
        if mirror_ready():
            stale_since = None
            seq = cli.frame_seq()
            if seq is None or seq != seq_seen:
                lines = []
                if read:
                    lines = cli.screen_text(show_all=True) if show_all else cli.screen_text()
                if mirror_ready():
                    seq_seen = seq
                    value = check(lines)
                    if value and time.monotonic() < deadline and mirror_ready():
                        return value
        else:
            seq_seen = None
            stale_since = now if stale_since is None else stale_since
            if now - stale_since >= 2:
                raise ScreenStop("mirror_stale")
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(interval, remaining))
    raise ScreenStop("mirror_stale" if stale_since is not None else "timeout")


def wait_for(label, timeout=10):
    try:
        hits = wait_screen(lambda lines: matches(lines, label), time.monotonic() + timeout)
        return {"status": "visible" if len(hits) == 1 else "ambiguous", "matches": hits[:6]}
    except ScreenStop as e:
        return e.result


def wait_for_unlock(timeout=commands.UNLOCK_WAIT_DEFAULT):
    try:
        wait_screen(unlock.shows_unlocked, time.monotonic() + timeout, show_all=True, interval=0.75)
        return {"status": "unlocked"}
    except ScreenStop as e:
        return e.result


def find_in_scroll(label, direction="down", max_scrolls=6, timeout=30):
    """Observe the initial screen and the result of each bounded scroll."""
    deadline = time.monotonic() + timeout
    scrolls, input_sent = 0, False
    h = None
    dy = 0.45 if direction == "down" else -0.45
    try:
        while True:
            hits, = wait_screen(lambda lines: (matches(lines, label),), deadline)
            if hits:
                return {"status": "found" if len(hits) == 1 else "ambiguous", "matches": hits[:6],
                        "scrolls": scrolls, "input_sent": input_sent}
            if scrolls >= max_scrolls:
                raise ScreenStop("scroll_limit")
            if h is None:
                h = cli.hid()
                # The screen may have changed while the owner granted control.
                continue
            if not mirror_ready():
                raise ScreenStop("mirror_stale")
            if time.monotonic() >= deadline:
                raise ScreenStop("timeout")
            h.first_seq = None
            input_sent = None
            cli.scroll_by(h, dy)
            scrolls += 1
            input_sent = True
            cli.acted(h.first_seq)
    except ScreenStop as e:
        return {**e.result, "scrolls": scrolls, "input_sent": input_sent}
    except (RuntimeError, OSError) as e:
        return {"status": "input_error", "scrolls": scrolls, "input_sent": input_sent, "detail": str(e)}


def tap_on(label, timeout=10, expect=None, *, near=None, region=None):
    stage, input_sent = "target", False
    deadline = time.monotonic() + timeout
    try:
        def resolve(lines):
            candidates = lines
            if region is not None:
                left, top, right, bottom = region
                candidates = [(x, y, text) for x, y, text in lines if left <= x <= right and top <= y <= bottom]
            anchor = None
            if near is not None:
                anchors = matches(candidates, near)
                if len(anchors) > 1:
                    raise ScreenStop("ambiguous_anchor", matches=anchors[:6])
                if not anchors:
                    return None
                anchor = anchors[0]
                candidates = [(x, y, text) for x, y, text in candidates
                              if abs(y - anchor["y"]) <= ROW_TOLERANCE
                              and (x, y, text) != (anchor["x"], anchor["y"], anchor["text"])]
            hits = matches(candidates, label)
            if not hits:
                return None
            icon = cli.home_icon_target(lines, hits[0]["x"], hits[0]["y"]) if len(hits) == 1 else None
            return hits, anchor, bool(expect and matches(lines, expect)), icon

        h = None
        for _ in range(2):
            hits, anchor, expected_visible, icon = wait_screen(resolve, deadline)
            if len(hits) != 1:
                raise ScreenStop("ambiguous", matches=hits[:6])
            if expected_visible:
                raise ScreenStop("expected_already_visible", expected=expect)
            hit = hits[0]
            if h is None:
                h = cli.hid()
            label_point = hit["x"], hit["y"]
            tap_point = icon or label_point
            points = [label_point]
            if icon:
                points.append(icon)
            if anchor:
                points.append((anchor["x"], anchor["y"]))
            if not any(cli.moved_since_look(*point, strict=True) for point in points) and mirror_ready():
                break
        else:
            raise ScreenStop("target_changed")

        stage = "tap"
        input_sent = None  # a failed Bluetooth call may have sent part of the input
        h.first_seq = None
        cli.touch.tap(h, *tap_point)
        input_sent = True
        cli.acted(h.first_seq)
        if expect:
            stage = "expected"
            confirmed = wait_screen(lambda lines: matches(lines, expect), time.monotonic() + timeout)
            return {"status": "confirmed", "input_sent": True, "target": hit, "observed": confirmed[0]}
        return {"status": "tapped", "input_sent": True, "target": hit}
    except ScreenStop as e:
        return {**e.result, "stage": stage, "input_sent": input_sent}
    except RuntimeError as e:
        return {"status": "input_error", "stage": stage, "input_sent": input_sent, "detail": str(e)}


def inspect_region(left, top, right, bottom, path):
    from PIL import Image

    commands.check_region((left, top, right, bottom))
    def capture(_):
        with tempfile.TemporaryDirectory() as folder:
            shot = Path(folder) / "screen.png"
            with contextlib.redirect_stdout(io.StringIO()):
                cli.screenshot(shot)
            with Image.open(shot) as img:
                width, height = img.size
                x0, y0 = int(left * width), int(top * height)
                x1, y1 = max(x0 + 1, round(right * width)), max(y0 + 1, round(bottom * height))
                bounds = [x0 / width, y0 / height, x1 / width, y1 / height]
                img.crop((x0, y0, x1, y1)).save(path, format="PNG")
        read = cli.vision({"op": "text", "image": str(Path(path).resolve())})
        lines = [{"text": line["text"], "x": round((x0 + line["x"] * (x1 - x0)) / width, 3),
                  "y": round((y0 + line["y"] * (y1 - y0)) / height, 3)} for line in read.get("lines", [])]
        return {"status": "captured", "bounds": bounds, "screen_pixels": [width, height], "lines": lines[:20]}

    try:
        return wait_screen(capture, time.monotonic() + 4, read=False)
    except ScreenStop as e:
        return e.result


def run(command, args):
    parser = argparse.ArgumentParser(prog=f"phoneuse {command}")
    parser.add_argument("--json", action="store_true")
    if command == "inspect-region":
        for name in ("left", "top", "right", "bottom"):
            parser.add_argument(name, type=float)
        parser.add_argument("path")
    elif command == "wait-for-unlock":
        parser.add_argument("--timeout", type=float, default=commands.UNLOCK_WAIT_DEFAULT)
    else:
        parser.add_argument("label", nargs="+")
        parser.add_argument("--timeout", type=float, default=30 if command == "find-in-scroll" else 10)
        if command == "tap-on":
            parser.add_argument("--expect")
            parser.add_argument("--near")
            parser.add_argument("--region", metavar="LEFT,TOP,RIGHT,BOTTOM")
            parser.add_argument("--text", action="store_true", help="read the full screen after the tap")
        if command == "find-in-scroll":
            parser.add_argument("--direction", choices=("up", "down"), default="down")
            parser.add_argument("--max-scrolls", type=int, default=6)
    options = parser.parse_args(args)
    try:
        if command == "inspect-region":
            result = inspect_region(options.left, options.top, options.right, options.bottom, options.path)
        elif command == "wait-for-unlock":
            if not 0 < options.timeout <= commands.UNLOCK_WAIT_MAX:
                parser.error(f"--timeout must be greater than zero and at most {commands.UNLOCK_WAIT_MAX} seconds")
            result = wait_for_unlock(options.timeout)
        else:
            label = " ".join(options.label).strip()
            if not label or not 0 < options.timeout <= 60:
                parser.error("supply a label and a timeout greater than zero and at most 60 seconds")
            if command == "tap-on":
                if options.near is not None and not options.near.strip():
                    parser.error("--near requires a visible label")
                region = None
                if options.region is not None:
                    region = commands.check_region(map(float, options.region.split(",")))
                result = tap_on(label, options.timeout, options.expect, near=options.near, region=region)
            elif command == "find-in-scroll":
                if not 0 <= options.max_scrolls <= commands.MAX_SCROLLS:
                    parser.error(f"--max-scrolls must be between 0 and {commands.MAX_SCROLLS}")
                result = find_in_scroll(label, options.direction, options.max_scrolls, options.timeout)
            else:
                result = wait_for(label, options.timeout)
    except ValueError as e:
        parser.error(str(e))
    if command == "tap-on" and options.text and result["status"] in {"tapped", "confirmed"}:
        result["lines"] = [{"x": x, "y": y, "text": text} for x, y, text in cli.screen_text()]
    if options.json:
        print(json.dumps(result, ensure_ascii=False))
    else:
        first = result.get("observed") or result.get("target")
        print(result["status"] + (f": {first['text']}" if first else ""))
        if "scrolls" in result:
            print(f"scrolls={result['scrolls']}")
        if "input_sent" in result:
            print(f"input_sent={result['input_sent']} stage={result.get('stage', 'complete')}")
        for hit in result.get("matches", result.get("lines", [])):
            print(f"{hit['x']:.3f} {hit['y']:.3f}  {hit['text']}")
        if result["status"] == "captured":
            print(f"{options.path} bounds={result['bounds']}")
    return result["status"] in {"unlocked", "visible", "tapped", "confirmed", "captured", "found"}
