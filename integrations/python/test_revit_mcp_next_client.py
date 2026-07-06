import os
import sys
import tempfile
import textwrap
import unittest

from revit_mcp_next_client import RevitMcpClient, RevitMcpToolError


FAKE_SERVER = r'''
import json
import sys

def send(message):
    sys.stdout.write(json.dumps(message, separators=(",", ":")) + "\n")
    sys.stdout.flush()

for line in sys.stdin:
    message = json.loads(line)
    if "id" not in message:
        continue
    request_id = message["id"]
    method = message.get("method")
    if method == "initialize":
        result = {
            "protocolVersion": message.get("params", {}).get("protocolVersion", "2025-11-25"),
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "fake-revit-mcp-next", "version": "test"}
        }
    elif method == "tools/list":
        result = {
            "tools": [
                {"name": "revit.status", "inputSchema": {"type": "object"}},
                {"name": "revit.get_levels", "inputSchema": {"type": "object"}},
                {"name": "revit.preview_change_set", "inputSchema": {"type": "object"}},
                {"name": "revit.apply_change_set", "inputSchema": {"type": "object"}}
            ]
        }
    elif method == "tools/call":
        params = message.get("params") or {}
        name = params.get("name")
        if name == "revit.status":
            result = {
                "content": [{"type": "text", "text": "Revit bridge connected."}],
                "structuredContent": {
                    "data": {
                        "connected": True,
                        "activeDocument": {"title": "Sample.rvt", "fingerprint": "doc-1", "generation": 1}
                    },
                    "warnings": [],
                    "metrics": {"elapsedMs": 1}
                }
            }
        elif name == "revit.get_levels":
            result = {
                "content": [{"type": "text", "text": "1 level returned."}],
                "structuredContent": {
                    "data": [{"id": "311", "name": "Level 1"}],
                    "warnings": [],
                    "metrics": {"elapsedMs": 1}
                }
            }
        elif name == "revit.preview_change_set":
            result = {
                "content": [{"type": "text", "text": "Preview ready."}],
                "structuredContent": {
                    "data": {
                        "ready": True,
                        "previewId": "preview-1",
                        "changeSetHash": "hash-1",
                        "baseGeneration": 1,
                        "expiresAt": "2026-01-01T00:00:00Z"
                    },
                    "warnings": [],
                    "metrics": {"elapsedMs": 1}
                }
            }
        elif name == "revit.apply_change_set":
            result = {
                "content": [{"type": "text", "text": "Applied."}],
                "structuredContent": {
                    "data": {"applied": True, "payload": params.get("arguments") or {}},
                    "warnings": [],
                    "metrics": {"elapsedMs": 1}
                }
            }
        else:
            result = {
                "isError": True,
                "content": [{"type": "text", "text": "Unknown tool"}]
            }
    else:
        send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32601, "message": method}})
        continue
    send({"jsonrpc": "2.0", "id": request_id, "result": result})
'''


class RecordingClient(RevitMcpClient):
    def __init__(self):
        RevitMcpClient.__init__(self, command=["unused"])
        self.calls = []

    def call_tool(self, name, arguments=None):
        self.calls.append((name, arguments))
        return {"toolName": name, "arguments": arguments}


class RevitMcpNextClientTests(unittest.TestCase):
    def setUp(self):
        handle = tempfile.NamedTemporaryFile("w", suffix=".py", delete=False)
        self.server_path = handle.name
        handle.write(textwrap.dedent(FAKE_SERVER))
        handle.close()

    def tearDown(self):
        try:
            os.remove(self.server_path)
        except OSError:
            pass

    def test_client_initializes_lists_tools_and_returns_structured_data(self):
        with RevitMcpClient(command=[sys.executable, self.server_path], timeout_seconds=5) as client:
            status = client.status()
            self.assertEqual(status["connected"], True)
            self.assertEqual(status["activeDocument"]["title"], "Sample.rvt")

            tools = client.list_tools()
            self.assertEqual(
                [tool["name"] for tool in tools],
                ["revit.status", "revit.get_levels", "revit.preview_change_set", "revit.apply_change_set"],
            )

            levels = client.get_levels({"documentFingerprint": "doc-1"})
            self.assertEqual(levels[0]["id"], "311")

            change_set = {"transactionName": "test", "operations": []}
            preview = client.preview_change_set(change_set)
            self.assertEqual(preview["previewId"], "preview-1")
            apply_result = client.apply_preview(change_set, preview)
            self.assertEqual(apply_result["payload"]["confirm"], True)
            self.assertEqual(apply_result["payload"]["previewId"], "preview-1")
            self.assertEqual(apply_result["payload"]["changeSetHash"], "hash-1")

            with self.assertRaises(RevitMcpToolError):
                client.call_tool("revit.unknown", {})

    def test_client_convenience_methods_map_to_revit_tool_names(self):
        client = RecordingClient()
        change_set = {"transactionName": "test", "operations": []}
        cases = [
            ("revit.status", {}, lambda: client.status()),
            ("revit.read_bundle", {"limit": 1}, lambda: client.read_bundle({"limit": 1})),
            ("revit.list_documents", {"limit": 1}, lambda: client.list_documents({"limit": 1})),
            ("revit.get_levels", {"limit": 1}, lambda: client.get_levels({"limit": 1})),
            ("revit.get_views", {"limit": 1}, lambda: client.get_views({"limit": 1})),
            ("revit.get_sheets", {"limit": 1}, lambda: client.get_sheets({"limit": 1})),
            ("revit.get_current_view", {"limit": 1}, lambda: client.get_current_view({"limit": 1})),
            ("revit.get_current_view_elements", {"limit": 1}, lambda: client.get_current_view_elements({"limit": 1})),
            ("revit.get_selection", {"limit": 1}, lambda: client.get_selection({"limit": 1})),
            ("revit.analyze_model", {"limit": 1}, lambda: client.analyze_model({"limit": 1})),
            ("revit.get_model_readiness", {"limit": 1}, lambda: client.get_model_readiness({"limit": 1})),
            ("revit.get_model_context", {"limit": 1}, lambda: client.get_model_context({"limit": 1})),
            ("revit.get_material_quantities", {"limit": 1}, lambda: client.get_material_quantities({"limit": 1})),
            ("revit.get_warnings", {"limit": 1}, lambda: client.get_warnings({"limit": 1})),
            ("revit.get_rooms", {"limit": 1}, lambda: client.get_rooms({"limit": 1})),
            ("revit.query", {"limit": 1}, lambda: client.query({"limit": 1})),
            ("revit.describe_parameters", {"limit": 1}, lambda: client.describe_parameters({"limit": 1})),
            ("revit.catalog", {"kind": "familySymbols"}, lambda: client.catalog({"kind": "familySymbols"})),
            ("revit.preview_change_set", change_set, lambda: client.preview_change_set(change_set)),
            ("revit.apply_change_set", change_set, lambda: client.apply_change_set(change_set)),
        ]

        for expected_name, expected_arguments, call in cases:
            client.calls = []
            result = call()
            self.assertEqual(result["toolName"], expected_name)
            self.assertEqual(result["arguments"], expected_arguments)
            self.assertEqual(client.calls, [(expected_name, expected_arguments)])

    def test_client_apply_preview_does_not_mutate_change_set(self):
        client = RecordingClient()
        change_set = {"transactionName": "test", "operations": []}
        original = {"transactionName": "test", "operations": []}
        preview = {"previewId": "preview-1"}

        result = client.apply_preview(change_set, preview)

        self.assertEqual(change_set, original)
        self.assertEqual(result["toolName"], "revit.apply_change_set")
        payload = result["arguments"]
        self.assertEqual(payload["previewId"], "preview-1")
        self.assertEqual(payload["confirm"], True)
        self.assertNotIn("changeSetHash", payload)
        self.assertNotIn("baseGeneration", payload)
        self.assertNotIn("expiresAt", payload)


if __name__ == "__main__":
    unittest.main()
