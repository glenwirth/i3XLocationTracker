#Requires -RunAsAdministrator
# Stops and removes the Windows service and its installed files. The saved settings and token are removed
# only if you also delete C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\I3XLocationTracker.

$ErrorActionPreference = 'Stop'

$serviceName = 'I3XLocationTrackerWeb'
$installDir = Join-Path $env:ProgramFiles 'I3X Locations Tracker Web'

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', '00:00:30')
    }
    & sc.exe delete $serviceName
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE" }
}

if (Test-Path $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}

Write-Host 'Service removed.'
