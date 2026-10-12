"""Stdlib MCP stdio client for plain Python running OUTSIDE Revit (scripts, notebooks, schedulers).

It starts the installed server exactly like Codex and Claude do:
    <home>\\runtime\\node.exe <home>\\broker\\revit-mcp.mjs     env REVIT_MCP_NEXT_HOME=<home>
and calls the same 37 tools. Results are the server's compact text (first line "<STATUS>: <summary> - doc: ...",
then notice:/warn: lines, one JSON line, more:/next:/fix: lines); ToolResult splits them for you.

    from revit_mcp_next_client import RevitMcpClient
    with RevitMcpClient() as revit:
        print(revit.call("status").text)
        walls = revit.call("find_elements", {"category": "Walls", "count_only": True})
        print(walls.status, walls.json)

Do not use it from inside Revit (pyRevit, Dynamo): it would wait on Revit's own UI thread. Use
revit_mcp_next_inprocess.py there. Do not call the add-in's named pipes yourself; the broker owns auth and targeting.
"""

from __future__ import print_function

import json
import os
import subprocess
import sys
import threading
import time


MCP_PROTOCOL_VERSION = "2025-11-25"
CLIENT_NAME = "revit-mcp-next-python"
CLIENT_VERSION = "0.4.0"


class RevitMcpError(RuntimeError):
    pass


class ToolResult(object):
    """One tools/call result. status: "ok", "not_applied", "running" or "error"; code: the ERROR code."""

    def __init__(self, tool, result):
        self.tool = tool
        self.raw = result or {}
        self.is_error = bool(self.raw.get("isError"))
        texts, images = [], []
        for item in self.raw.get("content") or []:
            if not isinstance(item, dict):
                continue
            if item.get("type") == "text":
                texts.append(item.get("text") or "")
            elif item.get("type") == "image":
                images.append({"mimeType": item.get("mimeType"), "data": item.get("data")})
        self.text = "\n".join(texts)
        self.images = images
        lines = [line for line in self.text.splitlines() if line.strip()]
        self.first_line = lines[0] if lines else ""
        self.status, self.code = _classify(self.first_line, self.is_error)
        self.json = None
        self.next = []
        self.fix = []
        self.more = None
        for line in lines[1:]:
            stripped = line.strip()
            if self.json is None and stripped.startswith("{"):
                try:
                    self.json = json.loads(stripped)
                    continue
                except ValueError:
                    pass
            if stripped.startswith("next:"):
                self.next.append(stripped[5:].strip())
            elif stripped.startswith("fix:"):
                self.fix.append(stripped[4:].strip())
            elif stripped.startswith("more:"):
                self.more = stripped[5:].strip()

    @property
    def ok(self):
        return self.status == "ok"

    def __repr__(self):
        return "<ToolResult {0} {1}: {2}>".format(self.tool, self.status, self.first_line[:120])


def _classify(first_line, is_error):
    if first_line.startswith("ERROR"):
        head = first_line[len("ERROR"):].strip()
        code = head.split(":", 1)[0].strip() or None
        return "error", code
    if first_line.startswith("NOT APPLIED"):
        return "not_applied", None
    if first_line.startswith("running"):
        return "running", None
    if is_error:
        return "error", None
    return "ok", None


class RevitMcpClient(object):
    """Starts the revit-mcp-next broker over stdio and calls its tools."""

    def __init__(self, home=None, command=None, env=None, timeout_seconds=130):
        self.home = home or default_home()
        self.command = command
        self.env = env
        self.timeout_seconds = timeout_seconds
        self.server_info = None
        self.instructions = None
        self._process = None
        self._next_id = 1
        self._stderr = []
        self._initialized = False

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()

    def start(self):
        if self._process is not None:
            return
        command = self.command or default_command(self.home)
        env = dict(os.environ)
        env["REVIT_MCP_NEXT_HOME"] = self.home
        if self.env:
            env.update(self.env)
        self._process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        thread = threading.Thread(target=self._drain_stderr)
        thread.daemon = True
        thread.start()
        self._initialize()

    def _initialize(self):
        result = self.request(
            "initialize",
            {
                "protocolVersion": MCP_PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": CLIENT_NAME, "version": CLIENT_VERSION},
            },
        )
        if not result.get("protocolVersion"):
            raise RevitMcpError("The server's initialize response has no protocolVersion.")
        self.server_info = result.get("serverInfo")
        self.instructions = result.get("instructions")
        self.notify("notifications/initialized", {})
        self._initialized = True

    def list_tools(self):
        self.start()
        return self.request("tools/list", {}).get("tools") or []

    def call(self, tool, arguments=None):
        """Calls a tool (bare name: "status", "find_elements", ...) and returns a ToolResult. Never raises for
        ERROR results; check result.status / result.code and follow result.fix like a model would."""
        self.start()
        result = self.request("tools/call", {"name": tool, "arguments": arguments or {}})
        return ToolResult(tool, result)

    def status(self):
        return self.call("status")

    def help(self, tool=None, op=None, topic=None):
        args = {}
        if tool:
            args["tool"] = tool
        if op:
            args["op"] = op
        if topic:
            args["topic"] = topic
        return self.call("help", args)

    def wait_job(self, job_id, poll_seconds=2.0, timeout_seconds=1800):
        """Polls job_status until the job is no longer running; returns the last ToolResult."""
        deadline = time.time() + timeout_seconds
        while True:
            result = self.call("job_status", {"id": job_id})
            if result.status != "running" or time.time() > deadline:
                return result
            time.sleep(poll_seconds)

    def request(self, method, params=None):
        self._ensure_running()
        request_id = self._next_id
        self._next_id += 1
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}})
        return self._read_response(request_id)

    def notify(self, method, params=None):
        self._ensure_running()
        self._write({"jsonrpc": "2.0", "method": method, "params": params or {}})

    def close(self):
        process = self._process
        self._process = None
        if process is None:
            return
        try:
            if process.stdin:
                process.stdin.close()  # the broker exits on stdin EOF
        except Exception:
            pass
        deadline = time.time() + 3
        while process.poll() is None and time.time() < deadline:
            time.sleep(0.05)
        if process.poll() is None:
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
            raise RevitMcpError("The client is not started.")
        if self._process.poll() is not None:
            tail = self.stderr_text().strip()
            raise RevitMcpError(
                "The revit-mcp-next server exited with code {0}.{1}".format(self._process.returncode, ("\n" + tail) if tail else "")
            )

    def _write(self, message):
        self._process.stdin.write(_to_bytes(json.dumps(message, separators=(",", ":")) + "\n"))
        self._process.stdin.flush()

    def _read_response(self, request_id):
        deadline = time.time() + self.timeout_seconds
        while time.time() < deadline:
            self._ensure_running()
            raw = self._process.stdout.readline()
            if not raw:
                time.sleep(0.02)
                continue
            line = _to_text(raw).strip()
            if not line:
                continue
            message = json.loads(line)
            if message.get("id") != request_id:
                continue  # notifications and server requests are ignored
            if "error" in message:
                error = message.get("error") or {}
                raise RevitMcpError("{0}: {1}".format(error.get("code"), error.get("message")))
            return message.get("result") or {}
        raise RevitMcpError("No answer to request {0} within {1} s.".format(request_id, self.timeout_seconds))

    def _drain_stderr(self):
        try:
            while self._process is not None and self._process.stderr is not None:
                chunk = self._process.stderr.readline()
                if not chunk:
                    break
                self._stderr.append(_to_text(chunk))
                while len(self._stderr) > 200:
                    self._stderr.pop(0)
        except Exception:
            pass


def default_home():
    """REVIT_MCP_NEXT_HOME, else %USERPROFILE%\\.revit-mcp-next."""
    explicit = os.environ.get("REVIT_MCP_NEXT_HOME")
    if explicit:
        return explicit
    return os.path.join(os.path.expanduser("~"), ".revit-mcp-next")


def default_command(home=None):
    """[<home>\\runtime\\node.exe, <home>\\broker\\revit-mcp.mjs], the same command the installer registers."""
    home = home or default_home()
    node = os.path.join(home, "runtime", "node.exe")
    bundle = os.path.join(home, "broker", "revit-mcp.mjs")
    for path in (node, bundle):
        if not os.path.exists(path):
            raise RevitMcpError(
                "{0} is missing. Install revit-mcp-next (installer\\install.ps1) or pass home=/command=.".format(path)
            )
    return [node, bundle]


def _to_bytes(value):
    if isinstance(value, bytes):
        return value
    return value.encode("utf-8")


def _to_text(value):
    if sys.version_info[0] < 3:
        return value if isinstance(value, unicode) else value.decode("utf-8")  # noqa: F821
    return value if isinstance(value, str) else value.decode("utf-8", "replace")


if __name__ == "__main__":
    # python revit_mcp_next_client.py [tool] [json-args]   e.g.  status   or   help '{"tool":"list"}'
    tool_name = sys.argv[1] if len(sys.argv) > 1 else "status"
    tool_args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    with RevitMcpClient() as client:
        print(client.call(tool_name, tool_args).text)
