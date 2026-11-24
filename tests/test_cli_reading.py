"""Regressions from reading an X feed on an unblurred Spotlight layout."""
import numpy as np
import pytest

from phoneuse import cli


EMPTY_SEARCH = [(0.25, 0.066, "Search or Ask"), (0.15, 0.41, "Settings")]
EMPTY_SEARCH += [(0.1 + i * 0.1, 0.72, key) for i, key in enumerate("qwerty")]


def test_unblurred_spotlight_and_home_are_distinguished():
    assert cli.spotlight_open(EMPTY_SEARCH)
    assert cli.spotlight_tap(EMPTY_SEARCH, "X", 956) == "empty"
    assert not cli.spotlight_open([(0.15, 0.41, "Settings"), (0.5, 0.9, "Search")])
    assert cli.spotlight_open([(0.25, 0.066, "Search or Ask")])
    assert not cli.spotlight_open([(0.25, 0.72, "Search or Ask")])


def test_already_open_search_is_not_toggled(monkeypatch):
    monkeypatch.setattr(cli, "screen_text", lambda: EMPTY_SEARCH)

    class Hid:
        def cmd(self, command):
            pytest.fail(f"Already-open Spotlight must not receive {command}")

    assert cli.open_spotlight(Hid(), np.zeros((5, 5)))


def test_open_search_uses_ocr_instead_of_blur(monkeypatch):
    screens = iter([[], EMPTY_SEARCH])
    monkeypatch.setattr(cli, "screen_text", lambda: next(screens))
    monkeypatch.setattr(cli, "acted", lambda seq: None)
    monkeypatch.setattr(cli, "_differs", lambda ref: pytest.fail("OCR can identify search"))
    sent = []

    class Hid:
        def cmd(self, command):
            sent.append(command)

    assert cli.open_spotlight(Hid(), None)
    assert sent == ["key space gui"]


def test_failed_search_open_has_bounded_retries(monkeypatch):
    monkeypatch.setattr(cli, "screen_text", lambda: [])
    monkeypatch.setattr(cli, "wait_for_spotlight", lambda: [])
    monkeypatch.setattr(cli, "acted", lambda seq: None)
    h = RecordingHid()
    assert not cli.open_spotlight(h, None)
    assert h.sent == ["key space gui"]


def test_visible_home_search_is_used_only_after_shortcut_fails(monkeypatch):
    home = [(0.5, 0.814, "Q Search")]
    screens = iter([home, EMPTY_SEARCH])
    taps = []
    monkeypatch.setattr(cli, "screen_text", lambda: home)
    monkeypatch.setattr(cli, "wait_for_spotlight", lambda: next(screens))
    monkeypatch.setattr(cli, "moved_since_look", lambda *pos, strict: None)
    monkeypatch.setattr(cli, "acted", lambda seq: None)
    monkeypatch.setattr(cli.touch, "tap", lambda h, x, y: taps.append((x, y)))
    h = RecordingHid()
    assert cli.open_spotlight(h, None)
    assert taps == [(0.5, 0.814)] and h.sent == ["key space gui"]


def test_delayed_spotlight_does_not_receive_a_second_input(monkeypatch):
    home = [(0.5, 0.814, "Q Search")]
    screens = iter([home, home, EMPTY_SEARCH])
    monkeypatch.setattr(cli, "screen_text", lambda: next(screens))
    monkeypatch.setattr(cli, "acted", lambda seq: None)
    monkeypatch.setattr(cli.time, "sleep", lambda seconds: None)
    monkeypatch.setattr(cli.touch, "tap", lambda *args: pytest.fail("Search already opened"))
    h = RecordingHid()
    assert cli.open_spotlight(h, None)
    assert h.sent == ["key space gui"]


def test_empty_search_retries_typing_without_toggling(monkeypatch):
    monkeypatch.setattr(cli, "home", lambda h: None)
    monkeypatch.setattr(cli, "refuse_if_locked", lambda: None)
    monkeypatch.setattr(cli.screen, "frames_request", lambda *a, **kw: ("ok", b""))
    monkeypatch.setattr(cli, "_thumb", lambda: np.zeros((5, 5)))
    monkeypatch.setattr(cli, "_differs", lambda ref: True)
    monkeypatch.setattr(cli, "step", lambda *a, **kw: None)
    monkeypatch.setattr(cli.time, "sleep", lambda seconds: None)
    hits = iter(["empty", (0.17, 0.20)])
    monkeypatch.setattr(cli, "spotlight_hit", lambda name: next(hits))
    opened, tapped = [], []
    monkeypatch.setattr(cli, "open_spotlight", lambda *a: opened.append(True) or True)
    monkeypatch.setattr(cli.touch, "tap", lambda h, x, y: tapped.append((x, y)))
    cli._open_app_ble(RecordingHid(), "X")
    assert opened == [True]
    assert tapped == [(0.17, 0.20)]


def test_headerless_x_result_taps_icon_above_label():
    lines = [(0.21, 0.068, "X-Open"), (0.85, 0.066, "X"),
             (0.182, 0.126, "Sleep"), (0.169, 0.198, "X"), (0.393, 0.198, "X"),
             (0.169, 0.247, "X"), (0.389, 0.247, "Xfinity"), (0.609, 0.247, "XBOX"),
             (0.12, 0.287, "Apps"), (0.84, 0.287, "Show More")]
    assert cli.spotlight_tap(lines, "X", 956) == (0.169, 0.247 - 47 / 956)


def test_spotlight_with_retained_query_and_no_heading_uses_the_result_icon():
    lines = [(0.198, 0.068, "Settings"), (0.182, 0.126, "Sleep"),
             (0.171, 0.247, "Settings"), (0.839, 0.412, "Show More")]
    assert not cli.spotlight_open(lines)
    lines += [(0.059 + 0.1 * i, 0.716, key) for i, key in enumerate("qwerty")]
    assert cli.spotlight_open(lines)
    assert cli.spotlight_tap(lines, "Settings", 956) == (0.171, 0.247 - 47 / 956)


def test_x_result_does_not_match_search_field_icon_or_keyboard():
    lines = [(0.21, 0.068, "X-Open"), (0.85, 0.066, "X"), (0.12, 0.287, "Apps"),
             (0.84, 0.287, "Show More"), (0.30, 0.83, "X")]
    assert cli.spotlight_tap(lines, "X", 956) == "missing"


def test_app_below_apps_heading_uses_its_row_in_headerless_search():
    lines = [(0.23, 0.068, "X-Open"), (0.12, 0.13, "Apps"),
             (0.84, 0.13, "Show More"), (0.167, 0.20, "X")]
    assert cli.spotlight_tap(lines, "X", 956) == (0.167, 0.20)


@pytest.mark.parametrize("picture, message", [
    ({"agent_state": "driving"}, "an agent already has control of the iPhone."),
    ({"agent_state": "requested"}, "an agent is waiting for access."),
    ({"agent_state": "idle"}, "no agent has control of the iPhone."),
    ({"paused": True}, "PAUSED. The owner took over."),
    ({}, "unknown."),
])
def test_status_reports_agent_control(monkeypatch, capsys, picture, message):
    monkeypatch.setattr(cli, "IS_WINDOWS", True)
    monkeypatch.setattr(cli, "Hid", RecordingHid)
    monkeypatch.setattr(cli.screen, "hub_request", lambda *a, **kw: ("ok", b""))
    monkeypatch.setattr(cli, "picture_state", lambda: picture)
    cli.status()
    assert f"Agent control: {message}" in capsys.readouterr().out


class Dots:
    def show(self, dot):
        pass

    show_now = show


class RecordingHid:
    def __init__(self):
        self.sent = []

    def cmd(self, command):
        self.sent.append(command)
        return "ok"


def test_typing_replaces_selection_baseline_but_drags_keep_press_baseline(monkeypatch):
    monkeypatch.setattr(cli, "_dots", Dots())
    seqs = iter([10, 20, 30])
    monkeypatch.setattr(cli, "frame_seq", lambda: next(seqs))
    h = cli.AgentHid(RecordingHid())
    h.cmd("key a gui")
    assert h.first_seq == 10
    h.cmd("type X")
    assert h.first_seq == 20
    h.first_seq = None
    h.cmd("a 0.5 0.7 1")
    h.cmd("m 0 -10 1")
    h.cmd("up")
    assert h.first_seq == 30


def test_typing_waits_on_its_own_input_and_consumes_baseline(monkeypatch):
    monkeypatch.setattr(cli, "_dots", Dots())
    monkeypatch.setattr(cli, "refuse_if_locked", lambda: None)
    monkeypatch.setattr(cli, "frame_seq", lambda: 42)
    monkeypatch.setattr(cli, "acted", lambda seq: None)
    requests = []
    monkeypatch.setattr(cli.screen, "frames_request",
                        lambda req, **kwargs: (requests.append(req) or "ok", b""))
    h = cli.AgentHid(RecordingHid())
    h.first_seq = 3  # stale select-all baseline
    cli.type_text(h, "X")
    assert requests == ["settle 42 0.35 3.0"]
    assert h.first_seq is None


@pytest.mark.parametrize("status,expected", [
    ("ok seq=25 ignored=0.4", False),  # known video
    ("ok seq=25 ignored=0.0", True),
    ("ok seq=11 ignored=0.0", False),  # one late animation frame
    ("ok seq=25", True),  # older frames services
])
def test_video_motion_is_not_reported_as_unexpected_input(monkeypatch, status, expected):
    monkeypatch.setattr(cli.screen, "frames_request", lambda *a, **kw: (status, b""))
    assert cli.unexpected_change(10) is expected


def test_explicit_user_takeover_still_stops_input(monkeypatch, capsys):
    from phoneuse import agent_session
    monkeypatch.setattr(agent_session, "start_setting", lambda: {"mode": "auto"})
    monkeypatch.setattr(cli.ipc, "http_post", lambda *a, **kw:
                        (409, b'{"result": "The user took over the phone"}'))
    monkeypatch.setattr(cli, "unexpected_change", lambda *a: pytest.fail("Must stop before inspecting motion"))
    with pytest.raises(SystemExit):
        cli.agent_turn()
    assert "user took over" in capsys.readouterr().err


def test_frames_status_exposes_video_mask_without_changing_sequence():
    from phoneuse.frames import Frames
    frames = Frames()
    frames.seq, frames.dynamic_frac = 25, 0.4

    class Source:
        def live(self):
            return True

        def frame_size(self):
            return 1320, 2868

    frames.source = Source()
    replies = []

    class Conn:
        def sendall(self, data):
            replies.append(data)

    frames.client(Conn(), [b"status\n"])
    assert replies == [b"ok live=1 seq=25 size=1320x2868 ignored=0.400\n"]


