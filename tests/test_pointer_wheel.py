"""The wheel's backlog (pointer.Pointer.scroll) follows the hand: capped at a screen each way,
and turning the other way starts from there. (No phone: the tick that drains it is stopped.)"""
from phoneuse import pointer, touch


def stopped_pointer(monkeypatch):
    monkeypatch.setattr(touch, "pt", lambda: (402.0, 874.0))
    p = pointer.Pointer(hid=lambda: None, log=lambda *a: None)
    p.stopped = True  # the tick never drains anything
    p.hover(0.5, 0.5)
    return p


def test_a_fast_flick_queues_at_most_a_screen(monkeypatch):
    p = stopped_pointer(monkeypatch)
    for _ in range(20):  # 20 notches of 125 pt: 2500 pt asked for
        p.scroll(0, 125)
    assert p.wheel == [0.0, 874.0]
    for _ in range(10):
        p.scroll(-125, 0)
    assert p.wheel[0] == -402.0


def test_turning_back_starts_from_the_new_way(monkeypatch):
    p = stopped_pointer(monkeypatch)
    for _ in range(4):
        p.scroll(0, 125)
    p.scroll(0, -125)  # one notch the other way: up at once, not 375 pt still going down first
    assert p.wheel == [0.0, -125.0]


def test_the_same_way_adds_up(monkeypatch):
    p = stopped_pointer(monkeypatch)
    p.scroll(0, 100)
    p.scroll(0, 100)
    assert p.wheel == [0.0, 200.0]
