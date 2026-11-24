"""Claude Code and Codex setup on Windows: launchers that survive updates, the skill links,
the service token, and the Claude app's private copy of the runtime folder. No phone needed."""
import os
import shutil
import subprocess
import sys

import pytest

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="Windows setup")

from phoneuse import claude_setup, cli, codex_setup, ipc  # noqa: E402

PRINT_PYTHON = "import sys\nprint(sys.prefix)\nprint(sys.argv[1:])\n"


def fake_copy(tmp_path, monkeypatch):
    root = tmp_path / "Phone Use" / "core"
    (root / "bin").mkdir(parents=True)
    (root / "bin" / "phoneuse").write_text(PRINT_PYTHON)
    (root / "phoneuse").mkdir()
    (root / "skill").mkdir()
    (root / "skill" / "SKILL.md").write_text("---\nname: phoneuse\n---\n")
    monkeypatch.setattr(claude_setup, "PKG", str(root / "phoneuse"))
    return root


def run_cmd(text, tmp_path, *args):
    launcher = tmp_path / "phoneuse.cmd"
    launcher.write_text(text, newline="")
    out = subprocess.run(["cmd", "/c", str(launcher), *args], capture_output=True, text=True, timeout=60)
    assert out.returncode == 0, out.stderr
    return out.stdout.splitlines()


def test_launcher_picks_the_venv_when_it_runs_not_when_written(tmp_path, monkeypatch):
    root = fake_copy(tmp_path, monkeypatch)
    text = claude_setup._cli_cmd()  # written before the update makes .venv
    prefix, args = run_cmd(text, tmp_path, "tap-on", "Done (2)")
    assert os.path.normcase(prefix) != os.path.normcase(str(root / ".venv"))
    assert args == "['tap-on', 'Done (2)']"

    # The update then makes core\.venv: the same launcher now runs it.
    here = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), ".venv")
    if not os.path.isfile(os.path.join(here, "pyvenv.cfg")):
        pytest.skip("needs the checkout's .venv to copy")
    (root / ".venv" / "Scripts").mkdir(parents=True)
    shutil.copy(os.path.join(here, "pyvenv.cfg"), root / ".venv")
    shutil.copy(os.path.join(here, "Scripts", "python.exe"), root / ".venv" / "Scripts")
    assert claude_setup._cli_cmd() == text
    prefix, _ = run_cmd(text, tmp_path)
    assert os.path.normcase(prefix) == os.path.normcase(str(root / ".venv"))


def test_sh_launcher_checks_for_the_venv_at_run_time(tmp_path, monkeypatch):
    fake_copy(tmp_path, monkeypatch)
    text = claude_setup._cli_script()
    assert '.venv/pyvenv.cfg' in text and 'exec "$py" "$root/bin/phoneuse" "$@"' in text


def test_codex_skill_link_follows_this_copy_and_spares_a_real_folder(tmp_path, monkeypatch):
    root = fake_copy(tmp_path, monkeypatch)
    dest = tmp_path / "home" / ".agents" / "skills" / "phoneuse"
    monkeypatch.setattr(codex_setup, "SKILL_DIR", str(dest))
    assert codex_setup._link_skill(True)
    assert (dest / "SKILL.md").exists() and claude_setup.skill_linked(str(dest))
    assert not codex_setup._link_skill(False)
    assert not dest.exists() and (root / "skill" / "SKILL.md").exists()  # the target is untouched

    dest.mkdir()
    (dest / "SKILL.md").write_text("the user's own")
    assert not codex_setup._link_skill(True)
    assert (dest / "SKILL.md").read_text() == "the user's own"


def test_clients_never_create_the_service_token(tmp_path, monkeypatch):
    monkeypatch.setattr(ipc, "TOKEN_FILE", str(tmp_path / "token"))
    monkeypatch.setattr(ipc, "_token", None)
    with pytest.raises(RuntimeError, match="isn't running"):
        ipc.token()
    assert not (tmp_path / "token").exists()
    made = ipc.token(create=True)
    assert len(made) == 32 and ipc.token() == made


def test_stale_private_runtime_copies_are_removed(tmp_path, monkeypatch):
    from phoneuse.windows import packaged
    local = tmp_path / "Local"
    stale = local / "Packages" / "Claude_x" / "LocalCache" / "Local" / "phoneuse"
    (stale / "run").mkdir(parents=True)
    (stale / "run" / "token").write_text("old")
    (stale / "PhoneUseMirror.json").write_text("{}")
    monkeypatch.setenv("LOCALAPPDATA", str(local))
    monkeypatch.setattr(packaged, "is_packaged", lambda: False)
    assert packaged.clear_shadow_runtime() == [str(stale / "run")]
    assert not (stale / "run").exists() and (stale / "PhoneUseMirror.json").exists()

    (stale / "run").mkdir()
    monkeypatch.setattr(packaged, "is_packaged", lambda: True)
    assert packaged.clear_shadow_runtime() == [] and (stale / "run").exists()


@pytest.mark.parametrize("argv, on", [([], None), (["connect"], True), (["disconnect"], False)])
def test_codex_command_sets_the_registration_like_settings(monkeypatch, capsys, argv, on):
    calls = []
    monkeypatch.setattr(codex_setup, "configure", lambda value: calls.append(value) or
                        {"connected": bool(on), "available": True, "skill": bool(on)})
    monkeypatch.setattr(cli, "can_reach_services", lambda: pytest.fail("needs no running service"))
    monkeypatch.setattr(sys, "argv", ["phoneuse", "codex", *argv])
    cli.main()
    assert calls == [on]
    out = capsys.readouterr().out
    assert ("MCP registered" if on else "MCP not registered") in out and codex_setup.SKILL_DIR in out


@pytest.mark.parametrize("state, message", [
    ({"connected": False, "available": False}, "Codex isn't installed"),
    ({"error": "Codex already has a different MCP server named phoneuse."}, "different MCP server"),
])
def test_codex_command_reports_what_stops_it(monkeypatch, capsys, state, message):
    monkeypatch.setattr(codex_setup, "configure", lambda value: state)
    monkeypatch.setattr(sys, "argv", ["phoneuse", "codex", "connect"])
    with pytest.raises(SystemExit, match="1"):
        cli.main()
    assert message in capsys.readouterr().err


def test_codex_command_usage(monkeypatch):
    monkeypatch.setattr(sys, "argv", ["phoneuse", "codex", "link"])
    with pytest.raises(SystemExit, match="1"):
        cli.main()
