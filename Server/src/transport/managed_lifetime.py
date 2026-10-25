"""Lifetime of an explicitly Editor-managed, local HTTP server."""
from __future__ import annotations

from collections.abc import Callable
import asyncio
import ctypes
import os
import secrets
import sys

active_lifetime: ManagedServerLifetime | None = None


def process_is_alive(pid: int, started_ms: int) -> bool | None:
    """Unknown is never treated as dead. Windows creation time excludes PID reuse."""
    if pid <= 1 or started_ms <= 0:
        return None
    if sys.platform != "win32":
        try:
            os.kill(pid, 0)
            # POSIX PID reuse can conservatively retain this server until that PID exits.
            return True
        except ProcessLookupError:
            return False
        except OSError:
            return None
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000 | 0x100000, False, pid)
    if not handle:
        return False if ctypes.get_last_error() == 87 else None
    try:
        if kernel.WaitForSingleObject(handle, 0) == 0:
            return False
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)):
            return None
        created = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        return created // 10000 - 11644473600000 == started_ms
    finally:
        kernel.CloseHandle(handle)


class ManagedServerLifetime:
    def __init__(self, token: str, launcher_pid: int, launcher_started_ms: int,
                 probe: Callable[[int, int], bool | None] = process_is_alive):
        self.token = token
        self.owners = {(launcher_pid, launcher_started_ms)}
        self.probe = probe
        self.unknown_owner = False
        self.connections = 0

    @classmethod
    def from_environment(cls) -> ManagedServerLifetime | None:
        token = os.environ.get("UNITY_MCP_INSTANCE_TOKEN", "")
        try:
            pid = int(os.environ.get("UNITY_MCP_EDITOR_PID", "0"))
            started = int(os.environ.get("UNITY_MCP_EDITOR_STARTED_MS", "0"))
        except ValueError:
            return None
        # External servers, including legacy --unity-instance-token launches, are not adopted.
        if len(token) != 32 or any(c not in "0123456789abcdef" for c in token) or pid <= 1 or started <= 0:
            return None
        return cls(token, pid, started)

    def remember_owner(self, pid: int | None, started_ms: int | None) -> None:
        if type(pid) is not int or type(started_ms) is not int or pid <= 1 or started_ms <= 0:
            self.unknown_owner = True
        else:
            self.owners.add((pid, started_ms))

    def _alive(self, owner: tuple[int, int]) -> bool | None:
        try:
            return self.probe(*owner)
        except Exception:
            return None

    def can_exit(self) -> bool:
        return (not self.unknown_owner and self.connections == 0
                and all(self._alive(owner) is False for owner in self.owners))

    def can_stop(self, token: str, pid: int, started_ms: int) -> bool:
        requester = (pid, started_ms)
        return (isinstance(token, str) and token.isascii() and secrets.compare_digest(token, self.token)
                and not self.unknown_owner and self.connections == 0 and requester in self.owners
                and self._alive(requester) is True
                and all(owner == requester or self._alive(owner) is False for owner in self.owners))

    async def watch(self, shutdown: Callable[[], None], interval: float = 1.0) -> None:
        # Two quiet observations allow an in-progress connection handshake to enter the app.
        while True:
            await asyncio.sleep(interval)
            if self.can_exit():
                await asyncio.sleep(interval)
                if self.can_exit():
                    shutdown()
                    return
