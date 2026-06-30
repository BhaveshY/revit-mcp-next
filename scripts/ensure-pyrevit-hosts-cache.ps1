[CmdletBinding(PositionalBinding = $false)]
param(
    [string[]] $Builds = @("20230106_1515", "20241105_1515"),
    [int] $RevitYear = 2024,
    [string] $Product = "Autodesk Revit",
    [string] $Version = "24.0.0.0",
    [string] $ReleaseLabel = "",
    [string] $SourceHostsPath = "",
    [string] $CachePath = "$env:APPDATA\pyRevit\Cache\pyrevit-hosts.json",
    [switch] $DryRun,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next pyrevit-hosts] $Message"
    }
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Normalize-Build($Build) {
    return ([string] $Build).Replace("(x64)", "").Trim()
}

function Resolve-DefaultSourceHostsPath {
    $candidate = "C:\Program Files\pyRevit-Master\bin\pyrevit-hosts.json"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }

    $pyrevit = Get-Command pyrevit.exe -ErrorAction SilentlyContinue
    if ($pyrevit) {
        $toolRoot = Split-Path -Parent $pyrevit.Source
        $toolCandidate = Join-Path $toolRoot "pyrevit-hosts.json"
        if (Test-Path -LiteralPath $toolCandidate -PathType Leaf) {
            return $toolCandidate
        }
    }

    return ""
}

function Read-HostEntries($Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }

    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($null -eq $data) {
        return @()
    }

    if ($data -is [System.Array]) {
        return @($data)
    }

    return @($data)
}

function New-HostEntry($Build) {
    $release = $ReleaseLabel
    if ([string]::IsNullOrWhiteSpace($release)) {
        $release = "$RevitYear local host metadata"
    }

    [pscustomobject] @{
        meta = [ordered] @{
            schema = "1.0"
            source = "revit-mcp-next local pyRevit CLI compatibility helper"
        }
        product = $Product
        release = $release
        version = $Version
        build = (Normalize-Build $Build)
        target = "x64"
        notes = "Added to the per-user pyRevit host cache so pyrevit run can open models whose build is newer than pyRevit's bundled metadata."
    }
}

if (-not $Builds -or $Builds.Count -eq 0) {
    throw "At least one Revit build id must be supplied."
}

$normalizedRequestedBuilds = @($Builds | ForEach-Object {
    ([string] $_).Split(",", [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object {
        Normalize-Build $_
    }
} | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})

if (-not $normalizedRequestedBuilds -or $normalizedRequestedBuilds.Count -eq 0) {
    throw "At least one non-empty Revit build id must be supplied."
}

if ([string]::IsNullOrWhiteSpace($SourceHostsPath)) {
    $SourceHostsPath = Resolve-DefaultSourceHostsPath
}

if ([string]::IsNullOrWhiteSpace($CachePath)) {
    throw "CachePath cannot be empty."
}

$cacheFull = Get-FullPath $CachePath
$sourceFull = ""
if (-not [string]::IsNullOrWhiteSpace($SourceHostsPath)) {
    $sourceFull = Get-FullPath $SourceHostsPath
}

$merged = New-Object System.Collections.Generic.List[object]
$seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)

foreach ($entry in (Read-HostEntries $sourceFull) + (Read-HostEntries $cacheFull)) {
    $build = Normalize-Build $entry.build
    if ([string]::IsNullOrWhiteSpace($build)) {
        continue
    }

    if ($seen.Add($build)) {
        $entry.build = $build
        $merged.Add($entry) | Out-Null
    }
}

$added = New-Object System.Collections.Generic.List[string]
foreach ($normalized in $normalizedRequestedBuilds) {
    if ($seen.Add($normalized)) {
        $merged.Add((New-HostEntry $normalized)) | Out-Null
        $added.Add($normalized) | Out-Null
    }
}

if ($DryRun) {
    Write-Step "Would write pyRevit host cache: $cacheFull"
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cacheFull) | Out-Null
    Set-Content -LiteralPath $cacheFull -Value ($merged | ConvertTo-Json -Depth 8) -Encoding UTF8
    Write-Step "Wrote pyRevit host cache: $cacheFull"
}

$result = [ordered] @{
    status = "ok"
    dryRun = [bool] $DryRun
    sourceHostsPath = $sourceFull
    cachePath = $cacheFull
    totalEntries = $merged.Count
    requestedBuilds = @($normalizedRequestedBuilds)
    addedBuilds = @($added)
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    if ($added.Count -gt 0) {
        Write-Step "Added builds: $($added -join ', ')"
    } else {
        Write-Step "All requested builds were already present."
    }
}
