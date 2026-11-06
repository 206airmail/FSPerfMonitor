# Restart FSPerf headless with --lan (run in an ADMIN PowerShell; full mode needs admin).
$exe = Join-Path $PSScriptRoot 'src\bin\Release\net8.0-windows\win-x64\fsperf.exe'
Get-Process -Name fsperf -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Start-Process -FilePath $exe -ArgumentList '--lan', '--no-browser' -WindowStyle Hidden
Start-Sleep -Seconds 3
Get-NetTCPConnection -LocalPort 8765 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess
