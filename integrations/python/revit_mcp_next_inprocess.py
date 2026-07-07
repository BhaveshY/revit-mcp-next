from __future__ import print_function

import json
import os
import uuid

try:
    STRING_TYPES = (basestring,)  # noqa: F821
except NameError:
    STRING_TYPES = (str,)


BRIDGE_PROTOCOL_VERSION = "2026-06-23"
DEFAULT_READ_TIMEOUT_MS = 30000
DEFAULT_WRITE_TIMEOUT_MS = 60000


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


def execute_operation(uiapp, operation, payload=None, operation_kind="read", document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    request = make_request(
        operation,
        payload=payload,
        operation_kind=operation_kind,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
    )
    return require_ok(execute(uiapp, request, addin_path=addin_path), operation)


def _payload_with(payload=None, values=None):
    result = dict(payload or {})
    for key, value in (values or {}).items():
        if value is not None:
            result[key] = value
    return result


def execute_read(uiapp, operation, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_operation(
        uiapp,
        operation,
        payload=payload,
        operation_kind="read",
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def list_documents(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "list_documents",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_levels(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_levels",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_views(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_views",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_sheets(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_sheets",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_schedules(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_schedules",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_schedule_fields(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_schedule_fields",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_current_view(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_current_view",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_current_view_elements(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_current_view_elements",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_selection(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_selection",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def analyze_model(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "analyze_model",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_model_readiness(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_model_readiness",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_model_context(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_model_context",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_material_quantities(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_material_quantities",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_warnings(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_warnings",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def get_rooms(uiapp, payload=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "get_rooms",
        payload=payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def query(uiapp, payload=None, query_filter=None, fields=None, preset=None, limit=None, cursor=None, include_total_count=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "query",
        payload=_payload_with(
            payload,
            {
                "filter": query_filter,
                "fields": fields,
                "preset": preset,
                "limit": limit,
                "cursor": cursor,
                "includeTotalCount": include_total_count,
            },
        ),
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def describe_parameters(uiapp, payload=None, parameter_filter=None, fields=None, preset=None, limit=None, cursor=None, include_total_count=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "describe_parameters",
        payload=_payload_with(
            payload,
            {
                "filter": parameter_filter,
                "fields": fields,
                "preset": preset,
                "limit": limit,
                "cursor": cursor,
                "includeTotalCount": include_total_count,
            },
        ),
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def catalog(uiapp, kind=None, payload=None, catalog_filter=None, fields=None, preset=None, limit=None, cursor=None, include_total_count=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS, addin_path=None):
    return execute_read(
        uiapp,
        "catalog",
        payload=_payload_with(
            payload,
            {
                "kind": kind,
                "filter": catalog_filter,
                "fields": fields,
                "preset": preset,
                "limit": limit,
                "cursor": cursor,
                "includeTotalCount": include_total_count,
            },
        ),
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def preview_change_set(uiapp, change_set, document_fingerprint=None, expected_generation=None, addin_path=None, timeout_ms=DEFAULT_READ_TIMEOUT_MS):
    return execute_operation(
        uiapp,
        "preview_change_set",
        payload=change_set,
        operation_kind="preview",
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def apply_change_set(uiapp, change_set, document_fingerprint=None, expected_generation=None, addin_path=None, timeout_ms=DEFAULT_WRITE_TIMEOUT_MS):
    return execute_operation(
        uiapp,
        "apply_change_set",
        payload=change_set,
        operation_kind="write",
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        timeout_ms=timeout_ms,
        addin_path=addin_path,
    )


def apply_preview(uiapp, change_set, preview, addin_path=None, document_fingerprint=None, expected_generation=None, timeout_ms=DEFAULT_WRITE_TIMEOUT_MS):
    apply_payload = dict(change_set)
    apply_payload["previewId"] = preview.get("previewId")
    apply_payload["confirm"] = True
    for key in ("changeSetHash", "baseGeneration", "expiresAt"):
        if preview.get(key) is not None:
            apply_payload[key] = preview.get(key)
    return apply_change_set(
        uiapp,
        apply_payload,
        document_fingerprint=document_fingerprint,
        expected_generation=expected_generation,
        addin_path=addin_path,
        timeout_ms=timeout_ms,
    )
