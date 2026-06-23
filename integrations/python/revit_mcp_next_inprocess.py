from __future__ import print_function

import json
import os
import uuid

try:
    STRING_TYPES = (basestring,)  # noqa: F821
except NameError:
    STRING_TYPES = (str,)


BRIDGE_PROTOCOL_VERSION = "2026-06-23"


def default_addin_path():
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is not set.")
    return os.path.join(local_app_data, "RevitMcpNext", "addin", "RevitMcpNext.Addin.dll")


def load_bridge(addin_path=None):
    import clr

    path = addin_path or default_addin_path()
    if os.path.exists(path):
        try:
            clr.AddReferenceToFileAndPath(path)
        except AttributeError:
            clr.AddReference(path)
    else:
        clr.AddReference("RevitMcpNext.Addin")

    from RevitMcpNext.Addin import RevitMcpInProcessBridge

    return RevitMcpInProcessBridge


def status(uiapp, addin_path=None):
    bridge = load_bridge(addin_path)
    return json.loads(str(bridge.StatusJson(uiapp)))


def execute(uiapp, request, addin_path=None):
    bridge = load_bridge(addin_path)
    if not isinstance(request, STRING_TYPES):
        request = json.dumps(request, separators=(",", ":"))
    return json.loads(str(bridge.ExecuteJson(uiapp, request)))


def make_request(operation, payload=None, operation_kind="read", document_fingerprint=None, expected_generation=None, timeout_ms=30000):
    request = {
        "protocolVersion": BRIDGE_PROTOCOL_VERSION,
        "requestId": uuid.uuid4().hex,
        "sessionId": "in-process-python",
        "operation": operation,
        "operationKind": operation_kind,
        "timeoutMs": timeout_ms,
        "payload": payload or {},
    }
    if document_fingerprint:
        request["documentFingerprint"] = document_fingerprint
    if expected_generation is not None:
        request["expectedGeneration"] = expected_generation
    return request


def require_ok(response, operation):
    if response.get("ok"):
        return response.get("data") or {}

    error = response.get("error") or {}
    code = error.get("code") or "IN_PROCESS_OPERATION_FAILED"
    message = error.get("message") or ("Revit MCP Next operation failed: " + operation)
    suggested = error.get("suggestedNextAction")
    if suggested:
        message = message + "\nSuggested next action: " + suggested
    raise RuntimeError(code + ": " + message)


def execute_operation(uiapp, operation, payload=None, operation_kind="read", document_fingerprint=None, expected_generation=None, timeout_ms=30000, addin_path=None):
    request = make_request(
        operation,
        payload=payload,
        operation_kind=operation_kind,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
    )
    return require_ok(execute(uiapp, request, addin_path=addin_path), operation)


def preview_change_set(uiapp, change_set, document_fingerprint=None, expected_generation=None, addin_path=None):
    return execute_operation(
        uiapp,
        "preview_change_set",
        payload=change_set,
        operation_kind="preview",
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=30000,
        addin_path=addin_path,
    )


def apply_change_set(uiapp, change_set, document_fingerprint=None, expected_generation=None, addin_path=None):
    return execute_operation(
        uiapp,
        "apply_change_set",
        payload=change_set,
        operation_kind="write",
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=60000,
        addin_path=addin_path,
    )


def apply_preview(uiapp, change_set, preview, addin_path=None):
    apply_payload = dict(change_set)
    apply_payload["previewId"] = preview.get("previewId")
    apply_payload["confirm"] = True
    for key in ("changeSetHash", "baseGeneration", "expiresAt"):
        if preview.get(key) is not None:
            apply_payload[key] = preview.get(key)
    return apply_change_set(uiapp, apply_payload, addin_path=addin_path)
