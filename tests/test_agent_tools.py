import asyncio
import json
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

from phoneuse import agent_tools as a, commands


def test_tap_resolves_a_moved_target_and_verifies_once(monkeypatch):
    screens = iter([[(0.5, 0.4, "Cart")], [(0.5, 0.6, "Cart")], [(0.5, 0.8, "Checkout")]])
    guards = iter(["moved", None])
    taps = []
    monkeypatch.setattr(a, "mirror_ready", lambda: True)
    monkeypatch.setattr(a.cli, "frame_seq", lambda: None)
    monkeypatch.setattr(a.cli, "screen_text", lambda: next(screens))
    monkeypatch.setattr(a.cli, "hid", lambda: SimpleNamespace(first_seq=None))
    monkeypatch.setattr(a.cli, "moved_since_look", lambda *args, **kwargs: next(guards))
    monkeypatch.setattr(a.cli, "acted", lambda seq: None)
    monkeypatch.setattr(a.cli.touch, "tap", lambda h, x, y: taps.append((x, y)))
    result = a.tap_on("Cart", expect="Checkout")
    assert result["status"] == "confirmed" and result["input_sent"]
    assert taps == [(0.5, 0.6)]


def test_duplicate_text_is_refused_before_hid(monkeypatch):
    monkeypatch.setattr(a, "wait_screen", lambda check, deadline: check([(0.3, 0.4, "Cart"), (0.3, 0.6, "Cart")]))
    monkeypatch.setattr(a.cli, "hid", lambda: pytest.fail("Ambiguous target must not touch HID"))
    result = a.tap_on("Cart")
    assert result["status"] == "ambiguous" and not result["input_sent"]
    assert len(result["matches"]) == 2


@pytest.mark.parametrize("grid, expected_y", [(True, 0.409 - 47 / 956), (False, 0.409)])
def test_home_screen_icon_tap_requires_a_grid_and_guards_the_icon(monkeypatch, grid, expected_y):
    lines = [(0.154, 0.409, "Settings"), (0.501, 0.814, "Q Search")]
    if grid:
        lines += [(0.383, 0.410, "Camera"), (0.613, 0.409, "Photos"), (0.841, 0.409, "Home"),
                  (0.153, 0.523, "Maps"), (0.385, 0.523, "FaceTime"), (0.613, 0.523, "Files")]
    taps, guards = [], []
    monkeypatch.setattr(a, "wait_screen", lambda check, deadline: check(lines))
    monkeypatch.setattr(a, "mirror_ready", lambda: True)
    monkeypatch.setattr(a.cli, "hid", lambda: SimpleNamespace(first_seq=None))
    monkeypatch.setattr(a.cli.orientation, "current_points", lambda: (440, 956))
    monkeypatch.setattr(a.cli, "moved_since_look", lambda x, y, strict: guards.append((x, y)))
    monkeypatch.setattr(a.cli, "acted", lambda seq: None)
    monkeypatch.setattr(a.cli.touch, "tap", lambda h, x, y: taps.append((x, y)))
    assert a.tap_on("Settings")["target"] == {"text": "Settings", "x": 0.154, "y": 0.409}
    assert taps == [(0.155 if grid else 0.154, expected_y)]
    assert guards == ([(0.154, 0.409), taps[0]] if grid else taps)


@pytest.mark.parametrize("scope, expected_x", [({"near": "Fries"}, 0.8),
                                             ({"region": (0.6, 0.65, 1, 0.8)}, 0.7)])
def test_scoped_tap_uses_unique_match_and_verifies_outside_scope(monkeypatch, scope, expected_x):
    screens = iter([[(0.2, 0.39, "Fries"), (0.8, 0.4, "Add"), (0.7, 0.7, "Add")],
                    [(0.4, 0.2, "Added to cart")]])
    taps = []
    monkeypatch.setattr(a, "wait_screen", lambda check, deadline: check(next(screens)))
    monkeypatch.setattr(a, "mirror_ready", lambda: True)
    monkeypatch.setattr(a.cli, "hid", lambda: SimpleNamespace(first_seq=None))
    monkeypatch.setattr(a.cli, "moved_since_look", lambda *args, **kwargs: None)
    monkeypatch.setattr(a.cli, "acted", lambda seq: None)
    monkeypatch.setattr(a.cli.touch, "tap", lambda h, x, y: taps.append((x, y)))
    result = a.tap_on("Add", expect="Added to cart", **scope)
    assert result["status"] == "confirmed"
    assert [x for x, _ in taps] == [expected_x]


def test_scoped_tap_refuses_duplicate_anchors(monkeypatch):
    lines = [(0.2, 0.4, "Fries"), (0.8, 0.4, "Add"), (0.2, 0.7, "Fries")]
    monkeypatch.setattr(a, "wait_screen", lambda check, deadline: check(lines))
    monkeypatch.setattr(a.cli, "hid", lambda: pytest.fail("Ambiguous anchor must not touch HID"))
    result = a.tap_on("Add", near="Fries")
    assert result["status"] == "ambiguous_anchor" and result["input_sent"] is False


def test_find_in_scroll_rechecks_after_approval_and_stops_on_match(monkeypatch):
    screens = iter([[], [], [(0.3, 0.6, "Fries")]])
    monkeypatch.setattr(a, "wait_screen", lambda check, deadline: check(next(screens)))
    monkeypatch.setattr(a, "mirror_ready", lambda: True)
    monkeypatch.setattr(a.cli, "hid", lambda: SimpleNamespace(first_seq=None))
    scrolls, frames = [], []

    def scroll(h, dy):
        scrolls.append(dy)
        h.first_seq = 42

    monkeypatch.setattr(a.cli, "scroll_by", scroll)
    monkeypatch.setattr(a.cli, "acted", frames.append)
    assert a.find_in_scroll("Fries", direction="up") == {
        "status": "found", "matches": [{"text": "Fries", "x": 0.3, "y": 0.6}],
        "scrolls": 1, "input_sent": True,
    }
    assert scrolls == [-0.45] and frames == [42]


@pytest.mark.parametrize("lines, status", [([], "scroll_limit"), ([(0.5, 0.4, "Fries")], "found"),
                                           ([(0.5, 0.4, "Fries"), (0.5, 0.7, "Fries")], "ambiguous")])
def test_find_in_scroll_checks_visible_list_without_input(monkeypatch, lines, status):
    monkeypatch.setattr(a, "wait_screen", lambda check, deadline: check(lines))
    monkeypatch.setattr(a.cli, "hid", lambda: pytest.fail("Searching this screen requires no HID"))
    result = a.find_in_scroll("Fries", max_scrolls=0 if not lines else 6)
    assert result["status"] == status and result["scrolls"] == 0 and result["input_sent"] is False


@pytest.mark.parametrize("paused", [False, True])
def test_find_in_scroll_stops_at_limit_or_takeover(monkeypatch, paused):
    scrolls = []

    def wait(check, deadline):
        if paused and scrolls:
            raise a.ScreenStop("paused")
        return check([])

    monkeypatch.setattr(a, "wait_screen", wait)
    monkeypatch.setattr(a, "mirror_ready", lambda: True)
    monkeypatch.setattr(a.cli, "hid", lambda: SimpleNamespace(first_seq=None))
    monkeypatch.setattr(a.cli, "scroll_by", lambda h, dy: scrolls.append(dy))
    monkeypatch.setattr(a.cli, "acted", lambda seq: None)
    assert a.find_in_scroll("Fries", max_scrolls=2) == {
        "status": "paused" if paused else "scroll_limit", "scrolls": 1 if paused else 2, "input_sent": True,
    }
    assert scrolls == [0.45] * (1 if paused else 2)


def test_stall_after_tap_leaves_input_sent(monkeypatch):
    waits = iter([True, False])
    taps = []

    def wait(check, deadline):
        if next(waits):
            return check([(0.5, 0.6, "Cart")])
        raise a.ScreenStop("mirror_stale")

    monkeypatch.setattr(a, "wait_screen", wait)
    monkeypatch.setattr(a, "mirror_ready", lambda: True)
    monkeypatch.setattr(a.cli, "hid", lambda: SimpleNamespace(first_seq=None))
    monkeypatch.setattr(a.cli, "moved_since_look", lambda *args, **kwargs: None)
    monkeypatch.setattr(a.cli, "acted", lambda seq: None)
    monkeypatch.setattr(a.cli.touch, "tap", lambda h, x, y: taps.append((x, y)))
    assert a.tap_on("Cart", expect="Checkout") == {"status": "mirror_stale", "stage": "expected", "input_sent": True}
    assert len(taps) == 1


def test_stale_frame_recovers_without_ocr_until_ready(monkeypatch):
    states = iter([False, False, True, True, True])
    monkeypatch.setattr(a, "mirror_ready", lambda: next(states))
    monkeypatch.setattr(a.time, "sleep", lambda seconds: None)
    monkeypatch.setattr(a.cli, "frame_seq", lambda: 1)
    monkeypatch.setattr(a.cli, "screen_text", lambda: [(0.4, 0.3, "Checkout")])
    assert a.wait_for("Checkout")["status"] == "visible"


@pytest.mark.parametrize("outcome", ["unlocked", "timeout", "paused", "mirror_stale", "mirror_unavailable"])
def test_wait_for_unlock_only_accepts_a_fresh_unlocked_screen(monkeypatch, outcome):
    now = 0.0
    reads = []

    def sleep(seconds):
        nonlocal now
        now += seconds

    def picture():
        return {"live": outcome != "mirror_unavailable" or now == 0,
                "stale": outcome == "mirror_stale" and now > 0,
                "paused": outcome == "paused" and now > 0}

    def read(*, show_all):
        assert show_all
        reads.append(now)
        if now >= 1.5 and outcome == "unlocked":
            return [(0.14, 0.044, "1.23"), (0.16, 0.409, "Settings")]
        if now == 0:
            return [(0.14, 0.044, "1:23"), (0.50, 0.35, "Enter iPhone Passcode")]
        return [(0.50, 0.24, "1:23"), (0.24, 0.61, "Flashlight")]

    monkeypatch.setattr(a.time, "monotonic", lambda: now)
    monkeypatch.setattr(a.time, "sleep", sleep)
    monkeypatch.setattr(a.cli, "picture_state", picture)
    monkeypatch.setattr(a.cli, "frame_seq", lambda: int(now / 1.5))
    monkeypatch.setattr(a.cli, "screen_text", read)
    monkeypatch.setattr(a.cli, "hid", lambda: pytest.fail("Unlock observation must not request HID"))
    assert a.wait_for_unlock(timeout=4) == {"status": outcome}
    if outcome in {"unlocked", "timeout"}:
        assert reads[:2] == [0, 1.5]
    else:
        assert reads == [0]


def test_crop_reports_positions_in_full_frame(monkeypatch, tmp_path):
    from PIL import Image

    path = tmp_path / "crop.png"
    monkeypatch.setattr(a, "wait_screen", lambda check, deadline, **kwargs: check([]))
    monkeypatch.setattr(a.cli, "screenshot", lambda shot: Image.new("RGB", (100, 200)).save(shot))
    monkeypatch.setattr(a.cli, "vision", lambda req: {"lines": [{"x": 0.5, "y": 0.5, "text": "Cart"}]})
    result = a.inspect_region(0.2, 0.5, 0.6, 0.8, path)
    with Image.open(path) as img:
        assert img.size == (40, 60)
    assert result["lines"] == [{"x": 0.4, "y": 0.65, "text": "Cart"}]


def test_mcp_discovers_manifest_over_stdio(tmp_path):
    launcher = Path(__file__).resolve().parents[1] / "bin" / "phoneuse"
    requests = [("initialize", {"protocolVersion": "2025-11-25"}), ("tools/list", {}),
                ("tools/call", {"name": "tap_on", "arguments": {"label": "Cart", "timeout": 0}}),
                ("tools/call", {"name": "run_steps", "arguments": {"steps": []}})]
    messages = [dict(jsonrpc="2.0", id=i, method=method, params=params)
                for i, (method, params) in enumerate(requests)]
    proc = subprocess.run([sys.executable, str(launcher), "mcp"], cwd=tmp_path, timeout=10, check=True,
                          input="".join(json.dumps(msg) + "\n" for msg in messages), text=True, capture_output=True)
    initialized, listing, invalid, invalid_batch = [json.loads(line)["result"] for line in proc.stdout.splitlines()]
    assert initialized["protocolVersion"] == "2025-11-25"
    assert "stop tool calls until it returns" in initialized["instructions"]
    schemas = {tool["name"]: tool["inputSchema"] for tool in listing["tools"]}
    steps_schema = schemas.pop("run_steps")["properties"]["steps"]
    assert steps_schema["maxItems"] == 6
    assert {"scroll_by", "type_text", "press_key"} <= set(steps_schema["items"]["properties"]["name"]["enum"])
    assert schemas == {name: command.schema() for name, command in commands.TOOLS.items()}
    assert invalid["isError"]
    assert invalid_batch["isError"]


@pytest.mark.parametrize("name, args, expected, status", [
    ("wait_for_unlock", {"timeout": 10}, (10,), "unlocked"),
    ("tap_on", {"label": "Add", "near": "Fries", "region": [0, 0.3, 1, 0.7]},
     ("Add", 10, None, "Fries", (0.0, 0.3, 1.0, 0.7)), "tapped"),
    ("find_in_scroll", {"label": "Fries", "direction": "up", "max_scrolls": 4, "timeout": 8},
     ("Fries", "up", 4, 8), "found"),
])
def test_mcp_passes_new_arguments_through_cli(monkeypatch, capsys, name, args, expected, status):
    from phoneuse import mcp_server

    def tool(*values, **scope):
        assert (*values, *scope.values()) == expected
        return {"status": status}

    def invoke(command, arguments, artifact, job, *, timeout):
        cli_args = command.arguments(arguments)
        monkeypatch.setattr(sys, "argv", ["phoneuse", *cli_args])
        with pytest.raises(SystemExit) as exit:
            a.cli._main()
        streams = capsys.readouterr()
        return SimpleNamespace(returncode=exit.value.code, stdout=streams.out, stderr=streams.err)

    monkeypatch.setattr(a.cli, "can_reach_services", lambda: True)
    monkeypatch.setattr(a, name, tool)
    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool(name, args)
    assert not result["isError"] and result["structuredContent"] == {"status": status}


def test_mcp_bounds_an_unresponsive_unlock_observation(monkeypatch):
    from phoneuse import mcp_server

    def invoke(command, arguments, artifact, job, *, timeout):
        assert command.cli == "wait-for-unlock" and timeout == 14
        raise subprocess.TimeoutExpired(command.cli, timeout)

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("wait_for_unlock", {"timeout": 10})
    assert result["isError"] and result["structuredContent"] == {"status": "timeout"}


def test_mcp_http_discovery_and_browser_request_rejection(monkeypatch):
    aiohttp_test = pytest.importorskip("aiohttp.test_utils")
    from phoneuse import mcp_server

    def invoke(command, arguments, artifact, job, *, timeout):
        assert command.cli == "text" and arguments == {}
        return SimpleNamespace(returncode=0, stdout="0.5 0.4  About", stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)

    async def check():
        async with aiohttp_test.TestClient(aiohttp_test.TestServer(mcp_server.http_app())) as client:
            initialize = {"jsonrpc": "2.0", "id": 1, "method": "initialize",
                          "params": {"protocolVersion": "2025-11-25"}}
            reply = await client.post("/mcp", json=initialize)
            assert reply.status == 200
            assert "native WinUI" in (await reply.json())["result"]["instructions"]
            listing = {"jsonrpc": "2.0", "id": 2, "method": "tools/list"}
            reply = await client.post("/mcp", json=listing)
            assert {"show_mirror", "request_unlock", "wait_for_unlock", "reconnect_bluetooth", "run_steps"} <= {
                tool["name"] for tool in (await reply.json())["result"]["tools"]}
            reply = await client.post("/mcp", json={"jsonrpc": "2.0", "method": "notifications/initialized"})
            assert reply.status == 202
            assert (await client.get("/mcp")).status == 405
            call = {"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                    "params": {"name": "open_app", "arguments": {"app": "X"}}}
            assert (await client.post("/mcp", json=call, headers={"Origin": "https://example.com"})).status == 403
            assert (await client.post("/mcp", json=call, headers={"Host": "example.com"})).status == 403
            call["params"] = {"name": "run_steps", "arguments": {"steps": [{"name": "look"}]}}
            reply = await client.post("/mcp", json=call)
            assert reply.status == 200
            batch = (await reply.json())["result"]
            assert batch["structuredContent"]["result"] == {"text": "0.5 0.4  About"}

    asyncio.run(check())


def test_run_steps_executes_in_order_and_returns_only_final_observation(monkeypatch):
    from phoneuse import mcp_server

    calls = []
    replies = iter(["ok", '{"status":"confirmed","input_sent":true,"observed":{"text":"About"}}',
                    "0.5 0.4  About"])

    def invoke(command, arguments, artifact, job, *, timeout):
        calls.append((command.cli, arguments))
        assert 0 < timeout <= mcp_server.STEPS_TIMEOUT
        return SimpleNamespace(returncode=0, stdout=next(replies), stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [
        {"name": "open_app", "arguments": {"app": "Settings"}},
        {"name": "tap_on", "arguments": {"label": "General", "expect": "About"}},
        {"name": "look"},
    ]})
    assert calls == [("open", {"app": "Settings"}), ("tap-on", {"label": "General", "expect": "About"}),
                     ("text", {})]
    assert not result["isError"]
    assert result["structuredContent"] == {
        "status": "completed",
        "steps": [{"name": "open_app", "status": "ok", "input_sent": True},
                  {"name": "tap_on", "status": "confirmed", "input_sent": True},
                  {"name": "look", "status": "ok"}],
        "result": {"text": "0.5 0.4  About"},
    }


def test_run_steps_batches_search_and_scroll(monkeypatch):
    from phoneuse import mcp_server

    calls = []

    def invoke(command, arguments, artifact, job, *, timeout):
        calls.append(command.arguments(arguments))
        stdout = "0.5 0.4  Restaurant" if command.cli == "text" else ""
        return SimpleNamespace(returncode=0, stdout=stdout, stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [
        {"name": "type_text", "arguments": {"text": "pepperoni pizza"}},
        {"name": "press_key", "arguments": {"combo": "return"}},
        {"name": "scroll_by", "arguments": {"dy": 0.5}},
        {"name": "look"},
    ]})
    assert calls == [["type", "pepperoni pizza"], ["key", "return"], ["scroll-by", "0.5"], ["text"]]
    assert not result["isError"]
    assert result["structuredContent"] == {
        "status": "completed",
        "steps": [{"name": "type_text", "status": "ok", "input_sent": True},
                  {"name": "press_key", "status": "ok", "input_sent": True},
                  {"name": "scroll_by", "status": "ok", "input_sent": True},
                  {"name": "look", "status": "ok"}],
        "result": {"text": "0.5 0.4  Restaurant"},
    }


@pytest.mark.parametrize("input_sent", [False, True, None])
def test_run_steps_stops_on_structured_failure_even_with_zero_exit(monkeypatch, input_sent):
    from phoneuse import mcp_server

    failure = {"status": "input_error", "stage": "tap", "input_sent": input_sent}
    replies = iter([("open", "ok"), ("tap-on", json.dumps(failure)), ("text", "0.5 0.2  Allow Location?")])

    def invoke(command, *args, **kwargs):
        expected, stdout = next(replies)
        assert command.cli == expected
        return SimpleNamespace(returncode=0, stdout=stdout, stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [
        {"name": "open_app", "arguments": {"app": "Settings"}},
        {"name": "tap_on", "arguments": {"label": "General"}}, {"name": "look"},
    ]})
    assert result["isError"]
    assert result["structuredContent"] == {
        "status": "stopped", "stopped_at": 2,
        "steps": [{"name": "open_app", "status": "ok", "input_sent": True},
                  {"name": "tap_on", "status": "input_error", "input_sent": input_sent}],
        "result": failure,
        "screen": "0.5 0.2  Allow Location?",
    }
    assert next(replies, None) is None


@pytest.mark.parametrize("later_steps", [
    [{"name": "run_steps"}],
    [{"name": "request_unlock"}],
    [{"name": "open_app", "arguments": {"app": "--force"}}],
    [{"name": "tap_on", "arguments": {"label": "General", "timeout": 0}}],
    [{"name": "tap_on", "arguments": {"label": "General", "region": [0.8, 0, 0.3, 1]}}],
    [{"name": "scroll_by", "arguments": {"dy": 2}}],
    [{"name": "type_text", "arguments": {"text": "pizza\0"}}],
    [{"name": "press_key", "arguments": {"combo": "invalid+return"}}],
    [{"name": "inspect_region", "arguments": {"left": 0.8, "top": 0, "right": 0.3, "bottom": 1}}],
    [{"name": "look"}, {"name": "look"}],
])
def test_run_steps_validates_later_steps_before_acting(monkeypatch, later_steps):
    from phoneuse import mcp_server

    def invoke(*args, **kwargs):
        pytest.fail("Invalid batch must not invoke any commands")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [
        {"name": "open_app", "arguments": {"app": "Settings"}}, *later_steps,
    ]})
    assert result["isError"]
    assert result["content"][0]["text"].startswith("Step 2:")


def test_run_steps_preserves_final_image(monkeypatch):
    from phoneuse import mcp_server

    def invoke(command, arguments, artifact, job, *, timeout):
        assert command.cli == "inspect-region"
        artifact.write_bytes(b"png")
        return SimpleNamespace(returncode=0, stdout='{"status":"captured","lines":[]}', stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [{
        "name": "inspect_region", "arguments": {"left": 0, "top": 0, "right": 0.5, "bottom": 0.5},
    }]})
    assert not result["isError"]
    assert result["structuredContent"]["result"] == {"status": "captured", "lines": []}
    assert result["content"][1] == {"type": "image", "mimeType": "image/png", "data": "cG5n"}


def test_run_steps_stops_when_shared_deadline_expires(monkeypatch):
    from phoneuse import mcp_server

    ticks = iter([0, 20, 90])
    monkeypatch.setattr(mcp_server, "time", SimpleNamespace(monotonic=lambda: next(ticks)))

    def invoke(command, arguments, artifact, job, *, timeout):
        assert command.cli == "open" and timeout == 70
        return SimpleNamespace(returncode=0, stdout="ok", stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [
        {"name": "open_app", "arguments": {"app": "Settings"}}, {"name": "look"},
    ]})
    assert result["isError"]
    assert result["structuredContent"]["stopped_at"] == 2
    assert result["structuredContent"]["result"] == {"status": "deadline_exceeded", "input_sent": False}
    assert "screen" not in result["structuredContent"]


def test_run_steps_accepts_six_steps_with_navigation_tools(monkeypatch):
    from phoneuse import mcp_server

    calls = []

    def invoke(command, arguments, artifact, job, *, timeout):
        calls.append(command.cli)
        stdout = '{"status":"found","matches":[],"scrolls":2,"input_sent":true}' if command.cli == "find-in-scroll" else ""
        return SimpleNamespace(returncode=0, stdout=stdout, stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [
        {"name": "back"}, {"name": "navigate", "arguments": {"name": "home"}},
        {"name": "open_app", "arguments": {"app": "Settings"}},
        {"name": "find_in_scroll", "arguments": {"label": "Privacy"}},
        {"name": "tap_on", "arguments": {"label": "Privacy"}}, {"name": "press_key", "arguments": {"combo": "esc"}},
    ]})
    assert calls == ["back", "recipe", "open", "find-in-scroll", "tap-on", "text"]
    # tap-on returned no JSON here, so the run stops at step 5 and reads the screen once.
    assert result["structuredContent"]["stopped_at"] == 5
    assert [s["input_sent"] for s in result["structuredContent"]["steps"][:4]] == [True, True, True, True]


def test_run_steps_refuses_seven_steps(monkeypatch):
    from phoneuse import mcp_server

    monkeypatch.setattr(mcp_server, "invoke", lambda *a, **k: pytest.fail("must not invoke"))
    result = mcp_server.call_tool("run_steps", {"steps": [{"name": "back"}] * 7})
    assert result["isError"] and result["content"][0]["text"] == "Supply 1 to 6 steps"


def test_run_steps_completed_run_reads_the_screen_only_when_asked(monkeypatch):
    from phoneuse import mcp_server

    calls = []

    def invoke(command, arguments, artifact, job, *, timeout):
        calls.append(command.cli)
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [{"name": "back"}]})
    assert calls == ["back"]
    assert result["structuredContent"]["status"] == "completed"
    assert "screen" not in result["structuredContent"]


@pytest.mark.parametrize("ticks, look_fails", [([0, 0, 87], False), ([0, 0, 0], True)])
def test_run_steps_omits_screen_when_short_of_time_or_look_fails(monkeypatch, ticks, look_fails):
    from phoneuse import mcp_server

    clock = iter(ticks)
    monkeypatch.setattr(mcp_server, "time", SimpleNamespace(monotonic=lambda: next(clock)))
    calls = []

    def invoke(command, arguments, artifact, job, *, timeout):
        calls.append(command.cli)
        if command.cli == "wait-for":
            return SimpleNamespace(returncode=1, stdout='{"status":"timeout"}', stderr="")
        return SimpleNamespace(returncode=1, stdout="", stderr="mirror not running")

    monkeypatch.setattr(mcp_server, "invoke", invoke)
    result = mcp_server.call_tool("run_steps", {"steps": [{"name": "wait_for", "arguments": {"label": "Done"}}]})
    assert calls == (["wait-for", "text"] if look_fails else ["wait-for"])
    assert result["structuredContent"]["status"] == "stopped"
    assert "screen" not in result["structuredContent"]


def test_optional_positionals_append_only_when_given():
    tools = commands.TOOLS
    assert tools["long_press"].arguments({"x": 0.1, "y": 0.2}) == ["longpress", "0.1", "0.2"]
    assert tools["long_press"].arguments({"x": 0.1, "y": 0.2, "hold": 1.5}) == ["longpress", "0.1", "0.2", "1.5"]
    values = {"x0": 0.1, "y0": 0.2, "x1": 0.3, "y1": 0.4, "duration": 0.5}
    assert tools["swipe"].arguments(values) == ["swipe", "0.1", "0.2", "0.3", "0.4", "0.5"]


def test_optional_is_not_required_in_schema():
    assert "hold" not in commands.TOOLS["long_press"].schema()["required"]


def test_navigate_accepts_only_builtin_gestures():
    from phoneuse import mcp_server
    command = commands.TOOLS["navigate"]
    mcp_server.validate(command, {"name": "switcher"})
    for name in ("list", "save"):
        with pytest.raises(ValueError):
            mcp_server.validate(command, {"name": name})


def test_press_key_rejects_unknown_modifiers():
    from phoneuse import mcp_server
    command = commands.TOOLS["press_key"]
    for combo in ("cmd+a", "return", "cmd+shift+z"):
        mcp_server.validate(command, {"combo": combo})
    for combo in ("hyper+a", "--force", "cmd+", "Return"):
        with pytest.raises(ValueError):
            mcp_server.validate(command, {"combo": combo})


def test_swipe_rejects_out_of_range():
    from phoneuse import mcp_server
    command = commands.TOOLS["swipe"]
    base = {"x0": 0.1, "y0": 0.2, "x1": 0.3, "y1": 0.4}
    for bad in ({"duration": 10}, {"x0": 1.5}):
        with pytest.raises(ValueError):
            mcp_server.validate(command, {**base, **bad})
