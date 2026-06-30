param(
    [string] $PythonPath = ""
)

$ErrorActionPreference = "Stop"

function Resolve-PythonCommand {
    if (-not [string]::IsNullOrWhiteSpace($PythonPath)) {
        return @{
            Command = $PythonPath
            PrefixArgs = @()
        }
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        return @{
            Command = $python.Source
            PrefixArgs = @()
        }
    }

    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        return @{
            Command = $py.Source
            PrefixArgs = @("-3")
        }
    }

    throw "Python 3 was not found on PATH. Install Python 3 or pass -PythonPath."
}

function Invoke-Python {
    param(
        [hashtable] $Python,
        [string[]] $Arguments
    )

    & $Python.Command @($Python.PrefixArgs + $Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "Python command failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$python = Resolve-PythonCommand

Write-Host "[revit-mcp-next integrations] Using Python: $($python.Command) $($python.PrefixArgs -join ' ')"

$syntaxCheck = @"
import ast
import json
import os
import sys

root = sys.argv[1]
folders = [
    os.path.join(root, "integrations", "python"),
    os.path.join(root, "integrations", "pyrevit"),
    os.path.join(root, "integrations", "dynamo"),
]
for folder in folders:
    for current, _, files in os.walk(folder):
        for name in files:
            if not name.endswith(".py"):
                continue
            path = os.path.join(current, name)
            with open(path, "rb") as handle:
                source = handle.read()
            ast.parse(source, filename=path)
            print("[ok] syntax", os.path.relpath(path, root))

graph_path = os.path.join(root, "integrations", "dynamo", "revit_mcp_next_host_smoke.dyn")
with open(graph_path, "r", encoding="utf-8") as handle:
    graph = json.load(handle)
python_nodes = [node for node in graph.get("Nodes", []) if node.get("NodeType") == "PythonScriptNode"]
if len(python_nodes) != 1:
    raise AssertionError("Dynamo host-smoke graph must contain exactly one PythonScriptNode.")
code = python_nodes[0].get("Code") or ""
ast.parse(code, filename=graph_path + "#PythonScriptNode")
if "revit_mcp_next_host_smoke" not in code or "run_host_smoke" not in code:
    raise AssertionError("Dynamo host-smoke graph does not call the shared host-smoke helper.")
if "REVIT_MCP_NEXT_DYNAMO_MODEL" not in code:
    raise AssertionError("Dynamo host-smoke graph does not support a model path override.")
if python_nodes[0].get("EngineName") != "CPython3":
    raise AssertionError("Dynamo host-smoke graph must use CPython3.")
if graph.get("View", {}).get("Dynamo", {}).get("RunType") != "Manual":
    raise AssertionError("Dynamo host-smoke graph must use manual run mode.")
watch_nodes = [node for node in graph.get("Nodes", []) if node.get("ConcreteType") == "CoreNodeModels.Watch, CoreNodeModels"]
if len(watch_nodes) != 1:
    raise AssertionError("Dynamo host-smoke graph must include one Watch node for operator-visible evidence.")
if len(graph.get("Connectors", [])) != 1:
    raise AssertionError("Dynamo host-smoke graph must connect Python OUT to the Watch node.")
for field in ("NodeLibraryDependencies", "ExtensionWorkspaceData", "Linting"):
    if field not in graph:
        raise AssertionError("Dynamo host-smoke graph is missing {0}.".format(field))
print("[ok] dynamo graph", os.path.relpath(graph_path, root))
"@

$syntaxScript = Join-Path $env:TEMP "revit-mcp-next-python-syntax-$PID.py"
Set-Content -LiteralPath $syntaxScript -Value $syntaxCheck -Encoding UTF8
try {
    Invoke-Python $python @($syntaxScript, $repoRoot)
} finally {
    Remove-Item -LiteralPath $syntaxScript -Force -ErrorAction SilentlyContinue
}

Invoke-Python $python @("-m", "unittest", "discover", "-s", (Join-Path $repoRoot "integrations\python"), "-p", "*test*.py")

Write-Host "[revit-mcp-next integrations] Python integration tests passed."
