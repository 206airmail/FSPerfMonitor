# Restart SVS Performance Monitor as the tray app with --lan, as admin (developer convenience; run in an ADMIN
# PowerShell: frame times need admin or the Performance Log Users group). Uses the published files (build.ps1 -Publish).
# --exit stops a running copy cleanly (its frame capture too), so no ETW session is left behind.
$com = Join-Path $PSScriptRoot 'publish\SVSPerfMonitor.com'
& $com --exit | Out-Null
Start-Process -FilePath (Join-Path $PSScriptRoot 'publish\SVSPerfMonitor.exe') -ArgumentList '--tray', '--lan', '--no-browser'
Start-Sleep -Seconds 3
Get-NetTCPConnection -LocalPort 8765 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
