from __future__ import print_function

import json
import os
import subprocess
import sys
import threading
import time


MCP_PROTOCOL_VERSION = "2025-11-25"


class RevitMcpError(RuntimeError):
    pass


class RevitMcpToolError(RevitMcpError):
    def __init__(self, tool_name, result):
        self.tool_name = tool_name
        self.result = result
        message = "MCP tool failed: {0}".format(tool_name)
        content = result.get("content") if isinstance(result, dict) else None
        if content:
            text_parts = []
            for item in content:
                if isinstance(item, dict) and item.get("type") == "text":
                    text_parts.append(item.get("text") or "")
            if text_parts:
                message = message + "\n" + "\n".join(text_parts)
        RuntimeError.__init__(self, message)


class RevitMcpClient(object):
    """Small stdlib MCP stdio client for plain Python outside Revit.

    The client talks to the installed Revit MCP Next launcher. It does not call
    the add-in named pipe directly; auth, broker policy, and tool schemas stay
    in one place.
    """

    def __init__(self, launcher_path=None, command=None, timeout_seconds=60):
        self.launcher_path = launcher_path
        self.command = command
        self.timeout_seconds = timeout_seconds
        self._process = None
        self._next_id = 1
        self._stderr = []
        self._stderr_thread = None
        self._initialized = False

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()

    def start(self):
        if self._process is not None:
            return

        command = self.command or default_launcher_command(self.launcher_path)
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        self._process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=None,
            creationflags=creationflags,
        )
        self._stderr_thread = threading.Thread(target=self._drain_stderr)
        self._stderr_thread.daemon = True
        self._stderr_thread.start()
        self.initialize()

    def initialize(self):
        if self._initialized:
            return

        result = self.request(
            "initialize",
            {
                "protocolVersion": MCP_PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": "revit-mcp-next-python", "version": "0.1.0"},
            },
        )
        negotiated = result.get("protocolVersion")
        if not negotiated:
            raise RevitMcpError("MCP server initialize response did not include protocolVersion.")
        self.notify("notifications/initialized", {})
        self._initialized = True

    def list_tools(self):
        result = self.request("tools/list", {})
        return result.get("tools") or []

    def call_tool(self, name, arguments=None):
        self.start()
        result = self.request(
            "tools/call",
            {
                "name": name,
                "arguments": arguments or {},
            },
        )
        if result.get("isError"):
            raise RevitMcpToolError(name, result)
        structured = result.get("structuredContent")
        if isinstance(structured, dict) and "data" in structured:
            return structured.get("data")
        return result

    def status(self):
        return self.call_tool("revit.status", {})

    def request(self, method, params=None):
        self._ensure_running()
        request_id = self._next_id
        self._next_id += 1
        self._write_message(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "method": method,
                "params": params or {},
            }
        )
        return self._read_response(request_id)

    def notify(self, method, params=None):
        self._ensure_running()
        self._write_message(
            {
                "jsonrpc": "2.0",
                "method": method,
                "params": params or {},
            }
        )

    def close(self):
        process = self._process
        self._process = None
        if process is None:
            return
        try:
            if process.stdin:
                process.stdin.close()
        except Exception:
            pass
        try:
            process.terminate()
        except Exception:
            pass
        try:
            wait_process(process, 2)
        except Exception:
            try:
                process.kill()
            except Exception:
                pass
        for stream in (process.stdout, process.stderr):
            try:
                if stream:
                    stream.close()
            except Exception:
                pass

    def stderr_text(self):
        return "".join(self._stderr)

    def _ensure_running(self):
        if self._process is None:
            raise RevitMcpError("MCP client is not started.")
        if self._process.poll() is not None:
            stderr = self.stderr_text().strip()
            suffix = "\n" + stderr if stderr else ""
            raise RevitMcpError("MCP launcher exited with code {0}.{1}".format(self._process.returncode, suffix))

    def _write_message(self, message):
        line = json.dumps(message, separators=(",", ":")) + "\n"
        self._process.stdin.write(to_bytes(line))
        self._process.stdin.flush()

    def _read_response(self, request_id):
        deadline = time.time() + self.timeout_seconds
        while time.time() < deadline:
            self._ensure_running()
            raw = self._process.stdout.readline()
            if not raw:
                time.sleep(0.02)
                continue
            line = to_text(raw).strip()
            if not line:
                continue
            message = json.loads(line)
            if message.get("id") != request_id:
                continue
            if "error" in message:
                error = message.get("error") or {}
                raise RevitMcpError("{0}: {1}".format(error.get("code"), error.get("message")))
            return message.get("result") or {}

        stderr = self.stderr_text().strip()
        suffix = "\n" + stderr if stderr else ""
        raise RevitMcpError("Timed out waiting for MCP response id {0}.{1}".format(request_id, suffix))

    def _drain_stderr(self):
        try:
            while self._process is not None and self._process.stderr is not None:
                chunk = self._process.stderr.readline()
                if not chunk:
                    break
                self._stderr.append(to_text(chunk))
                if len("".join(self._stderr)) > 16000 and len(self._stderr) > 1:
                    self._stderr.pop(0)
        except Exception:
            pass


def default_launcher_path():
    discovery = read_client_discovery(required=False)
    launcher_path = discovery.get("launcherPath") if isinstance(discovery, dict) else None
    if launcher_path:
        return launcher_path

    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RevitMcpError("LOCALAPPDATA is not set. Pass launcher_path explicitly.")
    return os.path.join(local_app_data, "RevitMcpNext", "launch-revit-mcp-next.cmd")


def default_client_discovery_path():
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RevitMcpError("LOCALAPPDATA is not set. Pass discovery_path explicitly.")
    return os.path.join(local_app_data, "RevitMcpNext", "config", "client-discovery.json")


def read_client_discovery(discovery_path=None, required=True):
    try:
        path = discovery_path or default_client_discovery_path()
    except RevitMcpError:
        if required:
            raise
        return {}
    if not os.path.exists(path):
        if required:
            raise RevitMcpError("Revit MCP Next client discovery was not found: {0}".format(path))
        return {}
    with open(path, "r") as handle:
        return json.load(handle)


def default_launcher_command(launcher_path=None):
    launcher = launcher_path or default_launcher_path()
    if not os.path.exists(launcher):
        raise RevitMcpError("Revit MCP Next launcher was not found: {0}".format(launcher))
    if os.name == "nt":
        comspec = os.environ.get("ComSpec") or "cmd.exe"
        return [comspec, "/d", "/s", "/c", '"{0}"'.format(launcher)]
    return [launcher]


def wait_process(process, timeout_seconds):
    if hasattr(process, "wait"):
        try:
            return process.wait(timeout=timeout_seconds)
        except TypeError:
            deadline = time.time() + timeout_seconds
            while time.time() < deadline:
                code = process.poll()
                if code is not None:
                    return code
                time.sleep(0.05)
            raise RevitMcpError("process did not exit before timeout")


def to_bytes(value):
    if sys.version_info[0] < 3:
        if isinstance(value, unicode):  # noqa: F821
            return value.encode("utf-8")
        return value
    if isinstance(value, bytes):
        return value
    return value.encode("utf-8")


def to_text(value):
    if sys.version_info[0] < 3:
        if isinstance(value, unicode):  # noqa: F821
            return value
        return value.decode("utf-8")
    if isinstance(value, str):
        return value
    return value.decode("utf-8", "replace")
