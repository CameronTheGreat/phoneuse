"""The first-time setup checklist (overview.checklist) the Windows window shows.
    .venv\\Scripts\\python -m pytest tests\\test_setup_checklist.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from phoneuse import overview  # noqa: E402


def ids(items):
    return [i["id"] for i in items]


def test_a_new_pc_starts_at_mirroring_with_nothing_done():
    s = {"picture": {"live": False}, "bridge": {}, "paired": 0, "pc": "DESKTOP-1",
         "claude": {"claude": False}, "calibrated": None, "bluetooth": False}
    items = overview.checklist(s, "Phone Use")
    assert ids(items) == ["mirroring", "bluetooth", "pointer", "app"]
    assert not any(i["done"] for i in items)
    assert "DESKTOP-1" in items[1]["detail"] and items[1]["action"] == "setup_bridge"


def test_the_firewall_step_shows_only_until_a_phone_got_through():
    s = {"picture": {}, "bridge": {}, "firewall": "windows", "confirmed": False}
    assert ids(overview.checklist(s, "Phone Use"))[0] == "firewall"
    s["confirmed"] = True
    assert "firewall" not in ids(overview.checklist(s, "Phone Use"))


def test_steps_tick_off_and_claude_is_optional():
    s = {"picture": {"live": True}, "bridge": {}, "paired": 1, "calibrated": True, "bluetooth": True,
         "claude": {"claude": True, "skill": False, "command": False}}
    items = overview.checklist(s, "Phone Use")
    assert all(i["done"] for i in items if not i["optional"])
    claude = items[-1]
    assert claude["id"] == "claude" and claude["optional"] and not claude["done"]


def test_with_a_bridge_the_bluetooth_step_is_the_bridges():
    s = {"picture": {}, "bridge": {"saved": {"id": "x"}}, "bluetooth": False}
    step = next(i for i in overview.checklist(s, "Phone Use") if i["id"] == "bluetooth")
    assert "Phone Use Bridge" in step["detail"] and step["action"] is None


def test_this_pcs_bluetooth_counts_as_set_up_on_windows():
    from phoneuse import bluetooth_setup
    if sys.platform == "win32":
        assert bluetooth_setup.on()  # so hero() doesn't ask Windows users to set up a bridge
        s = {"picture": {"live": False}, "bridge": {}, "paired": 1, "pc_bluetooth": bluetooth_setup.on()}
        assert overview.hero(s, "Phone Use")["step"] == "start_mirroring"
