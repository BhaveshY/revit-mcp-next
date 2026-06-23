param(
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [int] $RevitYear = 2024
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

function Test-RequiredFile($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "[ok] $Label"
        return $true
    }

    Write-Host "[missing] $Label -> $Path"
    $failures.Add("$Label missing: $Path")
    return $false
}

function Test-OptionalFile($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "[ok] $Label"
    } else {
        Write-Host "[info] optional $Label not found: $Path"
    }
}

function Test-RequiredDirectory($Path, $Label) {
    if (Test-Path -LiteralPath $Path -PathType Container) {
        Write-Host "[ok] $Label"
        return $true
    }

    Write-Host "[missing] $Label -> $Path"
    $failures.Add("$Label missing: $Path")
    return $false
}

function Add-TrailingSeparator($Path) {
    if ($Path.EndsWith("\") -or $Path.EndsWith("/")) {
        return $Path
    }

    return "$Path\"
}

function Test-NodeVersion($NodeCommand) {
    $versionText = (& $NodeCommand.Source --version)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($versionText)) {
        Write-Host "[missing] node.exe version check failed"
        $failures.Add("node.exe version check failed")
        return
    }

    if ($versionText -notmatch "^v?(\d+)\.") {
        Write-Host "[missing] node.exe version could not be parsed: $versionText"
        $failures.Add("node.exe version could not be parsed: $versionText")
        return
    }

    $major = [int] $Matches[1]
    if ($major -eq 24) {
        Write-Host "[ok] node.exe $versionText at $($NodeCommand.Source)"
    } else {
        Write-Host "[missing] node.exe $versionText at $($NodeCommand.Source) (expected Node 24.x)"
        $failures.Add("node.exe version is $versionText; expected Node 24.x")
    }
}

function Test-ManifestAssemblyPath($ManifestPath, $ExpectedAssemblyPath) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return
    }

    $manifestText = Get-Content -LiteralPath $ManifestPath -Raw
    $acceptedPaths = New-Object System.Collections.Generic.List[string]
    $acceptedPaths.Add($ExpectedAssemblyPath) | Out-Null

    $manifestDir = Split-Path -Parent $ManifestPath
    $manifestDirFull = Add-TrailingSeparator ([System.IO.Path]::GetFullPath($manifestDir))
    $expectedFull = [System.IO.Path]::GetFullPath($ExpectedAssemblyPath)
    if ($expectedFull.StartsWith($manifestDirFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rootUri = New-Object System.Uri($manifestDirFull)
        $pathUri = New-Object System.Uri($expectedFull)
        $relativePath = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
        $acceptedPaths.Add($relativePath.Replace("\", "/")) | Out-Null
        $acceptedPaths.Add($relativePath.Replace("/", "\")) | Out-Null
    }

    foreach ($acceptedPath in $acceptedPaths) {
        if ($manifestText.Contains($acceptedPath)) {
            Write-Host "[ok] Revit manifest points at staged add-in DLL"
            return
        }
    }

    Write-Host "[missing] Revit manifest does not point at staged add-in DLL"
    $failures.Add("Revit manifest target mismatch: $ManifestPath")
}

function Test-ManifestIdentity($ManifestPath, $ExpectedId) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return
    }

    $manifestText = Get-Content -LiteralPath $ManifestPath -Raw
    if ($manifestText.Contains("<AddInId>$ExpectedId</AddInId>")) {
        Write-Host "[ok] Revit manifest uses AddInId for add-in identity"
        return
    }

    if ($manifestText.Contains("<ClientId>$ExpectedId</ClientId>")) {
        Write-Host "[ok] Revit manifest uses legacy ClientId for add-in identity"
    } else {
        Write-Host "[missing] Revit manifest does not contain the expected add-in identity"
        $failures.Add("Revit manifest identity missing: $ManifestPath")
    }
}

function Test-AuthenticodeFile($Path, $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }

    try {
        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        if ($signature.Status -eq "Valid") {
            $subject = ""
            if ($signature.SignerCertificate) {
                $subject = " ($($signature.SignerCertificate.Subject))"
            }
            Write-Host "[ok] $Label is Authenticode signed$subject"
        } elseif ($signature.Status -eq "NotSigned") {
            Write-Host "[warn] $Label is not Authenticode signed"
        } else {
            Write-Host "[warn] $Label signature status is $($signature.Status): $($signature.StatusMessage)"
        }
    } catch {
        Write-Host "[info] $Label signature could not be inspected: $($_.Exception.Message)"
    }
}

function Read-AuthTokenConfig($Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*"?([^"\s]+)"?\s*$') {
            return $Matches[1]
        }
    }

    return ""
}

function Test-AuthTokenShape($Token) {
    return -not [string]::IsNullOrWhiteSpace($Token) -and $Token -match "^[A-Za-z0-9_-]{43,}$"
}

function Test-RuleGrantsRead($Rule) {
    $rights = $Rule.FileSystemRights
    foreach ($right in @(
        [System.Security.AccessControl.FileSystemRights]::ReadData,
        [System.Security.AccessControl.FileSystemRights]::Read,
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [System.Security.AccessControl.FileSystemRights]::Modify,
        [System.Security.AccessControl.FileSystemRights]::FullControl
    )) {
        if (($rights -band $right) -eq $right) {
            return $true
        }
    }

    return $false
}

function Test-AuthConfigAcl($Path) {
    try {
        $acl = Get-Acl -LiteralPath $Path
        $warnings = New-Object System.Collections.Generic.List[string]
        if (-not $acl.AreAccessRulesProtected) {
            $warnings.Add("auth token config inherits ACLs") | Out-Null
        }

        $broadSids = @(
            "S-1-1-0",       # Everyone
            "S-1-5-11",      # Authenticated Users
            "S-1-5-32-545"   # Builtin Users
        )

        foreach ($rule in $acl.Access) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) {
                continue
            }

            if (-not (Test-RuleGrantsRead $rule)) {
                continue
            }

            try {
                $sid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            } catch {
                $sid = ""
            }

            if ($broadSids -contains $sid) {
                $warnings.Add("auth token config grants read access to $($rule.IdentityReference.Value)") | Out-Null
            }
        }

        if ($warnings.Count -eq 0) {
            Write-Host "[ok] auth token config ACL is restricted"
        } else {
            $warnings | ForEach-Object { Write-Host "[warn] $_" }
        }
    } catch {
        Write-Host "[info] auth token config ACL could not be inspected: $($_.Exception.Message)"
    }
}

Write-Host "[revit-mcp-next doctor] Install root: $InstallRoot"

$node = Get-Command node.exe -ErrorAction SilentlyContinue
if ($node) {
    Test-NodeVersion $node
} else {
    Write-Host "[missing] node.exe"
    $failures.Add("node.exe not found")
}

$launcher = Join-Path $InstallRoot "launch-revit-mcp-next.cmd"
$authConfig = Join-Path $InstallRoot "config\auth.env"
$clientDiscovery = Join-Path $InstallRoot "config\client-discovery.json"
$brokerEntry = Join-Path $InstallRoot "broker\dist\src\index.js"
$brokerServer = Join-Path $InstallRoot "broker\dist\src\server.js"
$addinDll = Join-Path $InstallRoot "addin\RevitMcpNext.Addin.dll"
$contractsDll = Join-Path $InstallRoot "addin\RevitMcpNext.Contracts.dll"
$addinPdb = Join-Path $InstallRoot "addin\RevitMcpNext.Addin.pdb"
$contractsPdb = Join-Path $InstallRoot "addin\RevitMcpNext.Contracts.pdb"
$pythonClient = Join-Path $InstallRoot "integrations\python\revit_mcp_next_client.py"
$pythonInProcessHelper = Join-Path $InstallRoot "integrations\python\revit_mcp_next_inprocess.py"
$pyRevitStatusCommand = Join-Path $InstallRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Diagnostics.panel\Status.pushbutton\script.py"
$pyRevitCreateLevelCommand = Join-Path $InstallRoot "integrations\pyrevit\revit_mcp_next.extension\Revit MCP Next.tab\Examples.panel\Create Level.pushbutton\script.py"
$dynamoStatusNode = Join-Path $InstallRoot "integrations\dynamo\status_node.py"
$dynamoCreateLevelNode = Join-Path $InstallRoot "integrations\dynamo\create_level_node.py"
$manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\RevitMcpNext.addin"
$logs = Join-Path $InstallRoot "logs"
$receipt = Join-Path $InstallRoot "install-receipt.json"
$releaseManifest = Join-Path $InstallRoot "release-manifest.json"

$launcherOk = Test-RequiredFile $launcher "MCP launcher"
$authConfigOk = Test-RequiredFile $authConfig "auth token config"
Test-RequiredFile $clientDiscovery "client discovery config" | Out-Null
Test-RequiredFile $brokerEntry "broker entry" | Out-Null
Test-RequiredFile $brokerServer "broker server module" | Out-Null
Test-RequiredFile $addinDll "Revit add-in DLL" | Out-Null
Test-RequiredFile $contractsDll "Revit contracts DLL" | Out-Null
Test-RequiredFile $pythonClient "Python MCP integration client" | Out-Null
Test-RequiredFile $pythonInProcessHelper "Python in-process integration helper" | Out-Null
Test-RequiredFile $pyRevitStatusCommand "pyRevit status command example" | Out-Null
Test-RequiredFile $pyRevitCreateLevelCommand "pyRevit preview/apply create-level example" | Out-Null
Test-RequiredFile $dynamoStatusNode "Dynamo status node example" | Out-Null
Test-RequiredFile $dynamoCreateLevelNode "Dynamo preview/apply create-level node example" | Out-Null
Test-RequiredFile $manifest "Revit add-in manifest" | Out-Null
Test-RequiredDirectory (Join-Path $InstallRoot "broker\node_modules") "broker production node_modules" | Out-Null
Test-OptionalFile $addinPdb "Revit add-in PDB"
Test-OptionalFile $contractsPdb "Revit contracts PDB"
Test-OptionalFile $receipt "install receipt"
Test-OptionalFile $releaseManifest "release manifest"
Test-ManifestAssemblyPath $manifest $addinDll
Test-ManifestIdentity $manifest "6F78E70D-BE13-4E0B-9B11-9E28F876AF71"
Test-AuthenticodeFile $addinDll "Revit add-in DLL"
Test-AuthenticodeFile $contractsDll "Revit contracts DLL"

if ($authConfigOk) {
    $authToken = Read-AuthTokenConfig $authConfig
    if (Test-AuthTokenShape $authToken) {
        Write-Host "[ok] auth token config contains a strong token (redacted)"
    } else {
        Write-Host "[missing] auth token config is missing a strong token"
        $failures.Add("auth token config is missing a strong token: $authConfig")
    }

    Test-AuthConfigAcl $authConfig
}

if ($launcherOk) {
    $launcherText = Get-Content -LiteralPath $launcher -Raw
    if ($launcherText.Contains($brokerEntry)) {
        Write-Host "[ok] MCP launcher points at staged broker entry"
    } else {
        Write-Host "[missing] MCP launcher does not point at staged broker entry"
        $failures.Add("MCP launcher target mismatch: $launcher")
    }

    if ($launcherText.Contains("REVIT_MCP_NEXT_AUTH_TOKEN")) {
        Write-Host "[ok] MCP launcher exports auth token from local config"
    } else {
        Write-Host "[missing] MCP launcher does not export REVIT_MCP_NEXT_AUTH_TOKEN"
        $failures.Add("MCP launcher auth token setup missing: $launcher")
    }

    if ($launcherText -match 'REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*[A-Za-z0-9_-]{20,}') {
        Write-Host "[warn] MCP launcher appears to contain an inline auth token; reinstall to use the config-backed launcher"
    }
}

if ($node -and (Test-Path -LiteralPath $brokerServer -PathType Leaf)) {
    $script = "const p = process.argv[1]; import('file:///' + p.replace(/\\/g,'/')).then(() => console.log('[ok] broker imports')).catch((error) => { console.error(error); process.exit(1); });"
    & $node.Source -e $script $brokerServer
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("broker import failed")
    }
}

if (Test-Path -LiteralPath $logs -PathType Container) {
    Write-Host "[ok] logs directory: $logs"
} else {
    Write-Host "[info] logs directory will be created when the add-in writes diagnostics: $logs"
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "[revit-mcp-next doctor] FAILED"
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "[revit-mcp-next doctor] OK"
