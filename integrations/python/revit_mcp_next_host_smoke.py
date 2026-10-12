"""Host smoke for pyRevit and Dynamo: proves the in-process bridge works inside a running Revit.

run_host_smoke(uiapp, "pyrevit" | "dynamo") reads status, counts walls, lists levels, previews one level and (with
apply_writes) creates it, then writes a JSON evidence file (default <home>\\logs\\<host>-host-smoke-<time>.json).
Use a disposable model only: the default run adds a level named "<host> MCP Host Smoke <time>".
"""

from __future__ import print_function

import datetime
import json
import os
import traceback

from revit_mcp_next_inprocess import call, home_dir, status


def utc_now():
    return datetime.datetime.utcnow().replace(microsecond=0).isoformat() + "Z"


def default_evidence_path(host_name):
    stamp = datetime.datetime.utcnow().strftime("%Y%m%dT%H%M%SZ")
    return os.path.join(home_dir(), "logs", "{0}-host-smoke-{1}.json".format(host_name, stamp))


def _ensure_directory(path):
    parent = os.path.dirname(path)
    if parent and not os.path.exists(parent):
        os.makedirs(parent)


def _active_snapshot_doc(snapshot):
    active = snapshot.get("activeRid")
    for doc in snapshot.get("docs") or []:
        if doc.get("rid") == active:
            return doc
    return None


def ensure_active_document(uiapp, model_path=None):
    """Opens model_path (a disposable .rvt) when Revit has no active document."""
    if uiapp.ActiveUIDocument is not None:
        return uiapp.ActiveUIDocument.Document
    if not model_path:
        raise RuntimeError("No active Revit document. Open a disposable model or pass model_path.")
    if not os.path.exists(model_path):
        raise RuntimeError("Model not found: " + model_path)
    return uiapp.OpenAndActivateDocument(model_path).Document


def run_host_smoke(uiapp, host_name, evidence_path=None, apply_writes=True, addin_path=None, model_path=None, raise_on_failure=True):
    evidence = {
        "schemaVersion": 2,
        "status": "failed",
        "host": host_name,
        "startedAtUtc": utc_now(),
        "completedAtUtc": None,
        "addin": None,
        "activeDocument": None,
        "coveredKeys": [],
        "applyWrites": bool(apply_writes),
        "previewOk": False,
        "createdElementIds": [],
        "results": {},
        "error": None,
    }
    path = evidence_path or default_evidence_path(host_name)
    try:
        ensure_active_document(uiapp, model_path)

        st = status(uiapp, addin_path=addin_path)
        if not st.get("ok"):
            raise RuntimeError("in-process status failed: {0} {1}".format(st.get("code"), st.get("message")))
        data = st.get("data") or {}
        hello = data.get("hello") or {}
        snapshot = data.get("snapshot") or {}
        evidence["addin"] = dict((k, hello.get(k)) for k in ("addinVersion", "gitSha", "payloadId", "year", "instanceId"))
        evidence["activeDocument"] = _active_snapshot_doc(snapshot)

        walls = call(uiapp, "find_elements", {"category": ["Walls"], "count_only": True}, addin_path=addin_path)
        evidence["results"]["find_elements"] = walls.get("summary")
        levels = call(uiapp, "list.levels", {}, addin_path=addin_path)
        evidence["results"]["list.levels"] = levels.get("summary")
        evidence["coveredKeys"] += ["status", "find_elements", "list.levels"]

        stamp = datetime.datetime.utcnow().strftime("%Y%m%d%H%M%S")
        args = {"elevation": 9200, "name": "{0} MCP Host Smoke {1}".format(host_name, stamp), "plans": False}
        preview = call(uiapp, "create_elements.level", args, mode="preview", addin_path=addin_path)
        evidence["results"]["preview"] = preview.get("summary")
        evidence["previewOk"] = True
        evidence["coveredKeys"].append("create_elements.level (preview)")

        if apply_writes:
            applied = call(uiapp, "create_elements.level", args, mode="apply", addin_path=addin_path)
            evidence["results"]["apply"] = applied.get("summary")
            evidence["createdElementIds"] = list(((applied.get("changes") or {}).get("created")) or [])
            evidence["coveredKeys"].append("create_elements.level (apply)")
            if not evidence["createdElementIds"]:
                raise RuntimeError("create_elements.level reported no created element")

        evidence["status"] = "passed"
    except Exception as error:
        evidence["error"] = {"message": str(error), "type": error.__class__.__name__, "traceback": traceback.format_exc()}
    finally:
        evidence["completedAtUtc"] = utc_now()
        evidence["evidencePath"] = path
        _ensure_directory(path)
        with open(path, "w") as handle:
            json.dump(evidence, handle, indent=2, sort_keys=True)

    if evidence["status"] != "passed" and raise_on_failure:
        raise RuntimeError("{0} host smoke failed: {1} (evidence {2})".format(host_name, evidence["error"]["message"], path))
    return evidence
