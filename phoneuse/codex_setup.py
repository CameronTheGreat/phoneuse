"""Register WinUI's loopback MCP server in the user's Codex configuration, and link the
Phone Use skill where Codex reads personal skills ($HOME/.agents/skills, which Codex
follows through links)."""

import json
import os
from pathlib import Path
import subprocess
import threading

from . import claude_setup
from .mcp_server import MCP_PORT

MCP_URL = f"http://127.0.0.1:{MCP_PORT}/mcp"
SKILL_DIR = os.path.join(os.path.expanduser("~"), ".agents", "skills", "phoneuse")
_lock = threading.Lock()


def _link_skill(on):
    """Link or unlink the skill. A real folder of the same name is the user's own and stays."""
    try:
        if on:
            claude_setup.link_skill(SKILL_DIR, only_links=True)
        else:
            claude_setup.unlink_skill(SKILL_DIR, only_links=True)
    except (OSError, RuntimeError):
        pass  # the MCP tools still work without the skill
    return claude_setup.skill_linked(SKILL_DIR)


def _codex_cli():
    local = os.environ.get("LOCALAPPDATA")
    folders = [Path(local) / "Programs/OpenAI/Codex/bin"] if local else []
    folders.extend(Path(p.strip('"')) for p in os.get_exec_path())
    for folder in folders:
        if folder.is_absolute():
            for name in ("codex.exe", "codex.cmd"):
                candidate = folder / name
                if candidate.is_file():
                    return candidate
    return None


def _run(cli, *args):
    try:
        result = subprocess.run(
            [str(cli), "mcp", *args], cwd=Path.home(), stdin=subprocess.DEVNULL,
            capture_output=True, encoding="utf-8", errors="replace", timeout=10,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError("Codex took too long to respond. Try again.") from exc
    except OSError as exc:
        raise RuntimeError("Phone Use couldn't start the Codex CLI.") from exc
    if result.returncode:
        action = {"list": "read", "add": "add", "remove": "remove"}[args[0]]
        raise RuntimeError(f"Codex couldn't {action} the Phone Use MCP registration. Check your Codex configuration.")
    return result.stdout


def _state(cli):
    try:
        servers = json.loads(_run(cli, "list", "--json"))
        entry = next((server for server in servers if server["name"] == "phoneuse"), None)
        if entry is not None:
            transport = entry["transport"]
            if transport.get("type") != "streamable_http" or transport.get("url") not in (
                MCP_URL, f"http://localhost:{MCP_PORT}/mcp",
            ):
                return {"error": "Codex already has a different MCP server named phoneuse. Rename it in Codex to connect."}
            if not entry["enabled"]:
                return {"error": "Phone Use is disabled in Codex. Enable it in Codex to connect."}
    except (AttributeError, ValueError, KeyError, TypeError) as exc:
        raise RuntimeError("Phone Use couldn't read the Codex MCP server list.") from exc
    return {"connected": entry is not None, "available": True}


def configure(on=None):
    """Read the registration, or change it and return the resulting status."""
    with _lock:
        cli = _codex_cli()
        if cli is None:
            return {"connected": False, "available": False}

        state = _state(cli)
        if "error" in state:
            return state
        if on is not None and state["connected"] != on:
            if on:
                _run(cli, "add", "phoneuse", "--url", MCP_URL)
            else:
                _run(cli, "remove", "phoneuse")
            state = _state(cli)
            if "error" not in state and state["connected"] != on:
                raise RuntimeError("Codex didn't apply the Phone Use MCP change.")
        # Each read repairs the skill too, so it follows this copy after an update or a move.
        if on is not None or state["connected"] != claude_setup.skill_linked(SKILL_DIR):
            state["skill"] = _link_skill(state["connected"])
        else:
            state["skill"] = claude_setup.skill_linked(SKILL_DIR)
        return state


def refresh():
    """At start: when Codex has Phone Use registered, point its skill link at this copy."""
    try:
        return configure(None).get("skill")
    except RuntimeError:
        return None
