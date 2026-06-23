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
                {"name": "revit.get_levels", "inputSchema": {"type": "object"}}
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
            self.assertEqual([tool["name"] for tool in tools], ["revit.status", "revit.get_levels"])

            levels = client.call_tool("revit.get_levels", {"documentFingerprint": "doc-1"})
            self.assertEqual(levels[0]["id"], "311")

            with self.assertRaises(RevitMcpToolError):
                client.call_tool("revit.unknown", {})


if __name__ == "__main__":
    unittest.main()

