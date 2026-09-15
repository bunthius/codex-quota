[CmdletBinding()]
param([switch]$RemovePreferences)
$ErrorActionPreference = 'Stop'
$expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/CodexQuota')).TrimEnd('\')
$installedExe = Join-Path $expected 'CodexQuota.exe'
if (Test-Path -LiteralPath $expected) {
    $resolved = (Resolve-Path -LiteralPath $expected).Path.TrimEnd('\')
    if ($resolved -ne $expected -or (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to remove an unexpected or redirected installation path.'
    }
    $running = @(Get-Process -Name CodexQuota -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe })
    if ($running.Count -gt 0) {
        Start-Process -FilePath $installedExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
        foreach ($item in $running) { if (-not $item.WaitForExit(10000)) { throw 'Close Codex Quota from the tray and retry.' } }
    }
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CodexQuota' -ErrorAction SilentlyContinue
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Quota.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexQuota'
if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
if (Test-Path -LiteralPath $expected) { Remove-Item -LiteralPath $expected -Recurse -Force }
if ($RemovePreferences) {
    $dataExpected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexQuota')).TrimEnd('\')
    if (Test-Path -LiteralPath $dataExpected) {
        $dataResolved = (Resolve-Path -LiteralPath $dataExpected).Path.TrimEnd('\')
        if ($dataResolved -ne $dataExpected -or (Get-Item -LiteralPath $dataResolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to remove redirected preferences.'
        }
        Remove-Item -LiteralPath $dataResolved -Recurse -Force
    }
}
Write-Output 'Codex Quota was uninstalled. Your Codex account and app are unchanged.'
