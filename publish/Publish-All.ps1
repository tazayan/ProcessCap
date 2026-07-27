#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [string] $PackageDirectory = (Join-Path $PSScriptRoot 'dist'),
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string] $Version = '1.0.0',
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'ProcessCap.cs'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$runtimeIdentifiers = @('win-x86', 'win-x64', 'win-arm64')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $PackageDirectory -Force | Out-Null

foreach ($runtimeIdentifier in $runtimeIdentifiers) {
    $publishDirectory = Join-Path $OutputDirectory $runtimeIdentifier
    Write-Host "Publishing ProcessCap for $runtimeIdentifier..." -ForegroundColor Cyan

    & dotnet publish $sourcePath `
        --configuration Release `
        --runtime $runtimeIdentifier `
        --self-contained $selfContained `
        -p:PublishAot=false `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        -p:Nullable=disable `
        -p:DebugType=none `
        -p:PublishDir="$publishDirectory\"

    if ($LASTEXITCODE -ne 0) {
        throw "Publishing ProcessCap for $runtimeIdentifier failed with exit code $LASTEXITCODE."
    }

    $archivePath = Join-Path $PackageDirectory "ProcessCap-$Version-$runtimeIdentifier.zip"
    Compress-Archive `
        -LiteralPath (Join-Path $publishDirectory 'ProcessCap.exe'), (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'LICENSE') `
        -DestinationPath $archivePath `
        -CompressionLevel Optimal `
        -Force

    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    Write-Host "Created $archivePath" -ForegroundColor Green
    Write-Host "SHA256 $($hash.Hash)" -ForegroundColor Gray
}

Write-Host "Published all architectures to $OutputDirectory and packaged them in $PackageDirectory" -ForegroundColor Green
