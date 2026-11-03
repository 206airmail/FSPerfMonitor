# selftest.ps1 - FSPerf's offline checks against processes it starts itself (never the game):
#   1. a spinning .NET main thread (fsperf --burn): ~100% main-thread busy, CPU-bound verdict, samples at the asked rate
#   2. a native busy main thread (certutil hashing a sparse 2 GB file): attributed to its real module (bcryptprimitives)
#   3. the verdict rules (Collector.Judge) through --once on an idle process
# Exit 0 = all passed. Build first (build.ps1).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'src\bin\Release\net8.0-windows\win-x64\fsperf.exe'
if (-not (Test-Path $exe)) { Write-Host "build first: $exe missing"; exit 2 }
$fails = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:fails++ }
}
function Snap([int]$procId) { & $exe --once --pid $procId --interval-ms 2000 --sample-hz 250 | ConvertFrom-Json }

$b = Start-Process $exe -ArgumentList '--burn', '8' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 700
$j = Snap $b.Id
Check ($j.attached -and $j.mainThreadBusyPct -ge 85) "busy .NET main thread seen busy ($([math]::Round($j.mainThreadBusyPct))%)"
Check ($j.verdict.label -eq 'CPU-bound') "verdict CPU-bound ($($j.verdict.reason))"
Check ($j.mainThreadSamples -ge 300) "sampler rate: $($j.mainThreadSamples) samples in 2 s at 250 Hz"
$b.WaitForExit()

$big = Join-Path $env:TEMP 'fsperf_selftest_sparse.bin'
$f = [IO.File]::Create($big); $f.SetLength(2GB); $f.Close()
$c = Start-Process certutil -ArgumentList '-hashfile', $big, 'SHA512' -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 500
    $j = Snap $c.Id
    $top = $j.mainThreadModules | Select-Object -First 1
    Check ($top.name -eq 'bcryptprimitives.dll' -and $top.kind -eq 'Windows' -and $top.percent -ge 80) "native code attributed to its module ($($top.name) $([math]::Round($top.percent))%, $($top.kind))"
} finally {
    Stop-Process -Id $c.Id -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    Remove-Item $big -ErrorAction SilentlyContinue
}

$idle = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 6' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
$j = Snap $idle.Id
Check ($j.mainThreadBusyPct -lt 20 -and $j.verdict.label -eq 'neither') "idle process: low busy, verdict 'neither' ($([math]::Round($j.mainThreadBusyPct))%, $($j.verdict.label))"
$idle.WaitForExit()

if ($fails) { Write-Host "FSPERF SELFTEST: $fails failure(s)"; exit 1 }
Write-Host "FSPERF SELFTEST OK"
exit 0
