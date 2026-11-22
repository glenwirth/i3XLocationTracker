#Requires -RunAsAdministrator
# Builds the web app and installs (or updates) it as an automatically starting Windows service.
# It runs as LocalService, so it keeps its own settings and token, separate from your Windows account's.

$ErrorActionPreference = 'Stop'

$serviceName = 'I3XLocationTrackerWeb'
$displayName = 'i3X Location Tracker Web'
$description = 'Browser version of the i3X Location Tracker, on port 5085.'
$installDir = Join-Path $env:ProgramFiles 'I3X Location Tracker Web'
$exePath = Join-Path $installDir 'I3XLocationTracker.Web.exe'
# Folder name used before "Locations" was renamed to "Location"; removed when updating an older install.
$legacyInstallDir = Join-Path $env:ProgramFiles 'I3X Locations Tracker Web'

function Invoke-Native {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus('Stopped', '00:00:30')
}

Invoke-Native 'dotnet' @('publish', (Join-Path $PSScriptRoot 'I3XLocationTracker.Web.csproj'), '-c', 'Release', '-o', $installDir)

if (-not $existing) {
    New-Service -Name $serviceName `
        -BinaryPathName "`"$exePath`"" `
        -DisplayName $displayName `
        -Description $description `
        -StartupType Automatic | Out-Null
} else {
    # Point an existing service at the current install folder and name.
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath -Value "`"$exePath`""
    Set-Service -Name $serviceName -DisplayName $displayName -Description $description
}

Invoke-Native 'sc.exe' @('config', $serviceName, 'obj=', 'NT AUTHORITY\LocalService')
Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')

if (Test-Path $legacyInstallDir) {
    Remove-Item -LiteralPath $legacyInstallDir -Recurse -Force
}

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', '00:00:30')

Write-Host ''
Write-Host 'Service installed and running. Open http://localhost:5085'
Write-Host 'First time only: set Auth to Bearer, enter the token, then click Connect.'
