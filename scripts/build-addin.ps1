param(
    [string] $Configuration = "Release",
    [string] $RevitApiPath = "$env:ProgramFiles\Autodesk\Revit 2024",
    [string] $DotnetPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($dotnet) {
        $DotnetPath = $dotnet.Source
    } else {
        $localDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
        if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
            $DotnetPath = $localDotnet
        }
    }
}

if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "dotnet.exe not found. Install the .NET SDK or pass -DotnetPath."
}

if (-not (Test-Path -LiteralPath (Join-Path $RevitApiPath "RevitAPI.dll") -PathType Leaf)) {
    throw "Revit API was not found at '$RevitApiPath'. Install Revit 2024 or pass -RevitApiPath."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "addin\RevitMcpNext.Addin\RevitMcpNext.Addin.csproj"

& $DotnetPath build $project -c $Configuration -p:RevitApiPath="$RevitApiPath"
