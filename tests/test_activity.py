from phoneuse.activity import ActivityLog
from phoneuse.agent_session import AgentSession


def test_keeps_only_the_newest():
    log = ActivityLog(maxlen=3)
    for i in range(5):
        log.add("action", str(i))
    assert [e["text"] for e in log.entries()] == ["2", "3", "4"]


def test_after_returns_only_newer():
    log = ActivityLog()
    for i in range(5):
        log.add("action", str(i))
    assert [e["id"] for e in log.entries(after=3)] == [4, 5]
    assert log.entries(after=5) == []


def test_clear_keeps_ids_increasing():
    log = ActivityLog()
    log.add("action", "a")
    log.add("action", "b")
    log.clear()
    assert log.entries() == []
    assert log.add("action", "c")["id"] == 3
    assert log.entries(after=2)[0]["text"] == "c"


def test_text_is_truncated():
    log = ActivityLog()
    e = log.add("action", "x" * 500, "y" * 500)
    assert len(e["text"]) == 120 and len(e["why"]) == 120


def test_listener_records_transitions():
    log = ActivityLog()
    session = AgentSession()

    def listener(s, old, why):
        if old != s.state:
            log.add("session", f"{old} -> {s.state}", why)

    session.listeners.append(listener)
    session.act("Tapping x")
    session.done()
    assert [e["text"] for e in log.entries()] == ["idle -> driving", "driving -> idle"]
