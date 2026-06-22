param(
    [switch] $DryRun,
    [int[]] $RevitYears = @(2024),
    [string] $InstallRoot = "$env:LOCALAPPDATA\RevitMcpNext",
    [string] $OutputRoot = "",
    [int64] $MaxLogBytes = 5242880
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next support] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Add-TrailingSeparator($Path) {
    if ($Path.EndsWith("\") -or $Path.EndsWith("/")) {
        return $Path
    }

    return "$Path\"
}

function Get-RelativePath($Root, $Path) {
    $rootFull = Add-TrailingSeparator (Get-FullPath $Root)
    $pathFull = Get-FullPath $Path
    $rootUri = New-Object System.Uri($rootFull)
    $pathUri = New-Object System.Uri($pathFull)
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
}

function Redact-Text($Text) {
    $result = [string] $Text

    $pathRedactions = @(
        @{ Value = $env:USERPROFILE; Replacement = "%USERPROFILE%" },
        @{ Value = $env:LOCALAPPDATA; Replacement = "%LOCALAPPDATA%" },
        @{ Value = $env:APPDATA; Replacement = "%APPDATA%" }
    )

    foreach ($redaction in $pathRedactions) {
        if (-not [string]::IsNullOrWhiteSpace($redaction.Value)) {
            $result = [regex]::Replace($result, [regex]::Escape($redaction.Value), $redaction.Replacement, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }

    $secretPattern = "(?i)\b(api[-_ ]?key|token|secret|password|passwd|pwd|client_secret|access_token|refresh_token)\b\s*[:=]\s*[""']?[^""'\r\n,;]+"
    $result = [regex]::Replace($result, $secretPattern, '$1=<redacted>')
    $result = [regex]::Replace($result, "[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}", "<redacted-jwt>")
    $result = [regex]::Replace($result, "-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", "<redacted-private-key>")

    return $result
}

function Write-RedactedTextFile($Path, $Text) {
    if ($DryRun) {
        Write-Step "Would write redacted text file: $Path"
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Set-Content -LiteralPath $Path -Value (Redact-Text $Text) -Encoding UTF8
}

function Write-JsonFile($Path, $Value) {
    Write-RedactedTextFile $Path ($Value | ConvertTo-Json -Depth 8)
}

function Copy-RedactedTextFile($Source, $Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        return
    }

    $file = Get-Item -LiteralPath $Source
    if ($file.Length -gt $MaxLogBytes) {
        Write-RedactedTextFile "$Destination.skipped.txt" "Skipped $($file.Name): file size $($file.Length) exceeds MaxLogBytes $MaxLogBytes."
        return
    }

    try {
        $text = Get-Content -LiteralPath $Source -Raw -ErrorAction Stop
        Write-RedactedTextFile $Destination $text
    } catch {
        Write-RedactedTextFile "$Destination.skipped.txt" "Skipped $($file.Name): unable to read as text. $($_.Exception.Message)"
    }
}

function Invoke-Doctor($Year, $Destination) {
    $doctorScript = Join-Path $PSScriptRoot "doctor.ps1"
    if (-not (Test-Path -LiteralPath $doctorScript -PathType Leaf)) {
        Write-RedactedTextFile $Destination "doctor.ps1 was not found at $doctorScript."
        return
    }

    if ($DryRun) {
        Write-Step "Would run doctor for Revit $Year"
        return
    }

    $powershell = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if (-not $powershell) {
        Write-RedactedTextFile $Destination "powershell.exe was not found; doctor was not run."
        return
    }

    $output = & $powershell.Source -NoProfile -ExecutionPolicy Bypass -File $doctorScript -InstallRoot $InstallRoot -RevitYear $Year 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    $text += "`r`n[doctor exitCode] $exitCode`r`n"
    Write-RedactedTextFile $Destination $text
}

function Get-CommandSummary($CommandName) {
    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if (-not $command) {
        return $null
    }

    $version = $null
    try {
        $version = (& $command.Source --version 2>$null | Out-String).Trim()
    } catch {
        $version = $null
    }

    return [ordered] @{
        path = $command.Source
        version = $version
    }
}

function Get-FileInventoryEntry($Path, $Root) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $file = Get-Item -LiteralPath $Path
    $relativePath = $file.FullName
    if (-not [string]::IsNullOrWhiteSpace($Root) -and (Test-Path -LiteralPath $Root -PathType Container)) {
        $relativePath = Get-RelativePath $Root $file.FullName
    }

    return [ordered] @{
        path = $relativePath
        size = $file.Length
        lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString("o")
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$repoOrPackageRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoOrPackageRoot "artifacts\support"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stageRoot = Join-Path (Get-FullPath $OutputRoot) "revit-mcp-next-support-$timestamp"
$zipPath = "$stageRoot.zip"

Write-Step "Collecting support bundle: $stageRoot"

if ($DryRun) {
    Write-Step "Would reset support staging directory: $stageRoot"
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stageRoot) | Out-Null
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
}

$environmentSummary = [ordered] @{
    collectedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    installRoot = $InstallRoot
    revitYears = $RevitYears
    osVersion = [System.Environment]::OSVersion.VersionString
    machineName = $env:COMPUTERNAME
    userDomain = $env:USERDOMAIN
    processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    node = Get-CommandSummary "node.exe"
    npm = Get-CommandSummary "npm.cmd"
    dotnet = Get-CommandSummary "dotnet.exe"
}

Write-JsonFile (Join-Path $stageRoot "environment-summary.json") $environmentSummary

foreach ($year in $RevitYears) {
    Invoke-Doctor $year (Join-Path $stageRoot "diagnostics\doctor-$year.txt")
}

$configFiles = @(
    @{ Source = (Join-Path $InstallRoot "launch-revit-mcp-next.cmd"); Destination = "config\launch-revit-mcp-next.cmd" },
    @{ Source = (Join-Path $InstallRoot "install-receipt.json"); Destination = "config\install-receipt.json" },
    @{ Source = (Join-Path $InstallRoot "release-manifest.json"); Destination = "config\release-manifest.json" },
    @{ Source = (Join-Path $InstallRoot "release-CHECKSUMS.sha256"); Destination = "config\release-CHECKSUMS.sha256" },
    @{ Source = (Join-Path $InstallRoot "broker\package.json"); Destination = "config\broker-package.json" },
    @{ Source = (Join-Path $InstallRoot "contracts\package.json"); Destination = "config\contracts-package.json" }
)

foreach ($year in $RevitYears) {
    $configFiles += @{
        Source = (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\RevitMcpNext.addin")
        Destination = "config\RevitMcpNext-$year.addin"
    }
}

foreach ($configFile in $configFiles) {
    Copy-RedactedTextFile $configFile.Source (Join-Path $stageRoot $configFile.Destination)
}

$logsRoot = Join-Path $InstallRoot "logs"
if (Test-Path -LiteralPath $logsRoot -PathType Container) {
    $logFiles = Get-ChildItem -LiteralPath $logsRoot -Recurse -File | Sort-Object LastWriteTimeUtc -Descending
    foreach ($logFile in $logFiles) {
        $relativePath = Get-RelativePath $logsRoot $logFile.FullName
        Copy-RedactedTextFile $logFile.FullName (Join-Path $stageRoot (Join-Path "logs" $relativePath))
    }
} else {
    Write-RedactedTextFile (Join-Path $stageRoot "logs\README.txt") "No logs directory found at $logsRoot."
}

$inventoryTargets = New-Object System.Collections.Generic.List[string]
$inventoryTargets.Add((Join-Path $InstallRoot "launch-revit-mcp-next.cmd")) | Out-Null
$inventoryTargets.Add((Join-Path $InstallRoot "broker\dist\src\index.js")) | Out-Null
$inventoryTargets.Add((Join-Path $InstallRoot "broker\dist\src\server.js")) | Out-Null
$inventoryTargets.Add((Join-Path $InstallRoot "addin\RevitMcpNext.Addin.dll")) | Out-Null
$inventoryTargets.Add((Join-Path $InstallRoot "addin\RevitMcpNext.Contracts.dll")) | Out-Null
foreach ($year in $RevitYears) {
    $inventoryTargets.Add((Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\RevitMcpNext.addin")) | Out-Null
}

$inventory = New-Object System.Collections.Generic.List[object]
foreach ($target in $inventoryTargets) {
    $entry = Get-FileInventoryEntry $target $InstallRoot
    if ($entry) {
        $inventory.Add($entry) | Out-Null
    }
}

Write-JsonFile (Join-Path $stageRoot "file-inventory.json") $inventory

$bundleManifest = [ordered] @{
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    redaction = "Text files are redacted for common secret key names, JWT-shaped tokens, private keys, and local profile paths. Environment variables are not collected."
    maxLogBytes = $MaxLogBytes
    installRoot = $InstallRoot
    revitYears = $RevitYears
}

Write-JsonFile (Join-Path $stageRoot "support-bundle-manifest.json") $bundleManifest

if ($DryRun) {
    Write-Step "Would create support bundle zip: $zipPath"
    return
}

Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -Force
Write-Step "Created support bundle: $zipPath"
