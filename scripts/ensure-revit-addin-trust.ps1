param(
    [int[]] $RevitYears = @(2024),
    [string] $ClientId = "6F78E70D-BE13-4E0B-9B11-9E28F876AF71",
    [switch] $StatusOnly,
    [switch] $Remove,
    [switch] $DryRun,
    [switch] $Json
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next revit-trust] $Message"
    }
}

function Normalize-ClientId($Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "-ClientId cannot be empty."
    }

    $trimmed = $Value.Trim("{} ")
    $guid = [Guid]::Empty
    if (-not [Guid]::TryParse($trimmed, [ref] $guid)) {
        throw "-ClientId must be a GUID. Received: $Value"
    }

    return $guid.ToString("D").ToLowerInvariant()
}

function Get-CodeSigningRegistryPath($Year) {
    return "HKCU:\Software\Autodesk\Revit\Autodesk Revit $Year\CodeSigning"
}

function Get-CodeSigningStatus($Year, $NormalizedClientId) {
    $path = Get-CodeSigningRegistryPath $Year
    $present = $false
    $value = $null

    if (Test-Path -LiteralPath $path) {
        $item = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        if ($item -and $item.PSObject.Properties[$NormalizedClientId]) {
            $present = $true
            $value = $item.$NormalizedClientId
        }
    }

    return [ordered] @{
        revitYear = $Year
        path = $path
        clientId = $NormalizedClientId
        present = $present
        value = $value
    }
}

function Set-CodeSigningTrust($Year, $NormalizedClientId) {
    $path = Get-CodeSigningRegistryPath $Year
    if ($DryRun) {
        return [ordered] @{
            revitYear = $Year
            path = $path
            clientId = $NormalizedClientId
            changed = $true
            dryRun = $true
            action = "set"
        }
    }

    New-Item -Path $path -Force | Out-Null
    $before = Get-CodeSigningStatus $Year $NormalizedClientId
    New-ItemProperty -LiteralPath $path -Name $NormalizedClientId -Value 1 -PropertyType DWord -Force | Out-Null
    $after = Get-CodeSigningStatus $Year $NormalizedClientId

    return [ordered] @{
        revitYear = $Year
        path = $path
        clientId = $NormalizedClientId
        changed = (-not [bool] $before.present -or [int] $before.value -ne 1)
        dryRun = $false
        action = "set"
        present = [bool] $after.present
        value = $after.value
    }
}

function Remove-CodeSigningTrust($Year, $NormalizedClientId) {
    $path = Get-CodeSigningRegistryPath $Year
    $before = Get-CodeSigningStatus $Year $NormalizedClientId
    if ($DryRun) {
        return [ordered] @{
            revitYear = $Year
            path = $path
            clientId = $NormalizedClientId
            changed = [bool] $before.present
            dryRun = $true
            action = "remove"
        }
    }

    if (Test-Path -LiteralPath $path) {
        Remove-ItemProperty -LiteralPath $path -Name $NormalizedClientId -ErrorAction SilentlyContinue
    }

    return [ordered] @{
        revitYear = $Year
        path = $path
        clientId = $NormalizedClientId
        changed = [bool] $before.present
        dryRun = $false
        action = "remove"
    }
}

if (-not $RevitYears -or $RevitYears.Count -eq 0) {
    throw "At least one Revit year must be supplied."
}
if ($StatusOnly -and $Remove) {
    throw "-StatusOnly cannot be combined with -Remove."
}

$normalizedClientId = Normalize-ClientId $ClientId
$years = @($RevitYears | Sort-Object -Unique)
$entries = New-Object System.Collections.Generic.List[object]

foreach ($year in $years) {
    if ($year -lt 2019 -or $year -gt 2100) {
        throw "Unsupported Revit year value: $year"
    }

    if ($StatusOnly) {
        $entries.Add((Get-CodeSigningStatus $year $normalizedClientId)) | Out-Null
    } elseif ($Remove) {
        $entries.Add((Remove-CodeSigningTrust $year $normalizedClientId)) | Out-Null
    } else {
        $entries.Add((Set-CodeSigningTrust $year $normalizedClientId)) | Out-Null
    }
}

$result = [ordered] @{
    clientId = $normalizedClientId
    statusOnly = [bool] $StatusOnly
    remove = [bool] $Remove
    dryRun = [bool] $DryRun
    entries = $entries
}

if ($Json) {
    $result | ConvertTo-Json -Depth 5 -Compress
} else {
    foreach ($entry in $entries) {
        if ($StatusOnly) {
            Write-Step "Revit $($entry.revitYear): present=$($entry.present) value=$($entry.value) at $($entry.path)"
        } elseif ($Remove) {
            Write-Step "Revit $($entry.revitYear): removed=$($entry.changed) $($entry.clientId) from $($entry.path)"
        } else {
            Write-Step "Revit $($entry.revitYear): trusted $($entry.clientId) at $($entry.path)"
        }
    }
}
