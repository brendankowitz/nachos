"""Smoke conformance: the unmodified honcho-ai SDK, used through its documented API, against a running Nachos."""

import time
import uuid

import pytest
from honcho import AuthenticationError, Honcho, ServerError, UnprocessableEntityError
from honcho.api_types import SessionPeerConfig

from conftest import MATCH_ALL


def test_workspace_get_or_create_and_metadata(honcho: Honcho):
    # The SDK creates the workspace lazily on the first call, so the first metadata read is the get-or-create.
    assert honcho.get_metadata() == {}
    honcho.set_metadata({"owner": "conformance", "n": 1})
    assert honcho.get_metadata() == {"owner": "conformance", "n": 1}
    honcho.set_metadata({"n": 2})
    assert honcho.get_metadata() == {"n": 2}


def test_peer_get_or_create_and_metadata(honcho: Honcho):
    created = honcho.peer("alice", metadata={"role": "user"})
    assert created.id == "alice"
    again = honcho.peer("alice")
    assert again.id == "alice"
    assert again.get_metadata() == {"role": "user"}
    again.set_metadata({"role": "admin", "n": 3})
    assert honcho.peer("alice").get_metadata() == {"role": "admin", "n": 3}


def test_session_get_or_create_and_metadata(honcho: Honcho):
    created = honcho.session("s1", metadata={"topic": "intro"})
    assert created.id == "s1"
    again = honcho.session("s1")
    assert again.get_metadata() == {"topic": "intro"}
    again.set_metadata({"topic": "budget"})
    assert honcho.session("s1").get_metadata() == {"topic": "budget"}


def test_list_auto_paginates_over_25_sessions(honcho: Honcho):
    expected = {f"s{n:02d}" for n in range(25)}
    for session_id in expected:
        honcho.session(session_id, metadata={"batch": "b1"})

    page = honcho.sessions(filters={"metadata": {"batch": "b1"}}, size=10)
    assert (page.total, page.pages, len(page.items)) == (25, 3, 10)
    # Iterating the page follows the remaining pages on its own.
    assert {s.id for s in page} == expected

    assert honcho.workspaces(filters=MATCH_ALL).total >= 1


# MISMATCH M1: honcho-ai 2.5.1 sends `POST .../list` with Content-Length 0 when no filters are given. Honcho accepts that;
# Nachos answers 422 json_invalid ("Invalid JSON body."). The assertion states the expected behaviour; strict, so the
# suite goes red (XPASS) the day Nachos accepts an empty list body, and this marker must then be removed. `raises` keeps a
# dead server or any other error from being counted as the expected mismatch.
@pytest.mark.xfail(strict=True, raises=UnprocessableEntityError, reason="M1: Nachos answers 422 json_invalid to the empty body the SDK sends for an unfiltered list")
def test_list_without_filters(honcho: Honcho):
    honcho.session("s1")
    assert [s.id for s in honcho.sessions()] == ["s1"]


def test_session_peers_add_set_remove_and_peer_config(honcho: Honcho):
    alice, bob, carol = honcho.peer("alice"), honcho.peer("bob"), honcho.peer("carol")
    session = honcho.session("s1")

    def members() -> set[str]:
        return {p.id for p in session.peers()}

    session.add_peers([alice, bob])
    assert members() == {"alice", "bob"}
    session.set_peers([bob, carol])
    assert members() == {"bob", "carol"}
    session.remove_peers([bob])
    assert members() == {"carol"}

    # Peer-level configuration.
    assert alice.get_configuration().observe_me is None
    alice.set_configuration({"observe_me": False})
    assert alice.get_configuration().observe_me is False

    # Session-level peer configuration.
    session.add_peers([(alice, SessionPeerConfig(observe_others=True))])
    assert session.get_peer_configuration(alice).observe_others is True
    session.set_peer_configuration(alice, SessionPeerConfig(observe_me=False))
    assert session.get_peer_configuration(alice).observe_me is False


def test_batch_messages_list_get_and_update(honcho: Honcho):
    alice = honcho.peer("alice")
    session = honcho.session("s1")
    session.add_peers([alice])

    created = session.add_messages([alice.message(f"message {n}", metadata={"n": n}) for n in range(20)])
    assert [m.content for m in created] == [f"message {n}" for n in range(20)]
    assert len({m.id for m in created}) == 20

    listed = list(session.messages(filters=MATCH_ALL, size=7))
    assert {m.id for m in listed} == {m.id for m in created}

    fetched = session.get_message(created[3].id)
    assert (fetched.id, fetched.content, fetched.peer_id) == (created[3].id, "message 3", "alice")

    session.update_message(created[3].id, {"edited": True})
    assert session.get_message(created[3].id).metadata == {"edited": True}


def test_idempotent_replay_returns_same_batch(nachos, workspace_id: str):
    # honcho-ai takes extra request headers per client (`default_headers`), so a dedicated client replays one batch
    # under one Idempotency-Key. Spec 9.1: the replay returns the stored response and performs no second mutation.
    replaying = nachos.client(workspace_id, timeout=10, default_headers={"Idempotency-Key": f"py-{uuid.uuid4()}"})
    alice = replaying.peer("alice")
    session = replaying.session("s1")
    session.add_peers([alice])

    batch = [alice.message(f"replayed {n}") for n in range(3)]
    first = session.add_messages(batch)
    second = session.add_messages(batch)

    assert [m.id for m in second] == [m.id for m in first]
    assert len(list(session.messages(filters=MATCH_ALL))) == 3

    # Contrast: without the header the same batch is stored twice (spec 9.1: such requests behave exactly like Honcho),
    # so the assertions above are not satisfied by a server that merely ignores repeated batches.
    plain = nachos.client(workspace_id, timeout=10)
    plain_alice = plain.peer("alice")
    plain_session = plain.session("s2")
    plain_session.add_peers([plain_alice])
    unkeyed = [plain_alice.message(f"unkeyed {n}") for n in range(3)]
    unkeyed_first = plain_session.add_messages(unkeyed)
    unkeyed_second = plain_session.add_messages(unkeyed)
    assert {m.id for m in unkeyed_second}.isdisjoint(m.id for m in unkeyed_first)
    assert len(list(plain_session.messages(filters=MATCH_ALL))) == 6


def test_unimplemented_call_surfaces_501_promptly(nachos, workspace_id: str):
    client = nachos.client(workspace_id, timeout=3)
    alice = client.peer("alice")

    started = time.monotonic()
    with pytest.raises(ServerError) as failure:
        alice.chat("What do you know about me?")
    assert failure.value.status == 501
    assert time.monotonic() - started < 3


def test_peer_scoped_key_reads_members_but_cannot_list_workspaces(nachos):
    if nachos.auth_mode != "enforced":
        pytest.skip("awaiting auth/Task 11 publication")

    workspace, peer_id = nachos.auth_workspace, nachos.auth_peer
    suffix = uuid.uuid4().hex[:8]
    admin = nachos.client(workspace, timeout=10)
    member = admin.peer(peer_id)
    session = admin.session(f"py-auth-{suffix}")
    session.add_peers([member])
    session.add_messages([member.message("visible to the member")])

    scoped = nachos.client(workspace, api_key=nachos.peer_key, timeout=10)
    scoped_session = scoped.session(session.id)
    assert [m.content for m in scoped_session.messages(filters=MATCH_ALL)] == ["visible to the member"]

    with pytest.raises(AuthenticationError) as denied:
        list(scoped.workspaces(filters=MATCH_ALL))
    assert denied.value.status == 401
