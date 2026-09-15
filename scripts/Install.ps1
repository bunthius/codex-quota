[CmdletBinding()]
param([string]$PackageDirectory, [switch]$NoStart, [switch]$NoStartup)
$ErrorActionPreference = 'Stop'
if (-not $PackageDirectory) {
    $besideScript = Join-Path $PSScriptRoot 'CodexQuota.exe'
    if (Test-Path -LiteralPath $besideScript) { $PackageDirectory = $PSScriptRoot }
    else { $PackageDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish' }
}
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packageExe = Join-Path $packagePath 'CodexQuota.exe'
if (-not (Test-Path -LiteralPath $packageExe)) { throw 'Build the app first with scripts/Build.ps1, or use a complete release package.' }
$installPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/CodexQuota'))
$installedExe = Join-Path $installPath 'CodexQuota.exe'
if ($packagePath.TrimEnd('\') -eq $installPath.TrimEnd('\')) { throw 'Run the installer from the source project or extracted release, not the installed folder.' }
New-Item -ItemType Directory -Path $installPath -Force | Out-Null
$running = @(Get-Process -Name CodexQuota -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe })
if ($running.Count -gt 0) {
    Start-Process -FilePath $installedExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
    foreach ($item in $running) {
        if (-not $item.WaitForExit(10000)) { throw 'Codex Quota did not exit. Close it from the tray and retry.' }
    }
}
Copy-Item -LiteralPath $packageExe -Destination $installedExe -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $installPath 'Uninstall.ps1') -Force
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not $NoStartup) {
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name 'CodexQuota' -Value ('"' + $installedExe + '" --startup') -PropertyType String -Force | Out-Null
}
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Quota.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installPath
$shortcut.Description = 'Codex account quota in your system tray'
$shortcut.Save()
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexQuota'
New-Item -Path $uninstallKey -Force | Out-Null
$uninstallCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installPath 'Uninstall.ps1') + '"'
foreach ($entry in @{
    DisplayName = 'Codex Quota'; DisplayVersion = '1.0.0'; Publisher = 'bunthius';
    InstallLocation = $installPath; DisplayIcon = $installedExe; UninstallString = $uninstallCommand;
    URLInfoAbout = 'https://github.com/bunthius/codex-quota'
}.GetEnumerator()) { New-ItemProperty -Path $uninstallKey -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $uninstallKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null
if (-not $NoStart) { Start-Process -FilePath $installedExe -ArgumentList '--startup' -WorkingDirectory $installPath -WindowStyle Hidden }
Write-Output ('Installed: ' + $installedExe)
Write-Output ('Start with Windows: ' + (-not $NoStartup))
