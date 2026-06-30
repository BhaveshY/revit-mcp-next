from __future__ import print_function

import json
import os
import uuid

try:
    STRING_TYPES = (basestring,)  # noqa: F821
except NameError:
    STRING_TYPES = (str,)


BRIDGE_PROTOCOL_VERSION = "2026-06-23"


def _candidate_install_roots():
    roots = []

    explicit_root = os.environ.get("REVIT_MCP_NEXT_INSTALL_ROOT")
    if explicit_root:
        roots.append(explicit_root)

    auth_config = os.environ.get("REVIT_MCP_NEXT_AUTH_CONFIG")
    if auth_config:
        roots.append(os.path.dirname(os.path.dirname(auth_config)))

    current = os.path.abspath(os.path.dirname(__file__))
    for _ in range(6):
        if os.path.basename(current).lower() == "revitmcpnext":
            roots.append(current)
            break
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        roots.append(os.path.join(local_app_data, "RevitMcpNext"))

    app_data = os.environ.get("APPDATA")
    if app_data:
        for year in ("2024",):
            roots.append(os.path.join(app_data, "Autodesk", "Revit", "Addins", year, "RevitMcpNext"))

    unique = []
    seen = set()
    for root in roots:
        if not root:
            continue
        normalized = os.path.normcase(os.path.abspath(root))
        if normalized in seen:
            continue
        seen.add(normalized)
        unique.append(root)
    return unique


def _candidate_addin_paths():
    paths = []
    for root in _candidate_install_roots():
        paths.append(os.path.join(root, "addin", "RevitMcpNext.Addin.dll"))
    return paths


def default_addin_path():
    candidates = _candidate_addin_paths()
    for path in candidates:
        if os.path.exists(path):
            return path
    if candidates:
        return candidates[0]
    raise RuntimeError("Unable to resolve a Revit MCP Next add-in path.")


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
