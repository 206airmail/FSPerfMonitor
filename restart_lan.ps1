# Restart FSPerf headless with --lan (run in an ADMIN PowerShell; full mode needs admin).
# A killed fsperf leaves its ETW session FSPerf_<pid> running, which starves PresentMon (issue #9): stop every
# FSPerf_* session after the kill, before the new start.
$exe = Join-Path $PSScriptRoot 'src\bin\Release\net8.0-windows\win-x64\fsperf.exe'
Get-Process -Name fsperf -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
foreach ($s in (logman query -ets | Select-String -Pattern '^(FSPerf_\d+)\s' | ForEach-Object { $_.Matches[0].Groups[1].Value })) {
    logman stop $s -ets | Out-Null
    "stopped stale ETW session $s"
}
Start-Process -FilePath $exe -ArgumentList '--lan', '--no-browser' -WindowStyle Hidden
Start-Sleep -Seconds 3
Get-NetTCPConnection -LocalPort 8765 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
