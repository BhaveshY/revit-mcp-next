param(
    [string] $OutputRoot = "",
    [switch] $KeepArtifacts
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next release-contract] $Message"
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

function Assert-PathChild($Root, $Path, $Label) {
    $rootFull = Get-FullPath $Root
    $pathFull = Get-FullPath $Path
    $rootWithSeparator = Add-TrailingSeparator $rootFull

    if ($pathFull -ne $rootFull -and -not $pathFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use $Label outside expected root. Root: $rootFull Target: $pathFull"
    }
}

function Read-JsonFile($Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
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

function Invoke-RepoScript([string] $Path, [string[]] $Arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Path failed with exit code $LASTEXITCODE."
    }
}

function Assert-FileExists($Path, $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not created: $Path"
    }
}

function Assert-DirectoryExists($Path, $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label was not created: $Path"
    }
}

function New-SyntheticAddinOutput($Root) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Set-Content -LiteralPath (Join-Path $Root "RevitMcpNext.Addin.dll") -Value "synthetic add-in placeholder for package contract" -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $Root "RevitMcpNext.Contracts.dll") -Value "synthetic contracts placeholder for package contract" -Encoding ASCII
}

function Assert-NoRawTokenInSupportBundle($SupportRoot, $Token) {
    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "Auth token was not found in the temp install."
    }

    $textFiles = Get-ChildItem -LiteralPath $SupportRoot -Recurse -File |
        Where-Object { $_.Extension -ne ".zip" }

    foreach ($file in $textFiles) {
        try {
            $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        } catch {
            continue
        }

        if ($text.Contains($Token)) {
            throw "Support bundle leaked the raw auth token in $($file.FullName)."
        }
    }
}

function Assert-TamperedPackageFails($PackageRoot, $RunRoot, $InstallerScript) {
    $tamperRoot = Join-Path $RunRoot "tmp"
    $tamperedPackage = Join-Path $tamperRoot "p"
    New-Item -ItemType Directory -Force -Path $tamperRoot | Out-Null
    Copy-Item -LiteralPath $PackageRoot -Destination $tamperedPackage -Recurse -Force
    Add-Content -LiteralPath (Join-Path $tamperedPackage "README.md") -Value "tampered-by-release-contract"

    $tamperInstallRoot = Join-Path $RunRoot "ti"
    $failedAsExpected = $false
    try {
        & $InstallerScript `
            -PackageRoot $tamperedPackage `
            -InstallRoot $tamperInstallRoot `
            -DryRun `
            -SkipDependencyInstall
    } catch {
        if ($_.Exception.Message -like "*Checksum mismatch*") {
            $failedAsExpected = $true
        } else {
            throw
        }
    }

    if (-not $failedAsExpected) {
        throw "Tampered package dry-run unexpectedly passed checksum verification."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release-contract"
}

$outputRootFull = Get-FullPath $OutputRoot
New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null

$runId = "r-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runRoot = Join-Path $outputRootFull $runId
Assert-PathChild $outputRootFull $runRoot "release contract run root"

$oldAppData = $env:APPDATA
$oldLocalAppData = $env:LOCALAPPDATA

try {
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $syntheticAddinRoot = Join-Path $runRoot "syn"
    $packageOutputRoot = Join-Path $runRoot "pkg"
    $installRoot = Join-Path $runRoot "inst"
    $supportRoot = Join-Path $runRoot "sup"
    $env:APPDATA = Join-Path $runRoot "ad"
    $env:LOCALAPPDATA = Join-Path $runRoot "lad"
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA | Out-Null

    Write-Step "Run root: $runRoot"
    New-SyntheticAddinOutput $syntheticAddinRoot

    $packageScript = Join-Path $repoRoot "scripts\package-release.ps1"
    Invoke-RepoScript $packageScript @(
        "-OutputRoot", $packageOutputRoot,
        "-AddinOutputRoot", $syntheticAddinRoot
    )

    $rootPackage = Read-JsonFile (Join-Path $repoRoot "package.json")
    $packageRoot = Join-Path $packageOutputRoot "revit-mcp-next-$($rootPackage.version)-windows"
    Assert-DirectoryExists $packageRoot "staged package"
    Assert-FileExists "$packageRoot.zip" "package zip"
    Assert-FileExists (Join-Path $packageRoot "release-manifest.json") "release manifest"
    Assert-FileExists (Join-Path $packageRoot "CHECKSUMS.sha256") "package checksums"

    $manifest = Read-JsonFile (Join-Path $packageRoot "release-manifest.json")
    if ($manifest.signing.requested -ne $false) {
        throw "Release contract expected an unsigned package manifest."
    }
    if ($manifest.package.nodeModulesBundled -ne $true) {
        throw "Release contract expected packaged broker production node_modules."
    }

    $installerScript = Join-Path $packageRoot "installer\install-windows.ps1"
    Invoke-RepoScript $installerScript @(
        "-PackageRoot", $packageRoot,
        "-InstallRoot", $installRoot,
        "-RevitYears", "2024"
    )

    $doctorScript = Join-Path $packageRoot "scripts\doctor.ps1"
    Invoke-RepoScript $doctorScript @(
        "-InstallRoot", $installRoot,
        "-RevitYear", "2024"
    )

    $supportScript = Join-Path $packageRoot "scripts\collect-support-bundle.ps1"
    Invoke-RepoScript $supportScript @(
        "-InstallRoot", $installRoot,
        "-OutputRoot", $supportRoot,
        "-RevitYears", "2024"
    )
    $supportZip = Get-ChildItem -LiteralPath $supportRoot -Filter "*.zip" -File | Select-Object -First 1
    if (-not $supportZip) {
        throw "Support bundle zip was not created under $supportRoot."
    }

    $authToken = Read-AuthTokenConfig (Join-Path $installRoot "config\auth.env")
    Assert-NoRawTokenInSupportBundle $supportRoot $authToken

    Assert-TamperedPackageFails $packageRoot $runRoot $installerScript
    Write-Step "Release package contract passed."
} finally {
    $env:APPDATA = $oldAppData
    $env:LOCALAPPDATA = $oldLocalAppData

    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $runRoot -PathType Container)) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    } elseif ($KeepArtifacts) {
        Write-Step "Kept artifacts: $runRoot"
    }
}
