function Resolve-DotnetSdk {
    param([string] $DotnetPath = "", [int] $MinimumMajor = 10)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) {
        $candidates = @($DotnetPath)
    } else {
        if ($env:DOTNET_ROOT) { $candidates += Join-Path $env:DOTNET_ROOT "dotnet.exe" }
        $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($command) { $candidates += $command.Source }
        $candidates += Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
        $candidates += Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        try {
            $sdks = @(& $candidate --list-sdks 2>$null)
            if ($LASTEXITCODE -ne 0) { continue }
            foreach ($sdk in $sdks) {
                if ([string]$sdk -match '^(\d+)\.\d+\.\d+\s+\[' -and [int]$Matches[1] -ge $MinimumMajor) {
                    return $candidate
                }
            }
        } catch { continue }
    }
    throw "A runnable .NET $MinimumMajor SDK or newer was not found. Install the SDK (a runtime alone is insufficient), or pass -DotnetPath."
}
