param(
    [string] $PackageRoot = "",
    [string] $PackageZipPath = "",
    [string] $OutputRoot = "",
    [string] $SigningSkipReason = "",
    [string] $LiveSmokeEvidencePath = "",
    [string] $LiveSmokeSkipReason = "",
    [string] $SupportBundlePath = "",
    [string] $SupportBundleSkipReason = "",
    [string] $HostedIntegrationEvidencePath = "",
    [string] $HostedIntegrationSkipReason = "",
    [string] $ValidateRepoLogPath = "",
    [string] $PackageLogPath = "",
    [string] $DoctorLogPath = "",
    [string] $SigningLogPath = "",
    [string[]] $CommandLogPaths = @(),
    [string[]] $AdditionalEvidencePaths = @(),
    [switch] $NoZip
)

$ErrorActionPreference = "Stop"
$RedactedLocalPath = "<redacted-local-path>"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next evidence] $Message"
}

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Expand-DelimitedPathList($Values) {
    $expanded = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        if ([string]::IsNullOrWhiteSpace([string] $value)) {
            continue
        }

        $valueText = [string] $value
        if (Test-Path -LiteralPath ([Environment]::ExpandEnvironmentVariables($valueText))) {
            $expanded.Add($valueText) | Out-Null
            continue
        }

        foreach ($part in ($valueText -split ";")) {
            if (-not [string]::IsNullOrWhiteSpace($part)) {
                $expanded.Add($part.Trim()) | Out-Null
            }
        }
    }

    return @($expanded.ToArray())
}

function Test-PotentialSecretText($Text) {
    $patterns = @(
        '(?i)REVIT_MCP_NEXT_AUTH_TOKEN\s*=\s*["'']?[A-Za-z0-9._~+/=-]{20,}["'']?',
        '(?i)["'']authToken["'']\s*:\s*["''][^"'']{20,}["'']',
        '(?i)Authorization\s*:\s*Bearer\s+[A-Za-z0-9._~+/=-]{20,}',
        '-----BEGIN [^-]*PRIVATE KEY-----'
    )

    foreach ($pattern in $patterns) {
        if ([regex]::IsMatch([string] $Text, $pattern)) {
            return $true
        }
    }

    return $false
}

function Redact-ShareableText($Text) {
    $result = [string] $Text
    $knownPaths = @(
        $packageRootFull,
        $packageZipFull,
        $env:USERPROFILE,
        $env:LOCALAPPDATA,
        $env:APPDATA,
        $env:TEMP,
        $env:TMP
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) } | Sort-Object Length -Descending -Unique

    foreach ($path in $knownPaths) {
        $textPath = [string] $path
        $result = [regex]::Replace($result, [regex]::Escape($textPath), $RedactedLocalPath, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $jsonEscapedPath = $textPath.Replace("\", "\\")
        if ($jsonEscapedPath -ne $textPath) {
            $result = [regex]::Replace($result, [regex]::Escape($jsonEscapedPath), $RedactedLocalPath, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }

    $result = [regex]::Replace($result, '(?i)(?<![A-Za-z0-9])[A-Z]:\\\\[^"\r\n]*', $RedactedLocalPath)
    $result = [regex]::Replace($result, '(?i)(?<![A-Za-z0-9])[A-Z]:\\[^\r\n"''<>|]*', $RedactedLocalPath)
    $result = [regex]::Replace($result, '\\\\\\\\[^"\r\n]+', $RedactedLocalPath)
    $result = [regex]::Replace($result, '\\\\[^\\\r\n"''<>|]+\\[^\r\n"''<>|]*', $RedactedLocalPath)
    return $result
}

function ConvertTo-ShareableReason($Value) {
    if ([string]::IsNullOrWhiteSpace([string] $Value)) {
        return [string] $Value
    }

    return Redact-ShareableText ([string] $Value)
}

function ConvertTo-ShareableObject($Value) {
    if ($null -eq $Value) {
        return $null
    }

    $json = $Value | ConvertTo-Json -Depth 32
    return (Redact-ShareableText $json) | ConvertFrom-Json
}

function Test-ShareableTextFile($Path) {
    $textExtensions = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(".cmd", ".env", ".json", ".log", ".md", ".ps1", ".sha256", ".toml", ".txt", ".xml", ".yaml", ".yml")) {
        $textExtensions.Add($extension) | Out-Null
    }

    $file = Get-Item -LiteralPath $Path
    return $textExtensions.Contains($file.Extension) -and $file.Length -le 5MB
}

function Redact-ShareableEvidenceFiles($Root) {
    if (-not (Test-Path -LiteralPath $Root)) {
        return
    }

    $files = if (Test-Path -LiteralPath $Root -PathType Leaf) {
        @(Get-Item -LiteralPath $Root)
    } else {
        @(Get-ChildItem -LiteralPath $Root -Recurse -File)
    }

    foreach ($file in $files) {
        if (-not (Test-ShareableTextFile $file.FullName)) {
            continue
        }

        try {
            $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        } catch {
            continue
        }

        Set-Content -LiteralPath $file.FullName -Value (Redact-ShareableText $text) -Encoding UTF8
    }
}

function Assert-NoSensitiveEvidence($Root) {
    $textExtensions = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(".cmd", ".env", ".json", ".log", ".md", ".ps1", ".sha256", ".toml", ".txt", ".xml", ".yaml", ".yml")) {
        $textExtensions.Add($extension) | Out-Null
    }

    $rootFull = Get-FullPath $Root
    $textFiles = Get-ChildItem -LiteralPath $rootFull -Recurse -File |
        Where-Object { $textExtensions.Contains($_.Extension) -and $_.Length -le 5MB }

    foreach ($file in $textFiles) {
        try {
            $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        } catch {
            continue
        }

        if (Test-PotentialSecretText $text) {
            $relativePath = (Get-RelativePath $rootFull $file.FullName) -replace "\\", "/"
            throw "Release evidence contains a potential raw secret in $relativePath. Redact the source evidence before collecting the bundle."
        }
    }
}

function Add-TrailingSeparator($Path) {
    if ($Path.EndsWith("\") -or $Path.EndsWith("/")) {
        return $Path
    }

    return "$Path\"
}

function Assert-PathChild($Root, $Path, $Label) {
    $rootFull = Get-FullPath $Root
    $pathFull = Get-FullPath $Path
    $rootWithSeparator = Add-TrailingSeparator $rootFull

    if ($pathFull -ne $rootFull -and -not $pathFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use $Label outside expected root. Root: $rootFull Target: $pathFull"
    }
}

function Get-RelativePath($Root, $Path) {
    $rootFull = Add-TrailingSeparator (Get-FullPath $Root)
    $pathFull = Get-FullPath $Path
    $rootUri = New-Object System.Uri($rootFull)
    $pathUri = New-Object System.Uri($pathFull)
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
}

function Get-Sha256Hash($Path) {
    $stream = [System.IO.File]::OpenRead((Get-FullPath $Path))
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
        } finally {
            $sha256.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Resolve-RequiredFile($Path, $Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Message Missing: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredDirectory($Path, $Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Message Missing: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredEvidencePath($Path, $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label path cannot be empty."
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (Test-Path -LiteralPath $expanded -PathType Leaf) {
        return (Resolve-Path -LiteralPath $expanded).Path
    }

    if (Test-Path -LiteralPath $expanded -PathType Container) {
        return (Resolve-Path -LiteralPath $expanded).Path
    }

    throw "$Label path was not found: $Path"
}

function Find-LatestPackageRoot($ReleaseRoot) {
    if (-not (Test-Path -LiteralPath $ReleaseRoot -PathType Container)) {
        throw "PackageRoot was not provided and default release root was not found: $ReleaseRoot"
    }

    $candidates = Get-ChildItem -LiteralPath $ReleaseRoot -Directory -Filter "revit-mcp-next-*-windows" |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "release-manifest.json") -PathType Leaf } |
        Sort-Object LastWriteTimeUtc -Descending

    $candidate = $candidates | Select-Object -First 1
    if (-not $candidate) {
        throw "PackageRoot was not provided and no staged release package was found under $ReleaseRoot."
    }

    return $candidate.FullName
}

function New-Directory($Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Copy-RequiredFile($Source, $Destination) {
    Resolve-RequiredFile $Source "Required evidence file was not found." | Out-Null
    New-Directory (Split-Path -Parent $Destination)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-EvidencePath($Source, $DestinationRoot, $Label) {
    $resolvedSource = Resolve-RequiredEvidencePath $Source $Label
    New-Directory $DestinationRoot

    if (Test-Path -LiteralPath $resolvedSource -PathType Leaf) {
        $destination = Join-Path $DestinationRoot (Split-Path -Leaf $resolvedSource)
        Copy-Item -LiteralPath $resolvedSource -Destination $destination -Force
        Redact-ShareableEvidenceFiles $destination
        return $destination
    }

    Get-ChildItem -LiteralPath $resolvedSource -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $DestinationRoot -Recurse -Force
    }
    Redact-ShareableEvidenceFiles $DestinationRoot
    return $DestinationRoot
}

function Copy-NamedEvidenceFile($Path, $DestinationRoot, $StoredName, $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered] @{
            present = $false
            sourcePath = $null
            sourcePathRedacted = $false
            storedAs = $null
            sha256 = $null
            size = $null
        }
    }

    $resolvedPath = Resolve-RequiredFile ([Environment]::ExpandEnvironmentVariables($Path)) "$Label file was not found."
    New-Directory $DestinationRoot
    $destination = Join-Path $DestinationRoot $StoredName
    Copy-Item -LiteralPath $resolvedPath -Destination $destination -Force
    Redact-ShareableEvidenceFiles $destination
    $file = Get-Item -LiteralPath $destination

    return [ordered] @{
        present = $true
        sourcePath = $RedactedLocalPath
        sourcePathRedacted = $true
        storedAs = ((Get-RelativePath $stageRoot $destination) -replace "\\", "/")
        sha256 = Get-Sha256Hash $destination
        size = $file.Length
    }
}

function Get-InventoryEntries($Root, [string[]] $ExcludeRelativePaths = @()) {
    $excluded = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($exclude in $ExcludeRelativePaths) {
        $excluded.Add(($exclude -replace "\\", "/")) | Out-Null
    }

    $entries = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $Root)) {
        return $entries
    }

    if (Test-Path -LiteralPath $Root -PathType Leaf) {
        $file = Get-Item -LiteralPath $Root
        if ($excluded.Contains($file.Name)) {
            return $entries
        }

        $entries.Add([ordered] @{
            path = $file.Name
            sha256 = Get-Sha256Hash $file.FullName
            size = $file.Length
        }) | Out-Null
        return $entries
    }

    $files = Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName
    foreach ($file in $files) {
        $relativePath = (Get-RelativePath $Root $file.FullName) -replace "\\", "/"
        if ($excluded.Contains($relativePath)) {
            continue
        }

        $entries.Add([ordered] @{
            path = $relativePath
            sha256 = Get-Sha256Hash $file.FullName
            size = $file.Length
        }) | Out-Null
    }

    return $entries
}

function Get-SafeFileName($Path, $Index) {
    $leaf = Split-Path -Leaf $Path
    if ([string]::IsNullOrWhiteSpace($leaf)) {
        $leaf = "evidence-$Index"
    }

    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    foreach ($char in $invalidChars) {
        $leaf = $leaf.Replace($char, "_")
    }

    if ($Index -le 0) {
        return $leaf
    }

    $extension = [System.IO.Path]::GetExtension($leaf)
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($leaf)
    return "$stem-$Index$extension"
}

function Copy-PathList($Paths, $DestinationRoot, $Label) {
    $copied = New-Object System.Collections.Generic.List[object]
    $index = 0
    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $resolvedPath = Resolve-RequiredEvidencePath $path $Label
        $safeName = Get-SafeFileName $resolvedPath $index
        $destination = Join-Path $DestinationRoot $safeName
        New-Directory $DestinationRoot

        if (Test-Path -LiteralPath $resolvedPath -PathType Leaf) {
            Copy-Item -LiteralPath $resolvedPath -Destination $destination -Force
            $inventoryRoot = $destination
        } else {
            New-Directory $destination
            Get-ChildItem -LiteralPath $resolvedPath -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
            }
            $inventoryRoot = $destination
        }
        Redact-ShareableEvidenceFiles $inventoryRoot

        $copied.Add([ordered] @{
            sourcePath = $RedactedLocalPath
            sourcePathRedacted = $true
            storedAs = ((Get-RelativePath $stageRoot $destination) -replace "\\", "/")
            files = Get-InventoryEntries $inventoryRoot
        }) | Out-Null
        $index++
    }

    return $copied
}

function Require-SkipOrEvidence($EvidencePath, $SkipReason, $Label) {
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($SkipReason)) {
        throw "$Label evidence was not provided. Pass evidence path or an explicit skip reason."
    }
}

function Resolve-LiveSmokeSummaryPath($EvidencePath) {
    $resolved = Resolve-RequiredEvidencePath $EvidencePath "Live Revit smoke evidence"
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        if ((Split-Path -Leaf $resolved) -ieq "smoke-summary.json") {
            return $resolved
        }

        throw "Live Revit smoke evidence file must be smoke-summary.json. Received: $resolved"
    }

    $direct = Join-Path $resolved "smoke-summary.json"
    if (Test-Path -LiteralPath $direct -PathType Leaf) {
        return (Resolve-Path -LiteralPath $direct).Path
    }

    $candidates = @(Get-ChildItem -LiteralPath $resolved -Recurse -Filter "smoke-summary.json" -File -ErrorAction SilentlyContinue |
        Sort-Object FullName)
    if ($candidates.Count -eq 1) {
        return $candidates[0].FullName
    }

    if ($candidates.Count -gt 1) {
        throw "Live Revit smoke evidence contains multiple smoke-summary.json files. Pass the exact summary file or exact run directory: $resolved"
    }

    throw "Live Revit smoke evidence must include smoke-summary.json with status=passed. Missing under: $resolved"
}

function Get-StringArray($Value) {
    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        return @($Value | ForEach-Object { [string] $_ })
    }

    return @([string] $Value)
}

function Test-LiveSmokeOperationAttempted($Summary, [string] $OperationType) {
    $coveredOperations = Get-StringArray $Summary.coveredOperations
    if ($coveredOperations -contains $OperationType) {
        return $true
    }

    foreach ($skipped in @($Summary.skippedOperations)) {
        if ($null -eq $skipped) {
            continue
        }

        if ($skipped -is [string] -and [string] $skipped -eq $OperationType) {
            return $true
        }

        if ([string] $skipped.type -eq $OperationType) {
            return $true
        }
    }

    return $false
}

function Assert-LiveSmokeOperationAttempted($Summary, [string] $OperationType, [string] $SummaryPath) {
    if (-not (Test-LiveSmokeOperationAttempted $Summary $OperationType)) {
        throw "Live Revit smoke summary must cover or explicitly skip '$OperationType'. Summary: $SummaryPath"
    }
}

function Read-PassedLiveSmokeSummary($EvidencePath) {
    $summaryPath = Resolve-LiveSmokeSummaryPath $EvidencePath
    $summary = Read-JsonFile $summaryPath
    if ([string] $summary.status -ne "passed") {
        throw "Live Revit smoke summary did not pass. Status: $($summary.status). Summary: $summaryPath"
    }

    if ($summary.schemaVersion -ne 1) {
        throw "Live Revit smoke summary has unexpected schemaVersion: $($summary.schemaVersion). Summary: $summaryPath"
    }

    Assert-LiveSmokeOperationAttempted $summary "tag_room" $summaryPath
    Assert-LiveSmokeOperationAttempted $summary "tag_element" $summaryPath

    return [ordered] @{
        path = $summaryPath
        data = $summary
    }
}

function Resolve-HostedIntegrationSummaryPath($EvidencePath) {
    $resolved = Resolve-RequiredEvidencePath $EvidencePath "Hosted pyRevit/Dynamo integration smoke evidence"
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        if ((Split-Path -Leaf $resolved) -ieq "host-integrations-summary.json") {
            return $resolved
        }

        throw "Hosted pyRevit/Dynamo integration smoke evidence file must be host-integrations-summary.json. Received: $resolved"
    }

    $direct = Join-Path $resolved "host-integrations-summary.json"
    if (Test-Path -LiteralPath $direct -PathType Leaf) {
        return (Resolve-Path -LiteralPath $direct).Path
    }

    $candidates = @(Get-ChildItem -LiteralPath $resolved -Recurse -Filter "host-integrations-summary.json" -File -ErrorAction SilentlyContinue |
        Sort-Object FullName)
    if ($candidates.Count -eq 1) {
        return $candidates[0].FullName
    }

    if ($candidates.Count -gt 1) {
        throw "Hosted pyRevit/Dynamo integration smoke evidence contains multiple host-integrations-summary.json files. Pass the exact summary file or exact run directory: $resolved"
    }

    throw "Hosted pyRevit/Dynamo integration smoke evidence must include host-integrations-summary.json with status=passed. Missing under: $resolved"
}

function Get-RequiredHostSummary($Summary, $HostName, $SummaryPath) {
    if (-not $Summary.hosts) {
        throw "Hosted pyRevit/Dynamo integration summary is missing hosts. Summary: $SummaryPath"
    }

    $hostProperty = $Summary.hosts.PSObject.Properties[$HostName]
    if (-not $hostProperty) {
        throw "Hosted pyRevit/Dynamo integration summary is missing host '$HostName'. Summary: $SummaryPath"
    }

    $hostSummary = $hostProperty.Value
    if ([string] $hostSummary.status -ne "passed") {
        throw "Hosted pyRevit/Dynamo integration host '$HostName' did not pass. Status: $($hostSummary.status). Summary: $SummaryPath"
    }

    if ($null -ne $hostSummary.previewReady -and [bool] $hostSummary.previewReady -ne $true) {
        throw "Hosted pyRevit/Dynamo integration host '$HostName' did not report previewReady=true. Summary: $SummaryPath"
    }

    return $hostSummary
}

function Resolve-HostedIntegrationSiblingFile($SummaryPath, $RelativePath, $Label) {
    if ([string]::IsNullOrWhiteSpace([string] $RelativePath)) {
        throw "$Label path is missing from hosted integration summary. Summary: $SummaryPath"
    }

    if ([System.IO.Path]::IsPathRooted([string] $RelativePath)) {
        throw "$Label path must be relative to host-integrations-summary.json. Summary: $SummaryPath Path: $RelativePath"
    }

    $summaryRoot = Split-Path -Parent $SummaryPath
    $candidate = Join-Path $summaryRoot (([string] $RelativePath) -replace "/", "\")
    $resolved = Resolve-RequiredFile $candidate "$Label referenced by hosted integration summary was not found."
    Assert-PathChild $summaryRoot $resolved $Label
    return $resolved
}

function Assert-HostedRawEvidence($RawEvidence, $ExpectedHost, $ExpectedHostSummary, $Path) {
    if ($RawEvidence.schemaVersion -ne 1) {
        throw "$ExpectedHost raw hosted evidence has unexpected schemaVersion: $($RawEvidence.schemaVersion). File: $Path"
    }

    if ([string] $RawEvidence.host -ne $ExpectedHost) {
        throw "$ExpectedHost raw hosted evidence has unexpected host '$($RawEvidence.host)'. File: $Path"
    }

    if ([string] $RawEvidence.status -ne "passed") {
        throw "$ExpectedHost raw hosted evidence did not pass. Status: $($RawEvidence.status). File: $Path"
    }

    if ([bool] $RawEvidence.previewReady -ne $true -or [bool] $RawEvidence.applyWrites -ne $true) {
        throw "$ExpectedHost raw hosted evidence must record previewReady=true and applyWrites=true. File: $Path"
    }

    $coveredOperations = Get-StringArray $RawEvidence.coveredOperations
    if (-not ($coveredOperations -contains "create_level")) {
        throw "$ExpectedHost raw hosted evidence did not cover create_level. File: $Path"
    }

    if ((Get-StringArray $RawEvidence.createdElementIds).Count -lt 1) {
        throw "$ExpectedHost raw hosted evidence did not record createdElementIds. File: $Path"
    }

    if ([string]::IsNullOrWhiteSpace([string] $RawEvidence.activeDocument.fingerprint)) {
        throw "$ExpectedHost raw hosted evidence did not record activeDocument.fingerprint. File: $Path"
    }

    if ($null -eq $RawEvidence.inProcessBridge -or [bool] $RawEvidence.inProcessBridge.addinHandlerActive -ne $true) {
        throw "$ExpectedHost raw hosted evidence did not prove configured add-in in-process bridge usage. File: $Path"
    }

    $rawSha = ([string] $RawEvidence.inProcessBridge.assemblySha256).Trim().ToLowerInvariant()
    $summarySha = ([string] $ExpectedHostSummary.inProcessBridge.assemblySha256).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($rawSha) -or -not [string]::Equals($rawSha, $summarySha, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$ExpectedHost raw hosted evidence add-in SHA-256 does not match the hosted integration summary. File: $Path"
    }
}

function Assert-DynamoPreflightRawEvidence($Preflight, $Path) {
    if ($Preflight.schemaVersion -ne 1) {
        throw "Dynamo preflight evidence has unexpected schemaVersion: $($Preflight.schemaVersion). File: $Path"
    }

    if ([string] $Preflight.status -ne "preflight") {
        throw "Dynamo preflight evidence has unexpected status '$($Preflight.status)'. File: $Path"
    }

    if ([bool] $Preflight.privacySettingsChanged -ne $false -or [bool] $Preflight.privacyPromptAutomation -ne $false -or [bool] $Preflight.uiPromptAutomation -ne $false) {
        throw "Dynamo preflight evidence must prove privacySettingsChanged=false, privacyPromptAutomation=false, and uiPromptAutomation=false. File: $Path"
    }

    if ([string]::IsNullOrWhiteSpace([string] $Preflight.graphPath) -or [string]::IsNullOrWhiteSpace([string] $Preflight.installRoot)) {
        throw "Dynamo preflight evidence must record graphPath and installRoot. File: $Path"
    }
}

function Assert-HostedIntegrationRawEvidenceFiles($SummaryPath, $Summary, $PyRevitHostSummary, $DynamoHostSummary) {
    $pyRevitPath = Resolve-HostedIntegrationSiblingFile $SummaryPath $PyRevitHostSummary.evidencePath "pyRevit raw hosted evidence"
    $dynamoPath = Resolve-HostedIntegrationSiblingFile $SummaryPath $DynamoHostSummary.evidencePath "Dynamo raw hosted evidence"
    if ($null -eq $Summary.dynamoPreflight) {
        throw "Hosted pyRevit/Dynamo integration summary is missing dynamoPreflight. Summary: $SummaryPath"
    }

    $dynamoPreflightPath = Resolve-HostedIntegrationSiblingFile $SummaryPath $Summary.dynamoPreflight.evidencePath "Dynamo preflight evidence"

    Assert-HostedRawEvidence (Read-JsonFile $pyRevitPath) "pyrevit" $PyRevitHostSummary $pyRevitPath
    Assert-HostedRawEvidence (Read-JsonFile $dynamoPath) "dynamo" $DynamoHostSummary $dynamoPath
    Assert-DynamoPreflightRawEvidence (Read-JsonFile $dynamoPreflightPath) $dynamoPreflightPath

    return [ordered] @{
        pyrevit = $pyRevitPath
        dynamo = $dynamoPath
        dynamoPreflight = $dynamoPreflightPath
        bundleRoot = Split-Path -Parent $SummaryPath
    }
}

function Get-PackageContentEntry($ReleaseManifest, $RelativePath) {
    $normalizedRelativePath = $RelativePath -replace "\\", "/"
    foreach ($entry in @($ReleaseManifest.contents)) {
        if ([string]::Equals(([string] $entry.path), $normalizedRelativePath, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $entry
        }
    }

    throw "Release manifest is missing package content entry: $normalizedRelativePath"
}

function Get-PackagedAddinIdentity($ReleaseManifest) {
    $relativePath = "payload/addin/RevitMcpNext.Addin.dll"
    $entry = Get-PackageContentEntry $ReleaseManifest $relativePath
    $sha256 = ([string] $entry.sha256).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($sha256)) {
        throw "Release manifest content entry '$relativePath' is missing sha256."
    }

    return [ordered] @{
        packagePath = $relativePath
        sha256 = $sha256
        size = $entry.size
    }
}

function Assert-HostLoadedPackageAddin($HostSummary, $HostName, $ExpectedAddinIdentity, $SummaryPath) {
    $bridge = $HostSummary.inProcessBridge
    if ($null -eq $bridge) {
        throw "Hosted pyRevit/Dynamo integration host '$HostName' is missing inProcessBridge identity. Summary: $SummaryPath"
    }

    $actualSha256 = ([string] $bridge.assemblySha256).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($actualSha256)) {
        throw "Hosted pyRevit/Dynamo integration host '$HostName' did not record inProcessBridge.assemblySha256. Summary: $SummaryPath"
    }

    if (-not [string]::Equals($actualSha256, [string] $ExpectedAddinIdentity.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Hosted pyRevit/Dynamo integration host '$HostName' loaded add-in SHA-256 $actualSha256, expected packaged $($ExpectedAddinIdentity.sha256). Summary: $SummaryPath"
    }

    return [ordered] @{
        assemblyPath = $RedactedLocalPath
        assemblyPathRedacted = $true
        assemblySha256 = $actualSha256
        fileVersion = [string] $bridge.fileVersion
        productVersion = [string] $bridge.productVersion
    }
}

function Assert-LiveSmokePackageIdentity($LiveSmokeSummary, $ExpectedAddinIdentity) {
    $summaryPath = $LiveSmokeSummary.path
    $addinAssembly = $LiveSmokeSummary.data.addinAssembly
    if ($null -eq $addinAssembly) {
        throw "Live Revit smoke summary is missing addinAssembly identity. Rerun smoke with a build that records revit.status addinAssembly. Summary: $summaryPath"
    }

    $actualSha256 = ([string] $addinAssembly.assemblySha256).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($actualSha256)) {
        throw "Live Revit smoke summary did not record addinAssembly.assemblySha256. Summary: $summaryPath"
    }

    if (-not [string]::Equals($actualSha256, [string] $ExpectedAddinIdentity.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Live Revit smoke loaded add-in SHA-256 $actualSha256, expected packaged $($ExpectedAddinIdentity.sha256). Summary: $summaryPath"
    }

    return [ordered] @{
        expectedPackagePath = [string] $ExpectedAddinIdentity.packagePath
        expectedSha256 = [string] $ExpectedAddinIdentity.sha256
        assemblyPath = $RedactedLocalPath
        assemblyPathRedacted = $true
        assemblySha256 = $actualSha256
        fileVersion = [string] $addinAssembly.fileVersion
        productVersion = [string] $addinAssembly.productVersion
    }
}

function Assert-HostedIntegrationPackageIdentity($HostedIntegrationSummary, $ExpectedAddinIdentity) {
    return [ordered] @{
        expectedPackagePath = [string] $ExpectedAddinIdentity.packagePath
        expectedSha256 = [string] $ExpectedAddinIdentity.sha256
        hosts = [ordered] @{
            pyrevit = (Assert-HostLoadedPackageAddin $HostedIntegrationSummary.hosts.pyrevit "pyrevit" $ExpectedAddinIdentity $HostedIntegrationSummary.path)
            dynamo = (Assert-HostLoadedPackageAddin $HostedIntegrationSummary.hosts.dynamo "dynamo" $ExpectedAddinIdentity $HostedIntegrationSummary.path)
        }
    }
}

function Read-PassedHostedIntegrationSummary($EvidencePath) {
    $summaryPath = Resolve-HostedIntegrationSummaryPath $EvidencePath
    $summary = Read-JsonFile $summaryPath
    if ([string] $summary.status -ne "passed") {
        throw "Hosted pyRevit/Dynamo integration summary did not pass. Status: $($summary.status). Summary: $summaryPath"
    }

    if ($summary.schemaVersion -ne 1) {
        throw "Hosted pyRevit/Dynamo integration summary has unexpected schemaVersion: $($summary.schemaVersion). Summary: $summaryPath"
    }

    $pyRevit = Get-RequiredHostSummary $summary "pyrevit" $summaryPath
    $dynamo = Get-RequiredHostSummary $summary "dynamo" $summaryPath
    $rawEvidence = Assert-HostedIntegrationRawEvidenceFiles $summaryPath $summary $pyRevit $dynamo

    return [ordered] @{
        path = $summaryPath
        data = $summary
        rawEvidence = $rawEvidence
        hosts = [ordered] @{
            pyrevit = $pyRevit
            dynamo = $dynamo
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release-evidence"
}

if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Find-LatestPackageRoot (Join-Path $repoRoot "artifacts\release")
}

$packageRootFull = Resolve-RequiredDirectory $PackageRoot "Package root was not found."
$releaseManifestPath = Resolve-RequiredFile (Join-Path $packageRootFull "release-manifest.json") "Release manifest is missing."
$checksumsPath = Resolve-RequiredFile (Join-Path $packageRootFull "CHECKSUMS.sha256") "Package checksum file is missing."
$sharingNoticePath = Resolve-RequiredFile (Join-Path $packageRootFull "SHARING-NOTICE.md") "Package sharing notice is missing."

if ([string]::IsNullOrWhiteSpace($PackageZipPath)) {
    $PackageZipPath = "$packageRootFull.zip"
}
$packageZipFull = Resolve-RequiredFile $PackageZipPath "Package zip is missing."

Require-SkipOrEvidence $LiveSmokeEvidencePath $LiveSmokeSkipReason "Live Revit smoke"
Require-SkipOrEvidence $SupportBundlePath $SupportBundleSkipReason "Support bundle"
Require-SkipOrEvidence $HostedIntegrationEvidencePath $HostedIntegrationSkipReason "Hosted pyRevit/Dynamo integration smoke"

$releaseManifest = Read-JsonFile $releaseManifestPath
$version = [string] $releaseManifest.package.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Release manifest package.version is missing."
}
$packagedAddinIdentity = Get-PackagedAddinIdentity $releaseManifest

$platform = [string] $releaseManifest.package.platform
if ([string]::IsNullOrWhiteSpace($platform)) {
    $platform = "windows"
}

$outputRootFull = Get-FullPath $OutputRoot
New-Directory $outputRootFull

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$evidenceName = "revit-mcp-next-$version-$platform-evidence-$timestamp-$runId"
$stageRoot = Join-Path $outputRootFull $evidenceName
$bundleZipPath = "$stageRoot.zip"
Assert-PathChild $outputRootFull $stageRoot "release evidence root"
Assert-PathChild $outputRootFull $bundleZipPath "release evidence zip"
New-Directory $stageRoot

Write-Step "Collecting release evidence: $stageRoot"

$packageEvidenceRoot = Join-Path $stageRoot "package"
Copy-RequiredFile $releaseManifestPath (Join-Path $packageEvidenceRoot "release-manifest.json")
Copy-RequiredFile $checksumsPath (Join-Path $packageEvidenceRoot "CHECKSUMS.sha256")
Copy-RequiredFile $sharingNoticePath (Join-Path $packageEvidenceRoot "SHARING-NOTICE.md")

$packageZip = Get-Item -LiteralPath $packageZipFull
$packageZipHash = Get-Sha256Hash $packageZip.FullName
$packageZipHashLine = "$packageZipHash  $($packageZip.Name)"
Set-Content -LiteralPath (Join-Path $packageEvidenceRoot "package-zip.sha256") -Value $packageZipHashLine -Encoding ASCII

$shareableSigningSkipReason = ConvertTo-ShareableReason $SigningSkipReason
$shareableLiveSmokeSkipReason = ConvertTo-ShareableReason $LiveSmokeSkipReason
$shareableSupportBundleSkipReason = ConvertTo-ShareableReason $SupportBundleSkipReason
$shareableHostedIntegrationSkipReason = ConvertTo-ShareableReason $HostedIntegrationSkipReason

$signingRequested = [bool] $releaseManifest.signing.requested
$signingStatus = "captured"
$effectiveSigningSkipReason = $shareableSigningSkipReason
if (-not $signingRequested) {
    if ([string]::IsNullOrWhiteSpace($effectiveSigningSkipReason)) {
        throw "Signing was not requested for this build. Pass -SigningSkipReason to make that release evidence explicit."
    }

    $signingStatus = "skipped"
} elseif ([string]::IsNullOrWhiteSpace($SigningLogPath)) {
    throw "Signing was requested in release-manifest.json. Pass -SigningLogPath with signing verification output."
}

$liveSmokeSection = [ordered] @{
    status = "skipped"
    sourcePath = $null
    sourcePathRedacted = $false
    skipReason = $shareableLiveSmokeSkipReason
    summary = $null
    files = @()
}
if (-not [string]::IsNullOrWhiteSpace($LiveSmokeEvidencePath)) {
    $liveSmokeSummary = Read-PassedLiveSmokeSummary $LiveSmokeEvidencePath
    $liveSmokePackageIdentity = Assert-LiveSmokePackageIdentity $liveSmokeSummary $packagedAddinIdentity
    $liveSmokeRoot = Join-Path $stageRoot "live-smoke"
    $copiedLiveSmoke = Copy-EvidencePath $LiveSmokeEvidencePath $liveSmokeRoot "Live Revit smoke evidence"
    $liveSmokeSection = [ordered] @{
        status = "captured"
        sourcePath = $RedactedLocalPath
        sourcePathRedacted = $true
        storedAs = ((Get-RelativePath $stageRoot $liveSmokeRoot) -replace "\\", "/")
        skipReason = $null
        summary = [ordered] @{
            sourcePath = $RedactedLocalPath
            sourcePathRedacted = $true
            status = [string] $liveSmokeSummary.data.status
            evidenceKind = [string] $liveSmokeSummary.data.evidenceKind
            synthetic = [bool] $liveSmokeSummary.data.synthetic
            mode = [string] $liveSmokeSummary.data.mode
            expectedRevitYear = $liveSmokeSummary.data.expectedRevitYear
            revit = ConvertTo-ShareableObject $liveSmokeSummary.data.revit
            activeDocument = ConvertTo-ShareableObject $liveSmokeSummary.data.activeDocument
            documentFingerprint = $liveSmokeSummary.data.documentFingerprint
            addinAssembly = ConvertTo-ShareableObject $liveSmokeSummary.data.addinAssembly
            packageIdentity = $liveSmokePackageIdentity
            operationKindGuard = $liveSmokeSummary.data.operationKindGuard
            requiredCoverage = $liveSmokeSummary.data.requiredCoverage
            tagSelectors = $liveSmokeSummary.data.tagSelectors
            tagCoverage = $liveSmokeSummary.data.result.tagCoverage
            coveredTools = $liveSmokeSummary.data.coveredTools
            coveredOperations = $liveSmokeSummary.data.coveredOperations
            skippedOperations = $liveSmokeSummary.data.skippedOperations
        }
        files = Get-InventoryEntries $copiedLiveSmoke
    }
}

$supportSection = [ordered] @{
    status = "skipped"
    sourcePath = $null
    sourcePathRedacted = $false
    skipReason = $shareableSupportBundleSkipReason
    files = @()
}
if (-not [string]::IsNullOrWhiteSpace($SupportBundlePath)) {
    $supportRoot = Join-Path $stageRoot "support"
    $copiedSupport = Copy-EvidencePath $SupportBundlePath $supportRoot "Support bundle evidence"
    $supportSection = [ordered] @{
        status = "captured"
        sourcePath = $RedactedLocalPath
        sourcePathRedacted = $true
        storedAs = ((Get-RelativePath $stageRoot $supportRoot) -replace "\\", "/")
        skipReason = $null
        files = Get-InventoryEntries $copiedSupport
    }
}

$hostedIntegrationSection = [ordered] @{
    status = "skipped"
    sourcePath = $null
    sourcePathRedacted = $false
    skipReason = $shareableHostedIntegrationSkipReason
    summary = $null
    files = @()
}
if (-not [string]::IsNullOrWhiteSpace($HostedIntegrationEvidencePath)) {
    $hostedIntegrationSummary = Read-PassedHostedIntegrationSummary $HostedIntegrationEvidencePath
    $hostedIntegrationPackageIdentity = Assert-HostedIntegrationPackageIdentity $hostedIntegrationSummary $packagedAddinIdentity
    $hostedIntegrationRoot = Join-Path $stageRoot "host-integrations"
    $hostedIntegrationBundleRoot = [string] $hostedIntegrationSummary.rawEvidence.bundleRoot
    $copiedHostedIntegration = Copy-EvidencePath $hostedIntegrationBundleRoot $hostedIntegrationRoot "Hosted pyRevit/Dynamo integration smoke evidence"
    $hostedIntegrationSection = [ordered] @{
        status = "captured"
        sourcePath = $RedactedLocalPath
        sourcePathRedacted = $true
        storedAs = ((Get-RelativePath $stageRoot $hostedIntegrationRoot) -replace "\\", "/")
        skipReason = $null
        summary = [ordered] @{
            sourcePath = $RedactedLocalPath
            sourcePathRedacted = $true
            status = [string] $hostedIntegrationSummary.data.status
            evidenceKind = [string] $hostedIntegrationSummary.data.evidenceKind
            synthetic = [bool] $hostedIntegrationSummary.data.synthetic
            hosts = [ordered] @{
                pyrevit = ConvertTo-ShareableObject $hostedIntegrationSummary.hosts.pyrevit
                dynamo = ConvertTo-ShareableObject $hostedIntegrationSummary.hosts.dynamo
            }
            dynamoPreflight = ConvertTo-ShareableObject $hostedIntegrationSummary.data.dynamoPreflight
            rawEvidence = [ordered] @{
                sourcePathRedacted = $true
                pyrevitSourcePath = $RedactedLocalPath
                dynamoSourcePath = $RedactedLocalPath
                dynamoPreflightSourcePath = $RedactedLocalPath
            }
            packageIdentity = $hostedIntegrationPackageIdentity
        }
        files = Get-InventoryEntries $copiedHostedIntegration
    }
}

$commandLogs = Copy-PathList (Expand-DelimitedPathList $CommandLogPaths) (Join-Path $stageRoot "command-logs") "Command log evidence"
$additionalEvidence = Copy-PathList (Expand-DelimitedPathList $AdditionalEvidencePaths) (Join-Path $stageRoot "additional") "Additional evidence"
$validationRoot = Join-Path $stageRoot "validation"
$validationSection = [ordered] @{
    validateRepoLog = Copy-NamedEvidenceFile $ValidateRepoLogPath $validationRoot "validate-repo.log" "validate-repo"
    packageLog = Copy-NamedEvidenceFile $PackageLogPath $validationRoot "package-release.log" "package-release"
    doctorLog = Copy-NamedEvidenceFile $DoctorLogPath $validationRoot "doctor-windows.log" "doctor"
}
$signingLog = Copy-NamedEvidenceFile $SigningLogPath (Join-Path $stageRoot "signing") "signing.log" "signing"

$packageSummary = [ordered] @{
    sourcePathRedacted = $true
    packageRoot = $RedactedLocalPath
    packageZipPath = $packageZip.Name
    packageZipName = $packageZip.Name
    packageZipSha256 = $packageZipHash
    packageZipSize = $packageZip.Length
    packagedAddin = $packagedAddinIdentity
    releaseManifest = "package/release-manifest.json"
    checksums = "package/CHECKSUMS.sha256"
    packageZipChecksum = "package/package-zip.sha256"
    sharingNotice = "package/SHARING-NOTICE.md"
}

$evidenceManifest = [ordered] @{
    schemaVersion = 1
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    generatedBy = "scripts/collect-release-evidence.ps1"
    package = [ordered] @{
        name = [string] $releaseManifest.package.name
        version = $version
        platform = $platform
        createdAtUtc = [string] $releaseManifest.package.createdAtUtc
        gitCommit = [string] $releaseManifest.package.gitCommit
        gitDirty = [bool] $releaseManifest.package.gitDirty
        revitYears = $releaseManifest.package.revitYears
        nodeMajor = $releaseManifest.package.nodeMajor
        nodeModulesBundled = [bool] $releaseManifest.package.nodeModulesBundled
        sharing = ConvertTo-ShareableObject $releaseManifest.sharing
        evidence = $packageSummary
    }
    signing = [ordered] @{
        status = $signingStatus
        skipReason = $effectiveSigningSkipReason
        requested = $signingRequested
        requireSigned = [bool] $releaseManifest.signing.requireSigned
        requireTrusted = [bool] $releaseManifest.signing.requireTrusted
        timestampServer = [string] $releaseManifest.signing.timestampServer
        log = $signingLog
        targets = $releaseManifest.signing.targets
    }
    validation = $validationSection
    liveSmoke = $liveSmokeSection
    supportBundle = $supportSection
    hostedIntegrations = $hostedIntegrationSection
    commandLogs = $commandLogs
    additionalEvidence = $additionalEvidence
    contents = @()
}

$summaryLines = @(
    "# Revit MCP Next Release Evidence",
    "",
    "- Package: revit-mcp-next $version ($platform)",
    "- Package zip: $($packageZip.Name)",
    "- Package zip SHA-256: $packageZipHash",
    "- Git commit: $($releaseManifest.package.gitCommit)",
    "- Git dirty: $($releaseManifest.package.gitDirty)",
    "- Share profile: $($releaseManifest.sharing.shareProfile)",
    "- Signing mode: $($releaseManifest.sharing.signingMode)",
    "- Signing: $signingStatus",
    "- Live smoke: $($liveSmokeSection.status)",
    "- Support bundle: $($supportSection.status)",
    "- Hosted pyRevit/Dynamo integrations: $($hostedIntegrationSection.status)"
)

if ($signingStatus -eq "skipped") {
    $summaryLines += "- Signing skip reason: $effectiveSigningSkipReason"
}
if ($liveSmokeSection.status -eq "skipped") {
    $summaryLines += "- Live smoke skip reason: $shareableLiveSmokeSkipReason"
}
if ($supportSection.status -eq "skipped") {
    $summaryLines += "- Support bundle skip reason: $shareableSupportBundleSkipReason"
}
if ($hostedIntegrationSection.status -eq "skipped") {
    $summaryLines += "- Hosted pyRevit/Dynamo integrations skip reason: $shareableHostedIntegrationSkipReason"
}

$summaryLines += @(
    "",
    "See release-evidence-manifest.json for the complete evidence inventory."
)

Set-Content -LiteralPath (Join-Path $stageRoot "release-evidence-summary.md") -Value $summaryLines -Encoding UTF8

$evidenceManifest["contents"] = Get-InventoryEntries $stageRoot @("release-evidence-manifest.json")
Set-Content -LiteralPath (Join-Path $stageRoot "release-evidence-manifest.json") -Value ($evidenceManifest | ConvertTo-Json -Depth 12) -Encoding UTF8
Assert-NoSensitiveEvidence $stageRoot

if (-not $NoZip) {
    Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $bundleZipPath -Force
    Write-Step "Created release evidence zip: $bundleZipPath"
}

Write-Step "Created release evidence: $stageRoot"
