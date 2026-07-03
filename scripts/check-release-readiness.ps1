param(
    [Parameter(Mandatory = $true)]
    [string] $EvidencePath,
    [ValidateSet("external-preview", "release-candidate", "production")]
    [string] $Profile = "external-preview",
    [switch] $AllowDirty,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Get-FullPath($Path) {
    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function New-TempDirectory() {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) "revit-mcp-next-readiness"
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $path = Join-Path $root ([Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Resolve-EvidenceManifest($Path) {
    $fullPath = Get-FullPath $Path
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw "Evidence path was not found: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $manifest = Join-Path $fullPath "release-evidence-manifest.json"
        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
            return @{ Root = $fullPath; ManifestPath = $manifest; TempRoot = $null }
        }

        $manifests = @(Get-ChildItem -LiteralPath $fullPath -Recurse -Filter "release-evidence-manifest.json" -File)
        if ($manifests.Count -eq 1) {
            return @{ Root = (Split-Path -Parent $manifests[0].FullName); ManifestPath = $manifests[0].FullName; TempRoot = $null }
        }

        if ($manifests.Count -eq 0) {
            throw "Evidence directory does not contain release-evidence-manifest.json: $fullPath"
        }

        throw "Evidence directory contains multiple release-evidence-manifest.json files; pass the exact package evidence directory or manifest path: $fullPath"
    }

    if ([System.IO.Path]::GetFileName($fullPath) -ieq "release-evidence-manifest.json") {
        return @{ Root = (Split-Path -Parent $fullPath); ManifestPath = $fullPath; TempRoot = $null }
    }

    if ([System.IO.Path]::GetExtension($fullPath) -ieq ".zip") {
        $tempRoot = New-TempDirectory
        Expand-Archive -LiteralPath $fullPath -DestinationPath $tempRoot -Force
        $manifests = @(Get-ChildItem -LiteralPath $tempRoot -Recurse -Filter "release-evidence-manifest.json" -File)
        if ($manifests.Count -eq 1) {
            return @{ Root = (Split-Path -Parent $manifests[0].FullName); ManifestPath = $manifests[0].FullName; TempRoot = $tempRoot }
        }

        if ($manifests.Count -eq 0) {
            throw "Evidence zip does not contain release-evidence-manifest.json: $fullPath"
        }

        throw "Evidence zip contains multiple release-evidence-manifest.json files; pass the exact package evidence zip: $fullPath"
    }

    throw "EvidencePath must be a release evidence directory, release-evidence-manifest.json, or release evidence .zip."
}

function Test-Blank($Value) {
    return [string]::IsNullOrWhiteSpace([string] $Value)
}

function Test-HexSha256($Value) {
    return [string] $Value -match "^[a-f0-9]{64}$"
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

function Test-SafeRelativePath($Path) {
    if (Test-Blank $Path) {
        return $false
    }

    if ([System.IO.Path]::IsPathRooted([string] $Path)) {
        return $false
    }

    foreach ($part in ([string] $Path -split "[/\\]+")) {
        if ($part -eq "..") {
            return $false
        }
    }

    return $true
}

function Join-EvidencePath($Root, $RelativePath) {
    return Join-Path $Root (([string] $RelativePath) -replace "/", "\")
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

$checks = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

function Add-Check($Status, $Name, $Message) {
    $checks.Add([ordered] @{
        status = $Status
        name = $Name
        message = $Message
    }) | Out-Null
    if ($Status -eq "fail") {
        $failures.Add($Message) | Out-Null
    } elseif ($Status -eq "warn") {
        $warnings.Add($Message) | Out-Null
    }
}

function Pass($Name, $Message) { Add-Check "pass" $Name $Message }
function Warn($Name, $Message) { Add-Check "warn" $Name $Message }
function Fail($Name, $Message) { Add-Check "fail" $Name $Message }

function Require-NonBlank($Name, $Value, $Message) {
    if (Test-Blank $Value) {
        Fail $Name $Message
    } else {
        Pass $Name "$Name is present."
    }
}

function Test-Inventory($Manifest, $Root) {
    $inventory = New-Object "System.Collections.Generic.Dictionary[string,object]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Manifest.contents)) {
        $relativePath = [string] $entry.path
        if (-not (Test-SafeRelativePath $relativePath)) {
            Fail "contents.inventory" "Evidence inventory contains an unsafe path: $relativePath"
            continue
        }

        if ($inventory.ContainsKey($relativePath)) {
            Fail "contents.inventory" "Evidence inventory contains duplicate path: $relativePath"
            continue
        }

        $inventory.Add($relativePath, $entry)
        $filePath = Join-EvidencePath $Root $relativePath
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            Fail "contents.inventory" "Evidence inventory file is missing: $relativePath"
            continue
        }

        $file = Get-Item -LiteralPath $filePath
        if ($null -ne $entry.size -and [long] $entry.size -ne [long] $file.Length) {
            Fail "contents.inventory" "Evidence inventory size mismatch for $relativePath."
        }

        if (Test-HexSha256 $entry.sha256) {
            $actualHash = Get-Sha256Hash $filePath
            if ($actualHash -ne [string] $entry.sha256) {
                Fail "contents.inventory" "Evidence inventory SHA-256 mismatch for $relativePath."
            }
        } else {
            Fail "contents.inventory" "Evidence inventory SHA-256 is missing or malformed for $relativePath."
        }
    }

    if ($inventory.Count -gt 0) {
        Pass "contents.inventory" "Evidence inventory files exist and match recorded hashes."
    } else {
        Fail "contents.inventory" "Evidence inventory is empty."
    }

    return $inventory
}

function Test-NoSensitiveEvidence($Root) {
    $textExtensions = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(".cmd", ".env", ".json", ".log", ".md", ".ps1", ".sha256", ".toml", ".txt", ".xml", ".yaml", ".yml")) {
        $textExtensions.Add($extension) | Out-Null
    }

    $offendingPath = $null
    $textFiles = Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object { $textExtensions.Contains($_.Extension) -and $_.Length -le 5MB }
    foreach ($file in $textFiles) {
        try {
            $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        } catch {
            continue
        }

        if (Test-PotentialSecretText $text) {
            $offendingPath = [System.IO.Path]::GetFileName($file.FullName)
            break
        }
    }

    if ($offendingPath) {
        Fail "secrets.scan" "Evidence contains a potential raw secret in $offendingPath."
    } else {
        Pass "secrets.scan" "Evidence text files do not contain known raw secret patterns."
    }
}

function Test-RecordedFile($Inventory, $Name, $Entry, $Label) {
    if ($Entry -and [bool] $Entry.present -eq $true -and (Test-HexSha256 $Entry.sha256) -and -not (Test-Blank $Entry.storedAs) -and $Inventory.ContainsKey([string] $Entry.storedAs)) {
        $inventoryEntry = $Inventory[[string] $Entry.storedAs]
        if ([string] $inventoryEntry.sha256 -eq [string] $Entry.sha256) {
            Pass $Name "$Label is captured and present in the evidence bundle."
            return
        }
    }

    Fail $Name "$Label is missing from evidence or does not match the inventory."
}

function Test-SectionFiles($Inventory, $Name, $StoredAs, $Files, $Label) {
    $seen = 0
    foreach ($file in @($Files)) {
        if ($null -eq $file) {
            continue
        }

        $relativePath = [string] $file.path
        if (Test-Blank $relativePath) {
            continue
        }

        $storedPath = if (Test-Blank $StoredAs) { $relativePath } else { "$StoredAs/$relativePath" }
        if (-not $Inventory.ContainsKey($storedPath)) {
            Fail $Name "$Label inventory is missing $storedPath."
            return
        }

        $inventoryEntry = $Inventory[$storedPath]
        if ((Test-HexSha256 $file.sha256) -and [string] $inventoryEntry.sha256 -ne [string] $file.sha256) {
            Fail $Name "$Label SHA-256 mismatch for $storedPath."
            return
        }

        $seen++
    }

    if ($seen -gt 0) {
        Pass $Name "$Label files are present in the evidence bundle."
    } else {
        Fail $Name "$Label file inventory is empty."
    }
}

function Require-InventoryPath($Inventory, $Path, $Name, $Message) {
    if ($Inventory.ContainsKey($Path)) {
        Pass $Name "Evidence inventory includes $Path."
    } else {
        Fail $Name $Message
    }
}

function Test-ValidationLog($Manifest, $Inventory, $PropertyName, $Label) {
    $entry = $Manifest.validation.$PropertyName
    Test-RecordedFile $Inventory "validation.$PropertyName" $entry "$Label validation log"
}

function Test-SkippableSection($Section, $Name, $CapturedMessage, $SkippedMessage) {
    if ($Section.status -eq "captured") {
        Pass $Name $CapturedMessage
        return $true
    }

    if ($Section.status -eq "skipped" -and -not (Test-Blank $Section.skipReason)) {
        if ($Profile -eq "external-preview") {
            Warn $Name $SkippedMessage
            return $false
        }

        Fail $Name $SkippedMessage
        return $false
    }

    Fail $Name "$Name is neither captured nor explicitly skipped."
    return $false
}

function Test-LiveSmoke($Manifest, $Inventory) {
    $captured = Test-SkippableSection $Manifest.liveSmoke "liveSmoke" "Live Revit smoke evidence is captured." "Live Revit smoke evidence is skipped; this is acceptable only for clearly labeled external previews."
    if (-not $captured) { return }

    Test-SectionFiles $Inventory "liveSmoke.files" $Manifest.liveSmoke.storedAs $Manifest.liveSmoke.files "Live smoke evidence"

    if ($Manifest.liveSmoke.summary.status -eq "passed") {
        Pass "liveSmoke.summary.status" "Live Revit smoke summary passed."
    } else {
        Fail "liveSmoke.summary.status" "Live Revit smoke summary did not pass."
    }

    if (@($Manifest.liveSmoke.summary.coveredTools) -contains "revit.status") {
        Pass "liveSmoke.coveredTools.status" "Live smoke covered revit.status."
    } else {
        Fail "liveSmoke.coveredTools.status" "Live smoke did not record revit.status coverage."
    }

    if (@($Manifest.liveSmoke.summary.coveredTools) -contains "revit.cancel_request") {
        Pass "liveSmoke.coveredTools.cancel" "Live smoke covered revit.cancel_request no-op behavior."
    } else {
        Warn "liveSmoke.coveredTools.cancel" "Live smoke did not record revit.cancel_request coverage; rerun smoke with a newer build for cancellation evidence."
    }

    $operationKindGuard = $Manifest.liveSmoke.summary.operationKindGuard
    if ($operationKindGuard -and $operationKindGuard.errorCode -eq "OPERATION_KIND_MISMATCH") {
        Pass "liveSmoke.operationKindGuard" "Live smoke proved the loaded add-in rejects mismatched operationKind bridge calls."
    } elseif ($Profile -eq "external-preview") {
        Warn "liveSmoke.operationKindGuard" "Live smoke did not record operationKind mismatch guard evidence; rerun smoke with a newer build before release-candidate use."
    } else {
        Fail "liveSmoke.operationKindGuard" "Release-candidate and production readiness require live smoke evidence that the loaded add-in rejects mismatched operationKind bridge calls."
    }

    $coveredOperations = @($Manifest.liveSmoke.summary.coveredOperations)
    $requiredCoverage = $Manifest.liveSmoke.summary.requiredCoverage
    $requiresCuratedTagEvidence = $Profile -eq "release-candidate" -or $Profile -eq "production"
    if ($requiresCuratedTagEvidence) {
        if ($requiredCoverage -and $requiredCoverage.roomTag -eq $true -and $requiredCoverage.elementTag -eq $true) {
            Pass "liveSmoke.requiredCoverage.tags" "Release readiness required curated room and element tag smoke coverage."
        } else {
            Fail "liveSmoke.requiredCoverage.tags" "Release-candidate and production readiness require live smoke evidence captured with required tag_room and tag_element coverage."
        }
    }

    if ($requiredCoverage -and $requiredCoverage.roomTag -eq $true) {
        if ($coveredOperations -contains "tag_room") {
            Pass "liveSmoke.requiredCoverage.roomTag" "Live smoke required and covered tag_room."
        } else {
            Fail "liveSmoke.requiredCoverage.roomTag" "Live smoke required tag_room but did not record tag_room coverage."
        }
        $roomTagCoverage = $Manifest.liveSmoke.summary.tagCoverage.room
        if ($roomTagCoverage -and
            -not (Test-Blank $roomTagCoverage.createdTagId) -and
            -not (Test-Blank $roomTagCoverage.roomId) -and
            -not (Test-Blank $roomTagCoverage.viewId) -and
            -not (Test-Blank $roomTagCoverage.tagTypeId)) {
            Pass "liveSmoke.tagCoverage.room" "Live smoke recorded room tag target, view, type, and created tag IDs."
        } else {
            Fail "liveSmoke.tagCoverage.room" "Live smoke required tag_room but tagCoverage.room is missing target, view, type, or created tag IDs."
        }
    }
    if ($requiredCoverage -and $requiredCoverage.elementTag -eq $true) {
        if ($coveredOperations -contains "tag_element") {
            Pass "liveSmoke.requiredCoverage.elementTag" "Live smoke required and covered tag_element."
        } else {
            Fail "liveSmoke.requiredCoverage.elementTag" "Live smoke required tag_element but did not record tag_element coverage."
        }
        $elementTagCoverage = $Manifest.liveSmoke.summary.tagCoverage.element
        if ($elementTagCoverage -and
            -not (Test-Blank $elementTagCoverage.createdTagId) -and
            -not (Test-Blank $elementTagCoverage.elementId) -and
            -not (Test-Blank $elementTagCoverage.viewId) -and
            -not (Test-Blank $elementTagCoverage.tagTypeId)) {
            Pass "liveSmoke.tagCoverage.element" "Live smoke recorded element tag target, view, type, and created tag IDs."
        } else {
            Fail "liveSmoke.tagCoverage.element" "Live smoke required tag_element but tagCoverage.element is missing target, view, type, or created tag IDs."
        }
    }

    $packageIdentity = $Manifest.liveSmoke.summary.packageIdentity
    if ($packageIdentity -and
        (Test-HexSha256 $packageIdentity.expectedSha256) -and
        $packageIdentity.assemblySha256 -eq $packageIdentity.expectedSha256) {
        Pass "liveSmoke.packageIdentity" "Live smoke loaded add-in SHA-256 matches the package manifest."
    } elseif ($Profile -eq "external-preview") {
        Warn "liveSmoke.packageIdentity" "Live smoke package identity is missing or mismatched; rerun smoke with a newer build before release-candidate use."
    } else {
        Fail "liveSmoke.packageIdentity" "Live smoke package identity is missing or mismatched."
    }
}

function Test-HostedIntegrations($Manifest, $Inventory) {
    $captured = Test-SkippableSection $Manifest.hostedIntegrations "hostedIntegrations" "Hosted pyRevit/Dynamo evidence is captured." "Hosted pyRevit/Dynamo evidence is skipped; this is acceptable only for clearly labeled external previews."
    if (-not $captured) { return }

    Test-SectionFiles $Inventory "hostedIntegrations.files" $Manifest.hostedIntegrations.storedAs $Manifest.hostedIntegrations.files "Hosted integration evidence"

    if ($Manifest.hostedIntegrations.summary.status -eq "passed") {
        Pass "hostedIntegrations.summary.status" "Hosted integration summary passed."
    } else {
        Fail "hostedIntegrations.summary.status" "Hosted integration summary did not pass."
    }

    foreach ($hostName in @("pyrevit", "dynamo")) {
        $hostSummary = $Manifest.hostedIntegrations.summary.hosts.$hostName
        if ($hostSummary.status -eq "passed") {
            Pass "hostedIntegrations.$hostName.status" "$hostName hosted evidence passed."
        } else {
            Fail "hostedIntegrations.$hostName.status" "$hostName hosted evidence did not pass."
        }

        if ([bool] $hostSummary.previewReady -eq $true -and [bool] $hostSummary.applyWrites -eq $true) {
            Pass "hostedIntegrations.$hostName.writeCoverage" "$hostName hosted evidence includes preview/apply write coverage."
        } else {
            Fail "hostedIntegrations.$hostName.writeCoverage" "$hostName hosted evidence lacks preview/apply write coverage."
        }
    }

    $packageIdentity = $Manifest.hostedIntegrations.summary.packageIdentity
    if ($packageIdentity -and
        (Test-HexSha256 $packageIdentity.expectedSha256) -and
        $packageIdentity.hosts.pyrevit.assemblySha256 -eq $packageIdentity.expectedSha256 -and
        $packageIdentity.hosts.dynamo.assemblySha256 -eq $packageIdentity.expectedSha256) {
        Pass "hostedIntegrations.packageIdentity" "Hosted pyRevit/Dynamo loaded add-in SHA-256 matches the package manifest."
    } else {
        Fail "hostedIntegrations.packageIdentity" "Hosted pyRevit/Dynamo add-in identity does not match the package manifest."
    }
}

$resolved = $null
try {
    $resolved = Resolve-EvidenceManifest $EvidencePath
    $manifest = Read-JsonFile $resolved.ManifestPath

    if ($manifest.schemaVersion -eq 1) {
        Pass "schemaVersion" "Release evidence manifest schemaVersion is 1."
    } else {
        Fail "schemaVersion" "Release evidence manifest schemaVersion is not 1."
    }

    $inventory = Test-Inventory $manifest $resolved.Root
    Test-NoSensitiveEvidence $resolved.Root

    Require-NonBlank "package.version" $manifest.package.version "Package version is missing."
    Require-NonBlank "package.gitCommit" $manifest.package.gitCommit "Package git commit is missing."

    if ([bool] $manifest.package.gitDirty -eq $false) {
        Pass "package.gitDirty" "Package was built from a clean git worktree."
    } elseif ($AllowDirty) {
        Warn "package.gitDirty" "Package was built from a dirty git worktree; this is allowed only for local contract testing."
    } else {
        Fail "package.gitDirty" "Package was built from a dirty git worktree."
    }

    $years = @($manifest.package.revitYears | ForEach-Object { [int] $_ })
    if ($years.Count -eq 1 -and $years[0] -eq 2024) {
        Pass "package.revitYears" "Evidence is explicitly Revit 2024-only."
    } else {
        Fail "package.revitYears" "Evidence advertises unsupported Revit years: $($years -join ', ')."
    }

    if ([bool] $manifest.package.nodeModulesBundled -eq $true) {
        Pass "package.nodeModulesBundled" "Broker production dependencies are bundled."
    } else {
        Fail "package.nodeModulesBundled" "Broker production dependencies are not bundled."
    }

    if (Test-HexSha256 $manifest.package.evidence.packageZipSha256) {
        Pass "package.zipSha256" "Package zip SHA-256 is recorded."
    } else {
        Fail "package.zipSha256" "Package zip SHA-256 is missing or malformed."
    }

    if ([string] $manifest.package.evidence.packageZipName -like "*.zip" -and [string] $manifest.package.evidence.packageZipPath -like "*.zip") {
        Pass "package.zipPath" "Package evidence references a release .zip."
    } else {
        Fail "package.zipPath" "Package evidence does not reference a release .zip."
    }

    if (Test-HexSha256 $manifest.package.evidence.packagedAddin.sha256) {
        Pass "package.addinSha256" "Packaged add-in SHA-256 is recorded."
    } else {
        Fail "package.addinSha256" "Packaged add-in SHA-256 is missing or malformed."
    }

    foreach ($relativePath in @("package/release-manifest.json", "package/CHECKSUMS.sha256", "package/package-zip.sha256", "release-evidence-summary.md")) {
        Require-InventoryPath $inventory $relativePath "contents.$relativePath" "Evidence inventory is missing $relativePath."
    }

    Test-ValidationLog $manifest $inventory "validateRepoLog" "validate-repo"
    Test-ValidationLog $manifest $inventory "packageLog" "package"
    Test-ValidationLog $manifest $inventory "doctorLog" "doctor"

    if ($manifest.supportBundle.status -eq "captured") {
        Pass "supportBundle" "Support bundle evidence is captured."
        Test-SectionFiles $inventory "supportBundle.files" $manifest.supportBundle.storedAs $manifest.supportBundle.files "Support bundle evidence"
    } elseif ($manifest.supportBundle.status -eq "skipped" -and -not (Test-Blank $manifest.supportBundle.skipReason) -and $Profile -eq "external-preview") {
        Warn "supportBundle" "Support bundle evidence is skipped; shareable preview should explain this explicitly."
    } else {
        Fail "supportBundle" "Support bundle evidence is missing."
    }

    Test-LiveSmoke $manifest $inventory
    Test-HostedIntegrations $manifest $inventory

    if ($manifest.signing.status -eq "captured" -and [bool] $manifest.signing.requested -eq $true -and $manifest.signing.log.present -eq $true) {
        Pass "signing" "Signing evidence is captured."
        Test-RecordedFile $inventory "signing.log" $manifest.signing.log "Signing log"
    } elseif ($Profile -eq "production") {
        Fail "signing" "Production readiness requires captured signing evidence for the exact package."
    } elseif ($manifest.signing.status -eq "skipped" -and -not (Test-Blank $manifest.signing.skipReason)) {
        Warn "signing" "Signing was skipped with an explicit reason; label the package as unsigned."
    } else {
        Fail "signing" "Signing evidence is neither captured nor explicitly skipped."
    }

    $status = if ($failures.Count -gt 0) {
        "failed"
    } elseif ($warnings.Count -gt 0) {
        "passed_with_warnings"
    } else {
        "passed"
    }

    $summary = [ordered] @{
        schemaVersion = 1
        status = $status
        profile = $Profile
        evidencePath = (Get-FullPath $EvidencePath)
        manifestPath = $resolved.ManifestPath
        package = [ordered] @{
            version = [string] $manifest.package.version
            gitCommit = [string] $manifest.package.gitCommit
            gitDirty = [bool] $manifest.package.gitDirty
            revitYears = $manifest.package.revitYears
        }
        failures = @($failures.ToArray())
        warnings = @($warnings.ToArray())
        checks = @($checks.ToArray())
    }

    if ($Json) {
        $summary | ConvertTo-Json -Depth 8
    } else {
        Write-Host "[revit-mcp-next readiness] Profile: $Profile"
        Write-Host "[revit-mcp-next readiness] Status: $status"
        foreach ($check in $checks) {
            Write-Host "[$($check.status)] $($check.name): $($check.message)"
        }
    }

    if ($failures.Count -gt 0) {
        exit 1
    }
} finally {
    if ($resolved -and $resolved.TempRoot -and (Test-Path -LiteralPath $resolved.TempRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolved.TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
