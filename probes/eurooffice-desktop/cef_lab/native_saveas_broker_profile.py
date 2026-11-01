#!/usr/bin/env python3
"""GD31J27m: strict DECLARATIVE external sandbox policy; no OS changes.

This validates an administrator's intended security boundary. It does not
instantiate or attest a namespace, syscall allowlist, user account or VM.
A passing profile ALWAYS returns blocked pending independently observed
Linux/Astra sandbox evidence. No privileged launcher is implemented here.
"""
from __future__ import annotations

import re

SCHEMA = "getdoc-gd31j27m-external-sandbox-profile-lab/1"
REVISION = re.compile(r"[0-9a-f]{40}\Z", re.ASCII)
ROLES = ["source-readonly", "scratch-dir-readonly", "final-dir-readonly"]


class ProfileError(ValueError):
    pass


def draft_profile(*, revision: str, broker_uid: int, worker_uid: int) -> dict:
    return {
        "schema": SCHEMA,
        "revision": revision,
        "brokerUid": broker_uid,
        "workerUid": worker_uid,
        "launcher": "external-administrator-controlled",
        "kernel": {
            "distinctUserIdentity": True,
            "mountNamespaceDistinct": True,
            "networkNamespaceDistinct": True,
            "networkDisabled": True,
            "corporateMountsAbsent": True,
            "privilegedSecretsAbsent": True,
            "noNewPrivileges": True,
            "seccompFilter": True,
            "effectiveCapabilitiesEmpty": True,
            "ptraceDenied": True,
            "inheritedDescriptorsClosed": True,
        },
        "transport": {
            "family": "unix",
            "socketType": "seqpacket",
            "socketFd": 3,
            "singleMessageOnly": True,
            "fdRoles": list(ROLES),
            "allowPathParameters": False,
        },
        "filesystem": {
            "onlySyntheticFixtureNames": True,
            "scratchMode": "0700",
            "outputMode": "0700",
            "sourceRole": "readonly-fd",
            "publishOperation": "disabled",
            "allowCorporateStorage": False,
        },
    }


def _exact(value, fields):
    if type(value) is not dict or set(value) != set(fields):
        raise ProfileError("Sandbox policy schema mismatch")


def inspect_profile(value: dict) -> dict:
    """Validate only intended settings; ALWAYS deny launch and publishing."""
    if type(value) is not dict:
        raise ProfileError("External sandbox policy not an object")
    _exact(value, ("schema", "revision", "brokerUid", "workerUid",
                   "launcher", "kernel", "transport", "filesystem"))
    if (value["schema"] != SCHEMA or
            type(value["revision"]) is not str or
            not REVISION.fullmatch(value["revision"]) or
            type(value["brokerUid"]) is not int or value["brokerUid"] <= 0 or
            type(value["workerUid"]) is not int or value["workerUid"] <= 0 or
            value["brokerUid"] == value["workerUid"] or
            value["launcher"] != "external-administrator-controlled"):
        raise ProfileError("External broker identity or revision rejected")

    kernel = value["kernel"]
    _exact(kernel, ("distinctUserIdentity", "mountNamespaceDistinct",
                    "networkNamespaceDistinct", "networkDisabled",
                    "corporateMountsAbsent", "privilegedSecretsAbsent",
                    "noNewPrivileges", "seccompFilter",
                    "effectiveCapabilitiesEmpty", "ptraceDenied",
                    "inheritedDescriptorsClosed"))
    if any(x is not True for x in kernel.values()):
        raise ProfileError("Sandbox policy cannot relax kernel isolation")

    transport = value["transport"]
    _exact(transport, ("family", "socketType", "socketFd", "singleMessageOnly",
                       "fdRoles", "allowPathParameters"))
    if (transport["family"] != "unix" or transport["socketType"] != "seqpacket" or
            type(transport["socketFd"]) is not int or
            transport["socketFd"] != 3 or
            transport["singleMessageOnly"] is not True or
            transport["fdRoles"] != ROLES or
            transport["allowPathParameters"] is not False):
        raise ProfileError("Unexpected worker IPC capability")

    fs = value["filesystem"]
    _exact(fs, ("onlySyntheticFixtureNames", "scratchMode", "outputMode",
                "sourceRole", "publishOperation", "allowCorporateStorage"))
    if (fs["onlySyntheticFixtureNames"] is not True or
            fs["scratchMode"] != "0700" or fs["outputMode"] != "0700" or
            fs["sourceRole"] != "readonly-fd" or
            fs["publishOperation"] != "disabled" or
            fs["allowCorporateStorage"] is not False):
        raise ProfileError("Unapproved publication or storage policy")

    return {
        "schema": "getdoc-gd31j27m-policy-check-denied-lab/1",
        "revision": value["revision"],
        "state": "blocked",
        "nextGate": "external-operator-required",
        "brokerLaunchAuthorized": False,
        "kernelIsolationAttested": False,
        "nativePublicationAuthorized": False,
        "releaseReady": False,
        "approvedForProduction": False,
        "laboratoryOnly": True,
    }
