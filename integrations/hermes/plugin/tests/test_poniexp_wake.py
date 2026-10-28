"""Given/When/Then tests for the poniexp-wake Hermes plugin, against a fake poniexp-client (no daemon, no Hermes)."""

from __future__ import annotations

import importlib.util
import json
import os
import sys
import time
from pathlib import Path

import pytest

PLUGIN = Path(__file__).resolve().parents[1] / "poniexp-wake" / "__init__.py"

FAKE_CLIENT = r'''#!/usr/bin/env python3
import json, os, sys, time
log = os.environ["FAKE_LOG"]
with open(log, "a") as f:
    f.write(json.dumps(sys.argv[1:]) + "\n")
if sys.argv[1:2] == ["status"]:
    print("approved as wojtek (5f667cdc), connected to https://router/")
elif sys.argv[1:2] == ["listen"]:
    with open(os.environ["FAKE_LINES"]) as f:
        for line in f:
            print(line.rstrip("\n"), flush=True)
    time.sleep(60)
'''


def load_plugin():
    spec = importlib.util.spec_from_file_location("poniexp_wake_under_test", PLUGIN)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FakeContext:
    def __init__(self):
        self.hooks = {}
        self.injected = []

    def register_hook(self, name, callback):
        self.hooks[name] = callback

    def inject_message(self, content, role="user", *, session_key=None, origin=None):
        self.injected.append((session_key, content))
        return True


def message(id_, sender="ise/ops", body="secret body", direction="in"):
    return json.dumps({"id": id_, "from": sender, "to": "wojtek/x", "direction": direction, "body": body})


def wait_for(condition, seconds=10.0):
    until = time.time() + seconds
    while time.time() < until:
        if condition():
            return True
        time.sleep(0.05)
    return condition()


@pytest.fixture
def fake(tmp_path, monkeypatch):
    client = tmp_path / "poniexp-client"
    client.write_text(FAKE_CLIENT)
    client.chmod(0o755)
    log, lines = tmp_path / "log.jsonl", tmp_path / "lines.txt"
    lines.write_text("")
    monkeypatch.setenv("PONIEXP_CLIENT", str(client))
    monkeypatch.setenv("FAKE_LOG", str(log))
    monkeypatch.setenv("FAKE_LINES", str(lines))
    monkeypatch.delenv("PONIEXP_BASE_AGENT", raising=False)
    plugin = load_plugin()
    plugin.GATHER_SECONDS = 0.3
    ctx = FakeContext()
    plugin.register(ctx)

    class Fake:
        pass

    f = Fake()
    f.plugin, f.ctx, f.lines = plugin, ctx, lines
    f.calls = lambda: [json.loads(l) for l in log.read_text().splitlines()] if log.exists() else []
    yield f
    plugin._stop_all()


def test_Given_ClaudeAndHermesSessionIds_When_NamedHere_Then_TheyMatchTheCSharpRule():
    # Given
    plugin = load_plugin()

    # When
    claude = plugin.session_agent("claude", "8B1D44E2-7c0f-4f6e-9a51-2d3c4b5a6978")
    gateway = plugin.session_agent("hermes-default", "20261028_085157_8fcb67c3")
    tui = plugin.session_agent("hermes-default", "local-3f5ede2fe0fc1ef7ca799f5d5bde67907a09df35252cca684ac70fc4746a98d9")
    short = plugin.session_agent("hermes-default", "20261028_133348_ab12cd")
    word = plugin.session_agent("hermes-default", "local")

    # Then
    assert claude == "claude.8b1d44e2"
    assert gateway == "hermes-default.8fcb67c3"
    assert tui == "hermes-default.3f5ede2f"
    assert short == "hermes-default.ab12cd"
    assert word == "hermes-default.local"


def test_Given_ATuiSession_When_ItStarts_Then_ItListensAsItsOwnSessionAgent(fake):
    # Given
    session = "local-3f5ede2fe0fc1ef7ca799f5d5bde67907a09df35252cca684ac70fc4746a98d9"

    # When
    fake.ctx.hooks["on_session_start"](session_id=session, platform="tui")

    # Then
    assert wait_for(lambda: ["listen", "--agent", "hermes-default.3f5ede2f", "--json"] in fake.calls())


def test_Given_AGatewaySession_When_ItStarts_Then_NothingListens(fake):
    # Given
    session = "20261028_133348_ab12cd"

    # When
    fake.ctx.hooks["on_session_start"](session_id=session, platform="telegram")
    time.sleep(0.5)

    # Then
    assert not any(call[:1] == ["listen"] for call in fake.calls())
    assert fake.plugin._listeners == {}


def test_Given_ABurstOfTwoMessages_When_TheyArrive_Then_TheSessionGetsOneNudgeWithoutTheBodies(fake):
    # Given
    fake.lines.write_text(message("a1", "ise/ops") + "\n" + message("b2", "blinky/bot") + "\n")

    # When
    fake.ctx.hooks["on_session_start"](session_id="s1", platform="cli")

    # Then
    assert wait_for(lambda: len(fake.ctx.injected) == 1)
    time.sleep(0.5)
    assert len(fake.ctx.injected) == 1
    key, text = fake.ctx.injected[0]
    assert key == "s1"
    assert "2 new messages for wojtek/hermes-default.s1 (this session) from ise/ops, blinky/bot" in text
    assert 'agent "hermes-default.s1"' in text
    assert "secret" not in text


def test_Given_AMessageDeliveredTwice_When_TheStreamRedeliversIt_Then_TheSessionIsNudgedOnce(fake):
    # Given
    fake.lines.write_text(message("a1") + "\n" + message("a1") + "\n")

    # When
    fake.ctx.hooks["on_session_start"](session_id="s1", platform="tui")

    # Then
    assert wait_for(lambda: len(fake.ctx.injected) == 1)
    time.sleep(0.5)
    assert len(fake.ctx.injected) == 1
    assert "1 new message for" in fake.ctx.injected[0][1]


def test_Given_RoomEventsAndOutgoingCopies_When_TheyArrive_Then_NothingIsInjected(fake):
    # Given
    room = json.dumps({"kind": "room-message", "message": {"id": "r1", "direction": "in"}})
    fake.lines.write_text(room + "\n" + message("o1", direction="out") + "\nnot json\n")

    # When
    fake.ctx.hooks["on_session_start"](session_id="s1", platform="tui")
    assert wait_for(lambda: any(call[:1] == ["listen"] for call in fake.calls()))
    time.sleep(1.0)

    # Then
    assert fake.ctx.injected == []


def test_Given_AFirstTurn_When_TheLlmIsCalled_Then_TheSessionIsToldItsAddress(fake):
    # Given
    hook = fake.ctx.hooks["pre_llm_call"]

    # When
    first = hook(session_id="s1", platform="tui", is_first_turn=True)
    later = hook(session_id="s1", platform="tui", is_first_turn=False)

    # Then
    assert "this session's own address is wojtek/hermes-default.s1" in first["context"]
    assert later is None


def test_Given_AListeningSession_When_ItIsFinalized_Then_ItsListenerStopsAndItsAgentIsRetired(fake):
    # Given
    fake.ctx.hooks["on_session_start"](session_id="s1", platform="tui")
    listener = fake.plugin._listeners["s1"]
    assert wait_for(lambda: listener.running)

    # When
    fake.ctx.hooks["on_session_finalize"](session_id="s1")

    # Then
    assert wait_for(lambda: not listener.running)
    assert ["agent", "retire", "hermes-default.s1"] in fake.calls()
    assert "s1" not in fake.plugin._listeners


def test_Given_ASessionAlreadyListening_When_ItsHooksFireAgain_Then_OnlyOneListenerRuns(fake):
    # Given
    fake.ctx.hooks["on_session_start"](session_id="s1", platform="tui")

    # When
    fake.ctx.hooks["pre_llm_call"](session_id="s1", platform="tui", is_first_turn=True)
    fake.ctx.hooks["on_session_start"](session_id="s1", platform="tui")
    time.sleep(0.5)

    # Then
    assert sum(1 for call in fake.calls() if call[:1] == ["listen"]) == 1


def test_Given_AGatewayRouteKeyBoundInTheHook_When_AMessageArrives_Then_TheNudgeIsInjectedUnderThatKey(fake, monkeypatch):
    # Given
    monkeypatch.setattr(fake.plugin, "_bound_route_key", lambda: "agent:main:local:dm:s1")
    fake.lines.write_text(message("a1") + "\n")

    # When
    fake.ctx.hooks["on_session_start"](session_id="s1", platform="tui")

    # Then
    assert wait_for(lambda: len(fake.ctx.injected) == 1)
    assert fake.ctx.injected[0][0] == "agent:main:local:dm:s1"
