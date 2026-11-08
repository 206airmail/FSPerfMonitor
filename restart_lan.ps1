# Restart SVS Performance Monitor headless with --lan (run in an ADMIN PowerShell; frame times need admin or the
# Performance Log Users group). Developer convenience until the tray app (0.4) replaces it.
# A killed monitor leaves its ETW session SVSPerf_<pid> running, which starves PresentMon: stop every such session
# after the kill, before the new start.
$exe = Join-Path $PSScriptRoot 'src\bin\Release\net10.0-windows\win-x64\SVSPerfMonitor.exe'
Get-Process -Name SVSPerfMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
foreach ($s in (logman query -ets | Select-String -Pattern '^(SVSPerf_\d+)\s' | ForEach-Object { $_.Matches[0].Groups[1].Value })) {
    logman stop $s -ets | Out-Null
    "stopped stale ETW session $s"
}
Start-Process -FilePath $exe -ArgumentList '--lan', '--no-browser' -WindowStyle Hidden
Start-Sleep -Seconds 3
Get-NetTCPConnection -LocalPort 8765 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
