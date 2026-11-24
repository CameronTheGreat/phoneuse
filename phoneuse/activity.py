"""Recent agent activity, for the owner: what agents did on the phone, newest last.

Kept in memory only (MAX entries), never written to disk: it holds the labels an
agent tapped, which can be personal. It lasts as long as the web server."""
import itertools
import threading
import time
from collections import deque

MAX = 200
TEXT_MAX = 120


class ActivityLog:
    def __init__(self, maxlen=MAX):
        self._entries = deque(maxlen=maxlen)
        self._ids = itertools.count(1)
        self._lock = threading.Lock()  # on_session can run off the event loop

    def add(self, kind, text, why=""):
        """kind: "session" (a change of who controls the phone) or "action". Returns the entry."""
        with self._lock:
            entry = {"id": next(self._ids), "at": time.time(), "kind": kind,
                     "text": str(text)[:TEXT_MAX], "why": str(why)[:TEXT_MAX]}
            self._entries.append(entry)
            return dict(entry)

    def entries(self, after=0):
        """Entries with an id above `after`, oldest first, as a list of dicts."""
        with self._lock:
            return [dict(e) for e in self._entries if e["id"] > after]

    def clear(self):
        """Forget every entry. Ids keep counting up, so a client asking `after` never sees one again."""
        with self._lock:
            self._entries.clear()
