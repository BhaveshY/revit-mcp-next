from __future__ import print_function

import datetime
import json
import os
import traceback

from revit_mcp_next_inprocess import apply_preview, preview_change_set, status


def utc_now():
    return datetime.datetime.utcnow().replace(microsecond=0).isoformat() + "Z"


def ensure_directory(path):
    directory = os.path.dirname(path)
    if directory and not os.path.isdir(directory):
        os.makedirs(directory)


def default_evidence_path(host_name):
    root = os.environ.get("REVIT_MCP_NEXT_INSTALL_ROOT")
    if not root:
        auth_config = os.environ.get("REVIT_MCP_NEXT_AUTH_CONFIG")
        if auth_config:
            root = os.path.dirname(os.path.dirname(auth_config))
    if not root:
        local_app_data = os.environ.get("LOCALAPPDATA") or os.environ.get("TEMP") or os.getcwd()
        root = os.path.join(local_app_data, "RevitMcpNext")

    stamp = datetime.datetime.utcnow().strftime("%Y%m%d-%H%M%S")
    return os.path.join(root, "logs", "host-smoke", "{0}-{1}.json".format(host_name, stamp))


def format_blocked_preview(preview):
    changes = preview.get("changes") or []
    messages = []
    for change in changes:
        message = change.get("message") or change.get("status") or "blocked"
        messages.append("{0}: {1}".format(change.get("type") or "change", message))
    return "\n".join(messages) or "Preview was not ready."


def get_created_element_ids(apply_result):
    ids = []
    for change in apply_result.get("changes") or []:
        created = change.get("createdElementIds") or change.get("createdElementId")
        if isinstance(created, list):
            ids.extend([str(value) for value in created])
        elif created:
            ids.append(str(created))
        after = change.get("after") or {}
        element_id = after.get("elementId") or after.get("id")
        if element_id:
            ids.append(str(element_id))
    return ids


def format_bridge_error(response, operation):
    error = response.get("error") or {}
    code = error.get("code") or "IN_PROCESS_OPERATION_FAILED"
    message = error.get("message") or ("Revit MCP Next operation failed: " + operation)
    suggested = error.get("suggestedNextAction")
    if suggested:
        message = message + "\nSuggested next action: " + suggested
    return code + ": " + message


def require_configured_addin_handler(status_data, host_name):
    bridge_status = status_data.get("inProcessBridge") or {}
    if bridge_status.get("addinHandlerActive") is True:
        return bridge_status

    handler = bridge_status.get("handler") or "unknown"
    if not bridge_status:
        raise RuntimeError(
            "{0} host smoke could not prove the configured Revit MCP Next add-in handler is active. "
            "Update/reload the installed add-in so status reports inProcessBridge.addinHandlerActive=true.".format(host_name)
        )

    raise RuntimeError(
        "{0} host smoke reached the in-process bridge through '{1}', not the configured add-in handler. "
        "Confirm Revit loaded RevitMcpNext.addin from the installed package before collecting host evidence.".format(
            host_name,
            handler,
        )
    )


def ensure_active_document(uiapp, model_path=None):
    try:
        if uiapp.ActiveUIDocument is not None:
            return
    except Exception:
        pass

    if not model_path:
        return

    if not os.path.exists(model_path):
        raise RuntimeError("Configured host-smoke model path was not found: {0}".format(model_path))

    uiapp.OpenAndActivateDocument(model_path)


def build_change_set(active_document, host_name, stamp):
    return {
        "documentFingerprint": active_document.get("fingerprint"),
        "expectedGeneration": active_document.get("generation"),
        "transactionName": "{0} MCP host smoke {1}".format(host_name, stamp),
        "operations": [
            {
                "id": "{0}-host-smoke-create-level".format(host_name),
                "type": "create_level",
                "name": "{0} MCP Host Smoke {1}".format(host_name, stamp),
                "elevation": {"value": 9200, "unit": "mm", "system": "metric"},
            }
        ],
    }


def run_host_smoke(uiapp, host_name, evidence_path=None, apply_writes=True, addin_path=None, model_path=None, raise_on_failure=True):
    started = utc_now()
    evidence = {
        "schemaVersion": 1,
        "status": "failed",
        "host": host_name,
        "startedAtUtc": started,
        "completedAtUtc": None,
        "activeDocument": None,
        "inProcessBridge": None,
        "coveredTools": [
            "inprocess.status",
            "inprocess.preview_change_set",
            "inprocess.apply_change_set",
        ],
        "coveredOperations": ["create_level"],
        "applyWrites": bool(apply_writes),
        "previewReady": False,
        "createdElementIds": [],
        "preview": None,
        "apply": None,
        "error": None,
    }
    path = evidence_path or default_evidence_path(host_name)

    try:
        ensure_active_document(uiapp, model_path=model_path)
        status_response = status(uiapp, addin_path=addin_path)
        if not status_response.get("ok"):
            raise RuntimeError(format_bridge_error(status_response, "status"))

        status_data = status_response.get("data") or {}
        bridge_status = require_configured_addin_handler(status_data, host_name)
        evidence["inProcessBridge"] = bridge_status
        active_document = status_data.get("activeDocument") or {}
        evidence["activeDocument"] = active_document

        stamp = datetime.datetime.utcnow().strftime("%Y%m%d%H%M%S")
        change_set = build_change_set(active_document, host_name, stamp)
        preview = preview_change_set(uiapp, change_set, addin_path=addin_path)
        evidence["preview"] = preview
        evidence["previewReady"] = bool(preview.get("ready"))

        if not preview.get("ready"):
            raise RuntimeError(format_blocked_preview(preview))

        if apply_writes:
            apply_result = apply_preview(uiapp, change_set, preview, addin_path=addin_path)
            evidence["apply"] = apply_result
            evidence["createdElementIds"] = get_created_element_ids(apply_result)

        evidence["status"] = "passed"
    except Exception as error:
        evidence["error"] = {
            "message": str(error),
            "type": error.__class__.__name__,
            "traceback": traceback.format_exc(),
        }
        if raise_on_failure:
            raise
    finally:
        evidence["completedAtUtc"] = utc_now()
        evidence["evidencePath"] = path
        ensure_directory(path)
        with open(path, "w") as handle:
            json.dump(evidence, handle, indent=2, sort_keys=True)

    return evidence
