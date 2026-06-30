[CmdletBinding(PositionalBinding = $false)]
param(
    [string] $PackageRoot = "",
    [string[]] $Path = @(),
    [string] $CertificateThumbprint = "$env:REVIT_MCP_NEXT_SIGN_CERT_THUMBPRINT",
    [string] $CertificatePath = "$env:REVIT_MCP_NEXT_SIGN_CERT_PATH",
    [string] $CertificatePasswordEnv = "REVIT_MCP_NEXT_SIGN_CERT_PASSWORD",
    [string] $TimestampServer = "$env:REVIT_MCP_NEXT_TIMESTAMP_URL",
    [switch] $NoTimestamp,
    [switch] $VerifyOnly,
    [switch] $RequireSigned,
    [switch] $RequireTrusted,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    Write-Host "[revit-mcp-next sign] $Message"
}

function Resolve-SignTarget($Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }

    if ($Value.Contains("*")) {
        return Get-ChildItem -Path $Value -File | ForEach-Object { $_.FullName }
    }

    if (-not (Test-Path -LiteralPath $Value -PathType Leaf)) {
        throw "Signing target was not found: $Value"
    }

    return @((Resolve-Path -LiteralPath $Value).Path)
}

function Get-PackageTargets($Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        return @()
    }

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "PackageRoot was not found: $Root"
    }

    return Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object { $_.Extension -in @(".dll", ".ps1") } |
        Sort-Object FullName |
        ForEach-Object { $_.FullName }
}

function Resolve-Certificate {
    $normalizedThumbprint = ""
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $normalizedThumbprint = $CertificateThumbprint.Replace(" ", "")
    }
    if (-not [string]::IsNullOrWhiteSpace($normalizedThumbprint)) {
        foreach ($storePath in @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My")) {
            $match = Get-ChildItem -Path $storePath -ErrorAction SilentlyContinue |
                Where-Object { $_.Thumbprint -ieq $normalizedThumbprint } |
                Select-Object -First 1
            if ($match) {
                return $match
            }
        }

        throw "Signing certificate thumbprint was not found in CurrentUser or LocalMachine My stores."
    }

    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
            throw "Signing certificate file was not found: $CertificatePath"
        }

        $password = [Environment]::GetEnvironmentVariable($CertificatePasswordEnv)
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
            (Resolve-Path -LiteralPath $CertificatePath).Path,
            $password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        )
    }

    throw "No signing certificate configured. Set REVIT_MCP_NEXT_SIGN_CERT_THUMBPRINT, pass -CertificateThumbprint, or pass -CertificatePath."
}

function Test-Signature($Target) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Target
    $subject = $null
    if ($signature.SignerCertificate) {
        $subject = $signature.SignerCertificate.Subject
    }

    return [ordered] @{
        path = $Target
        status = $signature.Status.ToString()
        statusMessage = $signature.StatusMessage
        signerSubject = $subject
    }
}

function Assert-SignaturePolicy($Result) {
    if ($RequireTrusted -and $Result.status -ne "Valid") {
        throw "Signature for $($Result.path) is $($Result.status), expected Valid."
    }

    if ($RequireSigned -and $Result.status -eq "NotSigned") {
        throw "Signature is missing for $($Result.path)."
    }
}

$targets = New-Object System.Collections.Generic.List[string]
foreach ($target in (Get-PackageTargets $PackageRoot)) {
    $targets.Add($target) | Out-Null
}
foreach ($targetPattern in $Path) {
    foreach ($pattern in ([string] $targetPattern).Split(",", [System.StringSplitOptions]::RemoveEmptyEntries)) {
        foreach ($target in (Resolve-SignTarget $pattern.Trim())) {
            $targets.Add($target) | Out-Null
        }
    }
}

$uniqueTargets = $targets |
    Sort-Object -Unique |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

if ($uniqueTargets.Count -eq 0) {
    throw "No signing targets were found."
}

if ($NoTimestamp) {
    $TimestampServer = ""
} elseif ([string]::IsNullOrWhiteSpace($TimestampServer)) {
    $TimestampServer = "http://timestamp.digicert.com"
}

if ($VerifyOnly) {
    Write-Step "Verifying $($uniqueTargets.Count) Authenticode signature target(s)."
    foreach ($target in $uniqueTargets) {
        $result = Test-Signature $target
        Assert-SignaturePolicy $result
        Write-Step "$($result.status): $target"
    }
    return
}

if ($DryRun) {
    Write-Step "Would sign $($uniqueTargets.Count) Authenticode target(s)."
    foreach ($target in $uniqueTargets) {
        Write-Step "Would sign: $target"
    }
    return
}

$certificate = Resolve-Certificate
Write-Step "Signing $($uniqueTargets.Count) Authenticode target(s)."
foreach ($target in $uniqueTargets) {
    $signArguments = @{
        LiteralPath = $target
        Certificate = $certificate
        HashAlgorithm = "SHA256"
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
        $signArguments["TimestampServer"] = $TimestampServer
    }

    $signature = Set-AuthenticodeSignature @signArguments
    if ($signature.Status -eq "UnknownError" -or $signature.Status -eq "HashMismatch" -or $signature.Status -eq "NotSigned") {
        throw "Failed to sign $target. Status: $($signature.Status) $($signature.StatusMessage)"
    }

    $result = Test-Signature $target
    Assert-SignaturePolicy $result
    Write-Step "$($result.status): $target"
}
