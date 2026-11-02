#!/usr/bin/env python3
"""GD31J27v: read-only broker-process lifetime guard based on Linux pidfd.

A pidfd is a kernel reference to one process, unlike a bare numeric PID.
This guard opens a pidfd between TWO independently pinned /proc starttime
checks and polls it without waiting or signalling; any exit/hangup means
fail closed. It cannot attest executable identity, root sandbox, seccomp
semantics, Astra MAC, mount contents or production SaveAs rights.

There is NO pidfd_getfd, ptrace, kill, subprocess, privilege change, opening
/proc/<pid>/fd, creation of new IPC channels or user document access here.
"""
from __future__ import annotations

import os
import select
import sys
if sys.platform.startswith("linux"):
    import fcntl
else:
    fcntl=None

import native_saveas_root_supervisor_audit as audit

SCHEMA="getdoc-gd31j27v-broker-pidfd-lifetime-guard-lab/1"


class PidfdError(ValueError):
    """Never put private process IDs or kernel details in messages."""


def _poll_still_running(fd):
    try:
        poll=select.poll()
        poll.register(fd,select.POLLIN|select.POLLHUP|
                        select.POLLERR|select.POLLNVAL)
        # pidfd polling readiness means exit, reaped target, or error.
        # ALL poll events, including unknown bits, must be a refusal.
        events=poll.poll(0)
    except (OSError,ValueError) as exc:
        raise PidfdError("Kernel pidfd exit state unavailable") from exc
    if events:
        raise PidfdError("Broker process exited or pidfd polling failed")


class PinnedBrokerPidfd:
    """Owns exactly one fd until caller exits or invokes close()."""
    def __init__(self,pid,expected_start_ticks,fd):
        self.pid=pid
        self.expected_start_ticks=expected_start_ticks
        self._fd=fd
        self.processIdentityAttested=False
        self.launchAuthorized=False
        self.releaseReady=False

    def fileno(self):
        if self._fd is None:
            raise PidfdError("Broker pidfd already closed")
        return self._fd

    def assert_live(self):
        fd=self.fileno()
        # The pidfd holds identity, while the starttime check additionally
        # binds it to operator-pinned process data from the original launch.
        try:
            observed=audit.read_broker_start_ticks(self.pid)
        except (audit.AuditError,OSError,ValueError) as exc:
            raise PidfdError("Broker process origin not independently visible") from exc
        if observed!=self.expected_start_ticks:
            raise PidfdError("Broker pidfd and pinned process lifetime diverged")
        _poll_still_running(fd)
        return False  # Never equate liveness with OS or release attestation.

    def close(self):
        if self._fd is not None:
            fd=self._fd
            self._fd=None
            try:
                os.close(fd)
            except OSError as exc:
                raise PidfdError("Kernel pidfd close failed") from exc

    def __enter__(self):
        try:
            self.assert_live()
        except BaseException:
            self.close()
            raise
        return self

    def __exit__(self,exc_type,exc,tb):
        self.close()
        return False


def pin_broker_process(pid,expected_start_ticks):
    """Open a verified pidfd. No /proc file-descriptor fallback allowed.

    pidfd_open requires a supporting Linux kernel and Python runtime. The
    first and second /proc starttime samples reject obvious PID recycling
    races; the pidfd itself prevents confusion after successful open.
    """
    if (not sys.platform.startswith("linux") or fcntl is None or
            not hasattr(os,"pidfd_open") or
            type(pid) is not int or not 1<=pid<2**31 or
            type(expected_start_ticks) is not int or expected_start_ticks<=0):
        raise PidfdError("Unsupported or unpinned broker process")
    try:
        initial=audit.read_broker_start_ticks(pid)
    except (audit.AuditError,OSError,ValueError) as exc:
        raise PidfdError("Broker original birth time inaccessible") from exc
    if initial!=expected_start_ticks:
        raise PidfdError("Broker PID reused before pidfd acquisition")
    fd=-1
    completed=False
    try:
        fd=os.pidfd_open(pid,0)
        if type(fd) is not int or fd<3:
            raise PidfdError("Broker pidfd unavailable or stdio overwritten")
        if not fcntl.fcntl(fd,fcntl.F_GETFD)&fcntl.FD_CLOEXEC:
            raise PidfdError("Broker pidfd may leak to child process")
        handle=PinnedBrokerPidfd(pid,expected_start_ticks,fd)
        handle.assert_live()
        completed=True
        return handle
    except (OSError,ValueError,AttributeError) as exc:
        if isinstance(exc,PidfdError):
            raise
        raise PidfdError("Cannot pin broker with Linux pidfd") from exc
    finally:
        if fd>=0 and not completed:
            # A pidfd must not survive a failed acquisition.
            try:
                os.close(fd)
            except OSError:
                pass


def pin_worker_process(pid,expected_start_ticks):
    """Reuse exact pidfd/birth-time checks for the distinct-UID worker.

    The shared audit.read_broker_start_ticks only reads Linux /proc/<pid>/stat;
    it does not grant broker rights or imply that cross-UID /proc is readable.
    There is no fallback to weaker checks when the worker is inaccessible.
    """
    return pin_broker_process(pid,expected_start_ticks)
