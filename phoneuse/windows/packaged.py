"""Stale private copies of the runtime folder made by packaged (MSIX) apps.

A process inside an MSIX package, such as the Claude desktop app and the Claude Code it starts,
has its writes under %LOCALAPPDATA% redirected to
%LOCALAPPDATA%\\Packages\\<family>\\LocalCache\\Local, and from then on it reads that private copy in
place of the real file. Once phoneuse running there wrote into %LOCALAPPDATA%\\phoneuse\\run (the
token on first use, a pid file, a log), its view of that folder stopped following the real one. After
an update the services had a new token and new pid files, and every command from the Claude app
failed with "authentication rejected or stale service" until the copy was renamed by hand
(2026-11-24: the copy held a token from 2026-11-15).

clear_shadow_runtime() removes those copies when the unpackaged app starts. Reads from the package
then fall through to the real folder again. Only run\\ is removed: it holds nothing but runtime state
that the services write again. ipc.token() no longer lets a client create the token, so the copy
doesn't come back with a token of its own.
"""
import ctypes
import os
import shutil

APPMODEL_ERROR_NO_PACKAGE = 15700


def is_packaged():
    """Whether this process runs with a package identity (its own writes would be redirected)."""
    length = ctypes.c_uint32(0)
    try:
        result = ctypes.windll.kernel32.GetCurrentPackageFullName(ctypes.byref(length), None)
    except (AttributeError, OSError):
        return False
    return result != APPMODEL_ERROR_NO_PACKAGE


def clear_shadow_runtime():
    """Remove every package's private copy of %LOCALAPPDATA%\\phoneuse\\run. Returns those removed."""
    local = os.environ.get("LOCALAPPDATA")
    if not local or is_packaged():
        return []  # inside a package these paths are our own view: leave them
    packages = os.path.join(local, "Packages")
    removed = []
    try:
        families = os.listdir(packages)
    except OSError:
        return []
    for family in families:
        run = os.path.join(packages, family, "LocalCache", "Local", "phoneuse", "run")
        if os.path.isdir(run) and not os.path.islink(run):
            shutil.rmtree(run, ignore_errors=True)
            if not os.path.exists(run):
                removed.append(run)
    return removed
