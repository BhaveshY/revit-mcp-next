param(
    [string] $Subject = "CN=Revit MCP Next Local Dev Code Signing",
    [string] $Thumbprint = "",
    [int] $ValidYears = 3,
    [switch] $Trust,
    [switch] $AutoApproveRootTrustPrompt,
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
        $oidValue = ""
        if ($usage.ObjectId -is [string]) {
            $oidValue = [string] $usage.ObjectId
        } elseif ($usage.ObjectId) {
            $oidValue = [string] $usage.ObjectId.Value
        }

        if ($oidValue -eq $codeSigningOid -or [string] $usage.FriendlyName -eq "Code Signing") {
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

function Invoke-TrustPromptApproval($Process, $TimeoutSeconds) {
    if (-not $AutoApproveRootTrustPrompt) {
        return $false
    }

    try {
        Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes -ErrorAction Stop
        if (-not ("RevitMcpNextMouseClicker" -as [type])) {
            Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RevitMcpNextMouseClicker {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  public const uint LEFTDOWN = 0x0002;
  public const uint LEFTUP = 0x0004;
}
'@
        }
    } catch {
        throw "Unable to initialize UIAutomation for certificate trust prompt approval. $($_.Exception.Message)"
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $buttonNames = @("Yes", "Ja", "OK")
    $buttonIds = @("CommandButton_6", "CommandButton_1")
    while (-not $Process.HasExited -and (Get-Date) -lt $deadline) {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $elements = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition
        )

        foreach ($element in $elements) {
            $name = [string] $element.Current.Name
            $automationId = [string] $element.Current.AutomationId
            if ($buttonNames -notcontains $name -and $buttonIds -notcontains $automationId) {
                continue
            }

            $rect = $element.Current.BoundingRectangle
            if ($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0) {
                continue
            }

            $x = [int] ($rect.X + ($rect.Width / 2))
            $y = [int] ($rect.Y + ($rect.Height / 2))
            [RevitMcpNextMouseClicker]::SetCursorPos($x, $y) | Out-Null
            Start-Sleep -Milliseconds 100
            [RevitMcpNextMouseClicker]::mouse_event([RevitMcpNextMouseClicker]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 100
            [RevitMcpNextMouseClicker]::mouse_event([RevitMcpNextMouseClicker]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
            return $true
        }

        Start-Sleep -Milliseconds 500
        $Process.Refresh()
    }

    return $false
}

function Add-CertificateToRootWithCertUtil($Certificate) {
    if (Test-CertificateInStore $Certificate "Root") {
        return $false
    }

    if ($DryRun) {
        return $true
    }

    $tempCertificate = Join-Path $env:TEMP ("revit-mcp-next-dev-" + $Certificate.Thumbprint + ".cer")
    $stdoutPath = Join-Path $env:TEMP ("revit-mcp-next-dev-certutil-" + $Certificate.Thumbprint + ".out.log")
    $stderrPath = Join-Path $env:TEMP ("revit-mcp-next-dev-certutil-" + $Certificate.Thumbprint + ".err.log")
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

    try {
        Export-Certificate -Cert $Certificate -FilePath $tempCertificate -Type CERT -Force | Out-Null
        $process = Start-Process `
            -FilePath "certutil.exe" `
            -ArgumentList @("-user", "-addstore", "-f", "Root", $tempCertificate) `
            -PassThru `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        $approved = Invoke-TrustPromptApproval $process 60
        if (-not $process.WaitForExit(60000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $approvalMessage = if ($AutoApproveRootTrustPrompt) { "Auto-approval attempted: $approved." } else { "Pass -AutoApproveRootTrustPrompt on disposable interactive machines." }
            throw "Timed out adding the dev signing certificate to CurrentUser Root. $approvalMessage"
        }

        if ($process.ExitCode -ne 0) {
            $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { Get-Content -LiteralPath $stderrPath -Raw } else { "" }
            throw "certutil failed adding the dev signing certificate to CurrentUser Root with exit code $($process.ExitCode). $stderr"
        }
    } finally {
        Remove-Item -LiteralPath $tempCertificate -Force -ErrorAction SilentlyContinue
    }

    return $true
}

function Add-CertificateToStore($Certificate, $StoreName) {
    if (Test-CertificateInStore $Certificate $StoreName) {
        return $false
    }

    if ($DryRun) {
        return $true
    }

    if ($StoreName -eq "Root") {
        return Add-CertificateToRootWithCertUtil $Certificate
    }

    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        $StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
    )
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $publicCertificate = $null
    try {
        $publicBytes = $Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
        $publicCertificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$publicBytes)
        $store.Add($publicCertificate)
    } finally {
        if ($publicCertificate -and $publicCertificate -is [System.IDisposable]) {
            $publicCertificate.Dispose()
        }
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
