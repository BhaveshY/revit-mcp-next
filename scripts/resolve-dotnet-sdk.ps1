# Resolves a dotnet.exe that has a .NET SDK of at least -MinimumMajor (default 10).
# Candidate order (D4 section 3.2); every candidate is printed with its --list-sdks output and the first match wins:
#   1. -DotnetPath (file or folder), then $env:REVIT_MCP_NEXT_DOTNET
#   2. $env:DOTNET_ROOT\dotnet.exe
#   3. dotnet.exe on PATH
#   4. %ProgramFiles%\dotnet\dotnet.exe
#   5. %LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe, then %USERPROFILE%\.dotnet\dotnet.exe
#   6. %LOCALAPPDATA%\Packages\*\LocalCache\Local\Microsoft\dotnet\dotnet.exe (MSIX-virtualized copies; prints a warning)
# When the winning SDK lives inside an MSIX package folder, DOTNET_ROOT and DOTNET_MULTILEVEL_LOOKUP=0 are set for the
# current process only, so the build uses that SDK's own runtimes.

function Get-DotnetCandidateFile {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $trimmed = $Path.Trim().Trim('"').Replace([char]47, [char]92)
    if (Test-Path -LiteralPath $trimmed -PathType Container) {
        return (Join-Path $trimmed "dotnet.exe")
    }
    return $trimmed
}

function Test-DotnetPackagePath {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $Path = $Path.Replace([char]47, [char]92)
    return ($Path -match '\\Packages\\[^\\]+\\LocalCache\\') -or ($Path -match '\\WindowsApps\\')
}

function Get-DotnetPackageName {
    param([string] $Path)
    if ($Path -match '\\Packages\\([^\\]+)\\') { return $Matches[1] }
    return "(unknown package)"
}

function Resolve-DotnetSdk {
    param([string] $DotnetPath = "", [int] $MinimumMajor = 10, [switch] $Quiet)

    $candidates = New-Object System.Collections.Generic.List[string]
    $add = {
        param([string] $value)
        $file = Get-DotnetCandidateFile $value
        if ($file -and -not $candidates.Contains($file)) { $candidates.Add($file) | Out-Null }
    }

    & $add $DotnetPath
    & $add $env:REVIT_MCP_NEXT_DOTNET
    if ($env:DOTNET_ROOT) { & $add (Join-Path $env:DOTNET_ROOT "dotnet.exe") }
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { & $add $command.Source }
    if ($env:ProgramFiles) { & $add (Join-Path $env:ProgramFiles "dotnet\dotnet.exe") }
    if ($env:LOCALAPPDATA) { & $add (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe") }
    if ($env:USERPROFILE) { & $add (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe") }
    if ($env:LOCALAPPDATA) {
        $packagesRoot = Join-Path $env:LOCALAPPDATA "Packages"
        if (Test-Path -LiteralPath $packagesRoot -PathType Container) {
            Get-ChildItem -LiteralPath $packagesRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $packaged = Join-Path $_.FullName "LocalCache\Local\Microsoft\dotnet\dotnet.exe"
                if (Test-Path -LiteralPath $packaged -PathType Leaf) { & $add $packaged }
            }
        }
    }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            if (-not $Quiet) { Write-Host "[revit-mcp-next dotnet] candidate $candidate : not found" }
            continue
        }
        $sdks = @()
        try {
            $sdks = @(& $candidate --list-sdks 2>$null)
            if ($LASTEXITCODE -ne 0) { $sdks = @() }
        } catch {
            $sdks = @()
        }
        $listing = if ($sdks.Count -gt 0) { ($sdks | ForEach-Object { ([string] $_).Split(' ')[0] }) -join ", " } else { "(no SDKs)" }
        if (-not $Quiet) { Write-Host "[revit-mcp-next dotnet] candidate $candidate : $listing" }

        $match = $false
        foreach ($sdk in $sdks) {
            if ([string]$sdk -match '^(\d+)\.\d+\.\d+' -and [int]$Matches[1] -ge $MinimumMajor) { $match = $true; break }
        }
        if (-not $match) { continue }

        if (Test-DotnetPackagePath $candidate) {
            $packageName = Get-DotnetPackageName $candidate
            Write-Warning "using the .NET SDK inside package $packageName; install the SDK normally (winget install Microsoft.DotNet.SDK.10)"
            $env:DOTNET_ROOT = Split-Path -Parent $candidate
            $env:DOTNET_MULTILEVEL_LOOKUP = "0"
        }
        return $candidate
    }

    throw "A runnable .NET $MinimumMajor SDK or newer was not found. Install the SDK (winget install Microsoft.DotNet.SDK.10; a runtime alone is insufficient), set REVIT_MCP_NEXT_DOTNET, or pass -DotnetPath."
}
