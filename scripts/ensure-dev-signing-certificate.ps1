param(
    [string] $Subject = "CN=Revit MCP Next Local Dev Code Signing",
    [int] $ValidYears = 3,
    [switch] $Trust,
    [switch] $Json,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"

function Write-Step($Message) {
    if (-not $Json) {
        Write-Host "[revit-mcp-next dev-cert] $Message"
    }
}

function Test-CodeSigningCertificate($Certificate) {
    if (-not $Certificate.HasPrivateKey) {
        return $false
    }

    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    foreach ($usage in $Certificate.EnhancedKeyUsageList) {
        if ($usage.ObjectId.Value -eq $codeSigningOid) {
            return $true
        }
    }

    return $false
}

function Find-DevCertificate {
    $minimumExpiry = (Get-Date).AddDays(30)
    return Get-ChildItem -Path "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Subject -eq $Subject -and
            $_.NotAfter -gt $minimumExpiry -and
            (Test-CodeSigningCertificate $_)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function New-DevCertificate {
    if ($DryRun) {
        return $null
    }

    $notAfter = (Get-Date).AddYears($ValidYears)
    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -NotAfter $notAfter
}

function Test-CertificateInStore($Certificate, $StoreName) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        $StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
    )
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    try {
        foreach ($candidate in $store.Certificates) {
            if ($candidate.Thumbprint -ieq $Certificate.Thumbprint) {
                return $true
            }
        }

        return $false
    } finally {
        $store.Close()
    }
}

function Add-CertificateToStore($Certificate, $StoreName) {
    if (Test-CertificateInStore $Certificate $StoreName) {
        return $false
    }

    if ($DryRun) {
        return $true
    }

    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        $StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
    )
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $store.Add($Certificate)
    } finally {
        $store.Close()
    }

    return $true
}

if ($ValidYears -lt 1 -or $ValidYears -gt 10) {
    throw "-ValidYears must be between 1 and 10."
}

$created = $false
$certificate = Find-DevCertificate
if (-not $certificate) {
    Write-Step "Creating CurrentUser code-signing certificate: $Subject"
    $certificate = New-DevCertificate
    $created = $true
}

if (-not $certificate -and -not $DryRun) {
    throw "Unable to create or locate a local dev signing certificate."
}

$trustedRootAdded = $false
$trustedPublisherAdded = $false
$trustedRootPresent = $false
$trustedPublisherPresent = $false

if ($certificate -and $Trust) {
    $trustedRootAdded = Add-CertificateToStore $certificate "Root"
    $trustedPublisherAdded = Add-CertificateToStore $certificate "TrustedPublisher"
    $trustedRootPresent = Test-CertificateInStore $certificate "Root"
    $trustedPublisherPresent = Test-CertificateInStore $certificate "TrustedPublisher"
}

$result = [ordered] @{
    subject = $Subject
    thumbprint = if ($certificate) { $certificate.Thumbprint } else { $null }
    created = ($created -and -not $DryRun)
    wouldCreate = ($created -and $DryRun)
    trusted = [ordered] @{
        requested = [bool] $Trust
        rootPresent = $trustedRootPresent
        trustedPublisherPresent = $trustedPublisherPresent
        rootAdded = $trustedRootAdded
        trustedPublisherAdded = $trustedPublisherAdded
    }
    notAfter = if ($certificate) { $certificate.NotAfter.ToUniversalTime().ToString("o") } else { $null }
    store = "Cert:\CurrentUser\My"
    dryRun = [bool] $DryRun
}

if ($Json) {
    $result | ConvertTo-Json -Depth 5 -Compress
} else {
    if ($certificate) {
        Write-Step "Thumbprint: $($certificate.Thumbprint)"
        if ($Trust) {
            Write-Step "Trusted in CurrentUser Root: $trustedRootPresent; TrustedPublisher: $trustedPublisherPresent"
        }
    } else {
        Write-Step "Dry run: would create certificate."
    }
}
