#Requires -RunAsAdministrator
# Builds the web app and installs (or updates) it as an automatically starting Windows service.
# It runs as LocalService, so it keeps its own settings and token, separate from the desktop app.

$ErrorActionPreference = 'Stop'

$serviceName = 'I3XLocationTrackerWeb'
$installDir = Join-Path $env:ProgramFiles 'I3X Locations Tracker Web'
$exePath = Join-Path $installDir 'I3XLocationTracker.Web.exe'

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
        -DisplayName 'i3X Locations Tracker Web' `
        -Description 'Browser version of the i3X Locations Tracker, on port 5085.' `
        -StartupType Automatic | Out-Null
}

Invoke-Native 'sc.exe' @('config', $serviceName, 'obj=', 'NT AUTHORITY\LocalService')
Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', '00:00:30')

Write-Host ''
Write-Host 'Service installed and running. Open http://localhost:5085'
Write-Host 'First time only: set Auth to Bearer, enter the token, then click Connect.'
