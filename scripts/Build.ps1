[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipTests) {
        & dotnet run --project tests/CodexQuota.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Quota tests failed.' }
    }
    & dotnet publish src/CodexQuota/CodexQuota.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination (Join-Path $projectRoot 'artifacts/publish/Install.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $projectRoot 'artifacts/publish/Uninstall.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $projectRoot 'artifacts/publish/README.md') -Force
    Write-Output (Join-Path $projectRoot 'artifacts/publish')
} finally { Pop-Location }
