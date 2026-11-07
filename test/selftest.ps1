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

# full-mode verdict rules on synthetic frames (frame ms, CPU busy ms, GPU busy ms)
foreach ($c in @(@('16.7', '16.0', '9.0', 'CPU-bound'), @('16.7', '8.0', '16.2', 'GPU-bound'), @('16.7', '6.0', '7.0', 'neither'),
                 @('16.7', '15.8', '16.1', 'both near the limit'))) {
    $v = & $exe --judge $c[0] $c[1] $c[2]
    Check ($v -eq $c[3]) "full-mode verdict: frame $($c[0]) ms, CPU $($c[1]), GPU $($c[2]) -> $v"
}
# stale ETW session decision (issue #9): FSPerf_<pid> sessions whose pid is not a live fsperf are stopped; others never
$logman = "Name:                 Circular Kernel Context Logger`nName:                 FSPerf_111`nName:                 FSPerf_222   `nName:                 PresentMon_333`nName:                 XFSPerf_444`nName:                 FSPerf_notapid`n"
$stale = @($logman | & $exe --stale-sessions 222)
$logman2 = "FSPerf_111                  Trace   Running`nFSPerf_222                  Trace   Running`nPresentMon_333  Trace Running`n"
$stale2 = @($logman2 | & $exe --stale-sessions 222)
Check (($stale2 -join ',') -eq 'FSPerf_111') "stale sessions: dead fsperf's session stopped, live one and foreign ones kept ($($stale2 -join ','))"
Check ($stale.Count -eq 0) "stale sessions: only lines that start with FSPerf_<digits> count ($($stale -join ','))"
# the unprivileged cleanup on this machine must not crash fsperf (errors are logged, not fatal)
foreach ($c in @(@('fsperf', 'live'), @('FSPerf', 'live'), @('dotnet', 'live'), @('notepad', 'other'))) {
    $r = & $exe --is-fsperf-name $c[0]
    Check ($r -eq $c[1]) "a live '$($c[0])' process -> its ETW session is $(if ($c[1] -eq 'live') { 'kept' } else { 'stale' })"
}
# without admin / Performance Log Users, full mode reports why and basic mode goes on
$j = & $exe --once --pid $PID | ConvertFrom-Json
if (-not $j.elevated) { Check ($j.mode -eq 'basic' -and ($j.frames.state -in @('denied', 'off'))) "not elevated: full mode '$($j.frames.state)', basic mode used" }

# the monitored process exits mid-run (a test run's game quits or is killed): FSPerf keeps running ("waiting"), no crash
$b2 = Start-Process $exe -ArgumentList '--burn', '2' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 400
$mon = Start-Process $exe -ArgumentList '--no-browser', '--no-frames', '--pid', $b2.Id, '--seconds', '6', '--interval-ms', '250', '--port', '8799' -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $env:TEMP 'fsperf_selftest_err.txt') -RedirectStandardOutput (Join-Path $env:TEMP 'fsperf_selftest_out.txt')
$null = $mon.Handle   # keeps the handle, else ExitCode reads empty after the exit (PowerShell Start-Process quirk)
$mon.WaitForExit(15000) | Out-Null
$err = Get-Content (Join-Path $env:TEMP 'fsperf_selftest_err.txt') -Raw -ErrorAction SilentlyContinue
Check ($mon.HasExited -and $mon.ExitCode -eq 0 -and "$err" -notmatch 'Unhandled exception') "the game exiting under the monitor is no crash (exit $($mon.ExitCode))"

if ($fails) { Write-Host "FSPERF SELFTEST: $fails failure(s)"; exit 1 }
Write-Host "FSPERF SELFTEST OK"
exit 0
