"""Connect Claude Code to the Phone Use MCP server, skill and CLI.

Windows clients connect to the MCP server started by WinUI. Other platforms use
the CLI's stdio MCP transport. The skill and CLI point at this copy of Phone Use:
  * the skill is a link: to a checkout's skill/ folder (or the Mac app's), or inside the
    Flatpak to the installed app's skill through Flatpak's "active" path, which follows
    updates;
  * ~/.local/bin/phoneuse runs this copy's CLI (inside the Flatpak: `flatpak run`).
A Flatpak pointed at a checkout (scripts/dev-link.sh) runs the checkout's code, so it
links the checkout's skill, and edits there reach agents at once.

state() says which pieces are in place; connect() and disconnect() set them up or remove them.
refresh() redoes connect() for someone who connected before (the app runs it at start),
so older setups catch up.
Nothing else in ~/.claude or ~/.claude.json changes.

On Windows the skill is a directory junction (a symlink needs Developer Mode or admin),
and the command is two files in ~/.local/bin, where Claude Code's own claude.exe is:
phoneuse (sh, for Git Bash, which Claude Code's Bash tool runs) and phoneuse.cmd (for
PowerShell and cmd), both running this copy's CLI with this copy's Python.
"""
import json
import os
import shlex
import shutil
import sys

from .mcp_server import MCP_PORT

HOME = os.path.expanduser("~")
SKILL_DIR = os.path.join(HOME, ".claude", "skills", "phoneuse")
CLAUDE_JSON = os.path.join(HOME, ".claude.json")
BIN = os.path.join(HOME, ".local", "bin", "phoneuse")
PKG = os.path.dirname(os.path.realpath(__file__))
FLATPAK_ID = os.environ.get("FLATPAK_ID")
MARK = "# Phone Use"  # the first comment line of the command we write, so we only ever remove our own
WINDOWS = sys.platform == "win32"
BIN_CMD = BIN + ".cmd"  # Windows: the same command for PowerShell and cmd


def _flatpak_files():
    """The installed app's files as the host sees them, through the "active" link that
    follows updates (/.flatpak-info's app-path names one version)."""
    try:
        with open("/.flatpak-info") as f:
            path = next(line.split("=", 1)[1].strip() for line in f if line.startswith("app-path="))
    except (OSError, StopIteration):
        return None
    parts = path.rstrip("/").split("/")  # .../app/ID/ARCH/BRANCH/COMMIT/files
    if len(parts) < 3 or parts[-1] != "files":
        return None
    return "/".join(parts[:-2] + ["active", "files"])


def _skill_source():
    """Where the skill link points, as the host sees it, or None."""
    here = os.path.join(os.path.dirname(PKG), "skill")
    if not PKG.startswith("/app/") and os.path.exists(os.path.join(here, "SKILL.md")):
        return here  # a checkout or the Mac app's bundle: the same path outside
    if FLATPAK_ID and os.path.exists("/app/share/phoneuse/skill/SKILL.md"):
        files = _flatpak_files()
        return os.path.join(files, "share", "phoneuse", "skill") if files else None
    return None


def _fallback_python():
    """The Python the launchers use when this copy has no working .venv: the bundled one if
    there is one, else the one running now. The .venv itself is chosen when the launcher
    runs, not when it's written: an update that set up core\\.venv after the app had already
    rewritten the launchers left phoneuse.cmd on a core\\python that was gone (2026-11-24)."""
    bundled = os.path.join(os.path.dirname(PKG), "python", "python.exe")
    return bundled if os.path.isfile(bundled) else sys.executable


def _cli_script():
    if WINDOWS:  # Git Bash: Windows paths with forward slashes work there
        root = os.path.dirname(PKG).replace("\\", "/")
        fallback = _fallback_python().replace("\\", "/")
        return (f"#!/bin/sh\n{MARK}: lets agents (and you) run phoneuse commands.\n"
                f"root={shlex.quote(root)}\n"
                f"py=\"$root/.venv/Scripts/python.exe\"\n"
                f"[ -f \"$root/.venv/pyvenv.cfg\" ] && [ -f \"$py\" ] || py={shlex.quote(fallback)}\n"
                f"export PYTHONUTF8=1\nexec \"$py\" \"$root/bin/phoneuse\" \"$@\"\n")
    target = (f"flatpak run --command=phoneuse {FLATPAK_ID}" if FLATPAK_ID
              else shlex.quote(os.path.join(os.path.dirname(PKG), "bin", "phoneuse")))  # "Phone Use.app"
    return f"#!/bin/sh\n{MARK}: lets agents (and you) run phoneuse commands.\nexec {target} \"$@\"\n"


def _cli_cmd():
    """Windows: phoneuse.cmd, the same command for PowerShell and cmd."""
    # goto, not a ( ) block: an argument with a ")" in it would end the block early.
    root = os.path.dirname(PKG)
    return (f"@echo off\r\nrem {MARK}: lets agents (and you) run phoneuse commands.\r\n"
            f"setlocal\r\nset PYTHONUTF8=1\r\nset \"PHONEUSE_ROOT={root}\"\r\n"
            f"set \"PHONEUSE_PY={_fallback_python()}\"\r\n"
            "if not exist \"%PHONEUSE_ROOT%\\.venv\\pyvenv.cfg\" goto run\r\n"
            "if exist \"%PHONEUSE_ROOT%\\.venv\\Scripts\\python.exe\" "
            "set \"PHONEUSE_PY=%PHONEUSE_ROOT%\\.venv\\Scripts\\python.exe\"\r\n"
            ":run\r\n\"%PHONEUSE_PY%\" \"%PHONEUSE_ROOT%\\bin\\phoneuse\" %*\r\n")


def _is_link(path):
    """A symlink, or on Windows a directory junction (os.path.islink says no to those)."""
    if os.path.islink(path):
        return True
    if WINDOWS and os.path.isdir(path):
        try:
            os.readlink(path)  # reads a junction's target too
            return True
        except (OSError, ValueError):
            return False
    return False


def _link_target(path):
    try:
        return os.path.normcase(os.path.normpath(os.readlink(path).removeprefix("\\\\?\\")))
    except (OSError, ValueError):
        return None


def _read_json():
    try:
        with open(CLAUDE_JSON) as f:
            return json.load(f)
    except FileNotFoundError:
        return {}


def _write_json(d):
    # In place: inside the Flatpak only this one file is visible, not its directory, so a
    # temporary file beside it can't be made. The text is made first, so a failure leaves
    # the old file as it was.
    text = json.dumps(d, indent=2)
    with open(CLAUDE_JSON, "w") as f:
        f.write(text)


def _ours(path):
    try:
        with open(path) as f:
            return MARK in f.read()
    except OSError:
        return False


def state():
    """{"skill", "command", "mcp"}: each True when that piece is in place and points at this copy
    of Phone Use, and "claude": whether Claude Code looks installed (~/.claude exists)."""
    # Compare the link's text: inside the Flatpak its target isn't visible.
    skill_ok = skill_linked(SKILL_DIR)
    try:
        with open(BIN, newline="") as f:
            cmd_ok = f.read() == _cli_script()
        if WINDOWS:
            with open(BIN_CMD, newline="") as f:
                cmd_ok = cmd_ok and f.read() == _cli_cmd()
    except OSError:
        cmd_ok = False
    mcp_ok = (_read_json().get("mcpServers") or {}).get("phoneuse") == _mcp_config()
    return {"claude": os.path.isdir(os.path.join(HOME, ".claude")), "skill": skill_ok, "command": cmd_ok,
            "mcp": mcp_ok}


def connected():
    s = state()
    return s["skill"] and s["command"] and s["mcp"]


def skill_linked(dest):
    """Whether dest is a link to this copy's skill folder."""
    src = _skill_source()
    if WINDOWS:
        return bool(src) and _is_link(dest) and _link_target(dest) == os.path.normcase(os.path.normpath(src))
    return bool(src) and os.path.islink(dest) and os.readlink(dest) == src


def unlink_skill(dest, only_links=False):
    """Remove dest. With only_links, a real folder there (someone's own skill) is left alone."""
    if WINDOWS and _is_link(dest) and os.path.isdir(dest):
        os.rmdir(dest)  # removes the junction only, never what it points at
    elif os.path.islink(dest) or (os.path.isfile(dest) and not only_links):
        os.remove(dest)
    elif os.path.isdir(dest) and not only_links:
        shutil.rmtree(dest)


def link_skill(dest, only_links=False):
    """Point dest at this copy's skill folder. Returns False when a real folder is in the way."""
    src = _skill_source()
    if not src:
        raise RuntimeError("the skill isn't in this copy of Phone Use")
    unlink_skill(dest, only_links)
    if os.path.lexists(dest):
        return False
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    if WINDOWS:
        import _winapi
        _winapi.CreateJunction(src, dest)
    else:
        os.symlink(src, dest)
    return True


def _remove_skill():
    unlink_skill(SKILL_DIR)


def _mcp_config():
    if WINDOWS:
        return {"type": "http", "url": f"http://127.0.0.1:{MCP_PORT}/mcp"}
    return {"type": "stdio", "command": BIN, "args": ["mcp"]}


def _configure_mcp(on):
    d = _read_json()
    servers = d.get("mcpServers") or {}
    old = servers.get("phoneuse")
    if on and old != _mcp_config():
        servers["phoneuse"] = _mcp_config()
    elif not on and old == _mcp_config():
        del servers["phoneuse"]
    else:
        return
    d["mcpServers"] = servers
    _write_json(d)


def connect():
    link_skill(SKILL_DIR)
    os.makedirs(os.path.dirname(BIN), exist_ok=True)
    for path, text in [(BIN, _cli_script())] + ([(BIN_CMD, _cli_cmd())] if WINDOWS else []):
        if os.path.islink(path) or os.path.exists(path):
            os.remove(path)
        with open(path, "w", newline="") as f:
            f.write(text)
    os.chmod(BIN, 0o755)
    _configure_mcp(True)


def refresh():
    """Repair connections made through Phone Use when a piece is missing or has moved."""
    try:
        if (_ours(BIN) or os.path.lexists(SKILL_DIR)) and not connected():
            connect()
            return True
    except (OSError, RuntimeError, ValueError):
        pass
    return False


def disconnect():
    _remove_skill()
    _configure_mcp(False)
    for path in (BIN, BIN_CMD):
        if _ours(path):
            os.remove(path)
