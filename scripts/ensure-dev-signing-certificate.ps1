param(
    [string] $Subject = "CN=Revit MCP Next Local Dev Code Signing",
    [string] $Thumbprint = "",
    [int] $ValidYears = 3,
    [switch] $Trust,
    [switch] $StatusOnly,
    [switch] $Remove,
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

function Normalize-Thumbprint($Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return $Value.Replace(" ", "").ToUpperInvariant()
}

function Test-CertificateMatches($Certificate) {
    if ($Certificate.Subject -ne $Subject) {
        return $false
    }

    $normalizedThumbprint = Normalize-Thumbprint $Thumbprint
    if (-not [string]::IsNullOrWhiteSpace($normalizedThumbprint) -and $Certificate.Thumbprint -ine $normalizedThumbprint) {
        return $false
    }

    return $true
}

function Find-DevCertificate {
    $minimumExpiry = (Get-Date).AddDays(30)
    return Get-ChildItem -Path "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
        Where-Object {
            (Test-CertificateMatches $_) -and
            $_.NotAfter -gt $minimumExpiry -and
            (Test-CodeSigningCertificate $_)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Get-MatchingStoreCertificates($StoreName) {
    $storePath = "Cert:\CurrentUser\$StoreName"
    return @(Get-ChildItem -Path $storePath -ErrorAction SilentlyContinue |
        Where-Object { Test-CertificateMatches $_ } |
        Sort-Object Thumbprint -Unique)
}

function Get-StoreInventory {
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($storeName in @("My", "Root", "TrustedPublisher")) {
        $certificates = New-Object System.Collections.Generic.List[object]
        foreach ($certificate in (Get-MatchingStoreCertificates $storeName)) {
            $certificates.Add([ordered] @{
                thumbprint = $certificate.Thumbprint
                subject = $certificate.Subject
                issuer = $certificate.Issuer
                notBefore = $certificate.NotBefore.ToUniversalTime().ToString("o")
                notAfter = $certificate.NotAfter.ToUniversalTime().ToString("o")
                hasPrivateKey = [bool] $certificate.HasPrivateKey
                codeSigning = [bool] (Test-CodeSigningCertificate $certificate)
            }) | Out-Null
        }

        $entries.Add([ordered] @{
            store = "Cert:\CurrentUser\$storeName"
            count = $certificates.Count
            certificates = $certificates
        }) | Out-Null
    }

    return $entries
}

function Remove-MatchingStoreCertificates($StoreName) {
    $removed = New-Object System.Collections.Generic.List[object]
    foreach ($certificate in (Get-MatchingStoreCertificates $StoreName)) {
        $removed.Add([ordered] @{
            store = "Cert:\CurrentUser\$StoreName"
            thumbprint = $certificate.Thumbprint
            subject = $certificate.Subject
            dryRun = [bool] $DryRun
        }) | Out-Null

        if (-not $DryRun) {
            Remove-Item -LiteralPath $certificate.PSPath -Force
        }
    }

    return $removed
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
if ($Remove -and $Trust) {
    throw "-Remove cannot be combined with -Trust."
}

if ($StatusOnly) {
    $result = [ordered] @{
        subject = $Subject
        thumbprintFilter = Normalize-Thumbprint $Thumbprint
        statusOnly = $true
        stores = Get-StoreInventory
        dryRun = [bool] $DryRun
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8 -Compress
    } else {
        Write-Step "Status only for $Subject"
        foreach ($store in $result.stores) {
            Write-Step "$($store.store): $($store.count) matching certificate(s)"
        }
    }
    return
}

if ($Remove) {
    $removed = New-Object System.Collections.Generic.List[object]
    foreach ($storeName in @("TrustedPublisher", "Root", "My")) {
        foreach ($entry in (Remove-MatchingStoreCertificates $storeName)) {
            $removed.Add($entry) | Out-Null
        }
    }

    $result = [ordered] @{
        subject = $Subject
        thumbprintFilter = Normalize-Thumbprint $Thumbprint
        removed = $removed
        dryRun = [bool] $DryRun
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8 -Compress
    } else {
        if ($DryRun) {
            Write-Step "Dry run: would remove $($removed.Count) matching certificate store entries."
        } else {
            Write-Step "Removed $($removed.Count) matching certificate store entries."
        }
    }
    return
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
    stores = Get-StoreInventory
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
