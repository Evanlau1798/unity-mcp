"""Offline ownership order/reload/identity contracts; never contact a Unity Editor."""
import pytest

from transport.managed_lifetime import ManagedServerLifetime


@pytest.fixture
def managed():
    states = {(101, 1000): True, (202, 2000): True}
    lifetime = ManagedServerLifetime("launch-token", 101, 1000,
                                     lambda pid, started: states.get((pid, started)))
    return lifetime, states


@pytest.mark.parametrize("order", [(101, 202), (202, 101)])
def test_last_editor_out_closes_server_in_both_orders(managed, order):
    lifetime, states = managed
    lifetime.remember_owner(202, 2000)
    assert not lifetime.can_exit()
    states[(order[0], order[0] // 101 * 1000)] = False
    assert not lifetime.can_exit(), "a live peer must keep the shared server"
    states[(order[1], order[1] // 101 * 1000)] = False
    assert lifetime.can_exit(), "last peer must clean up even when launcher quit first"


def test_reload_disconnect_and_reconnect_keep_process_ownership(managed):
    lifetime, states = managed
    lifetime.remember_owner(202, 2000)
    states[(101, 1000)] = False
    # WebSocket presence is deliberately not an ownership/liveness signal.
    assert not lifetime.can_exit()
    lifetime.remember_owner(202, 2000)
    assert len(lifetime.owners) == 2
    states[(202, 2000)] = False
    assert lifetime.can_exit()


@pytest.mark.parametrize("unknown", [None, (None, None), (0, 0)])
def test_unknown_or_unverifiable_owner_is_fail_safe(managed, unknown):
    lifetime, states = managed
    if unknown is None:
        states[(101, 1000)] = None
    else:
        lifetime.remember_owner(*unknown)
        states[(101, 1000)] = False
    assert not lifetime.can_exit()
    assert not lifetime.can_stop("launch-token", 101, 1000)


def test_manual_stop_requires_token_and_single_verified_owner(managed):
    lifetime, states = managed
    assert not lifetime.can_stop("wrong-token", 101, 1000)
    assert not lifetime.can_stop("launch-token", 999, 9000)
    assert lifetime.can_stop("launch-token", 101, 1000)
    lifetime.remember_owner(202, 2000)
    assert not lifetime.can_stop("launch-token", 101, 1000)
    states[(202, 2000)] = False
    assert lifetime.can_stop("launch-token", 101, 1000)


def test_pid_reuse_does_not_adopt_new_process(managed):
    lifetime, states = managed
    states[(101, 1000)] = False  # probe confirmed the old process identity is gone
    states[(101, 3000)] = True
    assert lifetime.can_exit()
    assert not lifetime.can_stop("launch-token", 101, 3000)


def test_separate_servers_do_not_share_owners(managed):
    lifetime, states = managed
    other = ManagedServerLifetime("other-token", 202, 2000,
                                  lambda pid, started: states.get((pid, started)))
    states[(101, 1000)] = False
    assert lifetime.can_exit()
    assert not other.can_exit()


def test_manual_stop_does_not_race_an_unregistered_connection(managed):
    lifetime, _ = managed
    lifetime.connections = 1  # requester disconnected; a new peer handshake has entered ASGI
    assert not lifetime.can_stop("launch-token", 101, 1000)


def test_non_ascii_token_is_rejected_without_throwing(managed):
    lifetime, _ = managed
    assert not lifetime.can_stop("非 ASCII", 101, 1000)
