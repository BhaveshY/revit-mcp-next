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
