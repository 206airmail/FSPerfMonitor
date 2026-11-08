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
# (a member of BUILTIN\Performance Log Users, SID S-1-5-32-559, may read the events without admin: PresentMon starts)
$perfLogUser = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.SecurityIdentifier]'S-1-5-32-559')
if ($j.frames.state -eq 'off' -and "$($j.frames.hint)" -like 'PresentMon not found*') {
    # a plain build.ps1 does not download PresentMon: full mode is off for that reason, whatever the permissions
    Check ($j.mode -eq 'basic') "PresentMon not installed: full mode off, basic mode used (build.ps1 -FetchPresentMon for full mode)"
} elseif (-not $j.elevated -and $perfLogUser) {
    Check ($j.frames.state -in @('starting', 'running')) "not elevated, in Performance Log Users: full mode allowed ('$($j.frames.state)')"
} elseif (-not $j.elevated) {
    Check ($j.mode -eq 'basic' -and ($j.frames.state -in @('denied', 'off'))) "not elevated: full mode '$($j.frames.state)', basic mode used"
}

# the monitored process exits mid-run (a test run's game quits or is killed): FSPerf keeps running ("waiting"), no crash
$b2 = Start-Process $exe -ArgumentList '--burn', '2' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 400
$mon = Start-Process $exe -ArgumentList '--no-browser', '--no-frames', '--pid', $b2.Id, '--seconds', '6', '--interval-ms', '250', '--port', '8799' -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $env:TEMP 'fsperf_selftest_err.txt') -RedirectStandardOutput (Join-Path $env:TEMP 'fsperf_selftest_out.txt')
$null = $mon.Handle   # keeps the handle, else ExitCode reads empty after the exit (PowerShell Start-Process quirk)
$mon.WaitForExit(15000) | Out-Null
$err = Get-Content (Join-Path $env:TEMP 'fsperf_selftest_err.txt') -Raw -ErrorAction SilentlyContinue
Check ($mon.HasExited -and $mon.ExitCode -eq 0 -and "$err" -notmatch 'Unhandled exception') "the game exiting under the monitor is no crash (exit $($mon.ExitCode))"

# 0.3 log tail: the tailer against temp files (appends, partial line, truncate/recreate, ring bounds, resume)
$lt = & $exe --logtail-selftest
$lt | ForEach-Object { Write-Host $_ }
Check ($LASTEXITCODE -eq 0 -and ($lt -join "`n") -match 'LOGTAIL SELFTEST OK') "log tailer checks passed"

# 0.3 log stream end to end: a fake profile dir with a temp log.txt, FSPerf on port 8799
$prof = Join-Path $env:TEMP ('fsperf_selftest_prof_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory $prof | Out-Null
$logf = Join-Path $prof 'log.txt'
[IO.File]::WriteAllText($logf, "first line`nWarning: something odd`n")
$srv = Start-Process $exe -ArgumentList '--no-browser', '--no-frames', '--profile', "`"$prof`"", '--seconds', '30', '--port', '8799' -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 1200
    $page = (curl.exe -s http://127.0.0.1:8799/log) -join "`n"
    Check ($page -match '<title>FSPerf log</title>' -and $page -match 'href="/"') "GET /log serves the log page (links back to the dashboard)"
    Check (((curl.exe -s http://127.0.0.1:8799/) -join "`n") -match 'href="/log"') "the dashboard links to /log"
    $first = (curl.exe -siN --max-time 2 'http://127.0.0.1:8799/log/stream?n=10') -join "`n"
    Check ($first -match 'text/event-stream' -and $first -match '"reset":true' -and $first -match 'first line' -and $first -match 'Warning: something odd') "stream: a new client gets the recent lines first"
    $lastId = ([regex]::Matches($first, '(?m)^id: (\S+)') | Select-Object -Last 1).Groups[1].Value
    $f = [IO.File]::Open($logf, 'Append', 'Write', 'ReadWrite, Delete')
    $b = [Text.Encoding]::UTF8.GetBytes("added while away`nError: boom`n"); $f.Write($b, 0, $b.Length); $f.Flush()
    Start-Sleep -Milliseconds 1000
    $resumed = (curl.exe -sN --max-time 2 -H "Last-Event-ID: $lastId" 'http://127.0.0.1:8799/log/stream?n=10') -join "`n"
    Check ($resumed -match 'added while away' -and $resumed -match '"reset":false' -and $resumed -notmatch 'first line') "stream: Last-Event-ID resumes without duplicates ($lastId)"
    # live push: connect, append mid-stream, the line must arrive in the same connection
    $job = Start-Job { param($id, $u) curl.exe -sN --max-time 3 -H "Last-Event-ID: $id" $u } -ArgumentList $lastId, 'http://127.0.0.1:8799/log/stream'
    Start-Sleep -Milliseconds 800
    $b = [Text.Encoding]::UTF8.GetBytes("pushed live`n"); $f.Write($b, 0, $b.Length); $f.Flush()
    $live = (Receive-Job $job -Wait -AutoRemoveJob) -join "`n"
    Check ($live -match 'pushed live') "stream: a line appended later is pushed to a connected client"
    # a client that never reads must not delay another client or the tailer (all socket I/O is off the tailer's lock)
    $stall = New-Object Net.Sockets.TcpClient
    $stall.ReceiveBufferSize = 1024
    $stall.Connect('127.0.0.1', 8799)
    $sreq = [Text.Encoding]::ASCII.GetBytes("GET /log/stream?n=5000 HTTP/1.1`r`nHost: 127.0.0.1:8799`r`n`r`n")
    $stall.GetStream().Write($sreq, 0, $sreq.Length)          # ... and never reads
    $outB = Join-Path $env:TEMP 'fsperf_selftest_b.txt'
    Remove-Item $outB -ErrorAction SilentlyContinue
    $jobB = Start-Job { param($id, $o) curl.exe -sN --max-time 8 -H "Last-Event-ID: $id" -o $o 'http://127.0.0.1:8799/log/stream' } -ArgumentList $lastId, $outB
    Start-Sleep -Milliseconds 800
    $chunk = [Text.Encoding]::UTF8.GetBytes((("z" * 3000) + "`n") * 500)       # 1.5 MB per write, 8 MB in all
    for ($i = 0; $i -lt 5; $i++) { $f.Write($chunk, 0, $chunk.Length) }
    $f.Flush()
    $mk = [Text.Encoding]::UTF8.GetBytes("MARKER_AFTER_STALL`n"); $f.Write($mk, 0, $mk.Length); $f.Flush()
    $sw = [Diagnostics.Stopwatch]::StartNew(); $got = $false
    while ($sw.ElapsedMilliseconds -lt 5000 -and -not $got) {
        Start-Sleep -Milliseconds 100
        try { $got = [bool](Select-String -Path $outB -SimpleMatch 'MARKER_AFTER_STALL' -Quiet) } catch { }
    }
    Check $got "stalled client: another client still gets live lines ($($sw.ElapsedMilliseconds) ms after 8 MB of log growth)"
    Receive-Job $jobB -Wait -AutoRemoveJob | Out-Null
    $n3 = (curl.exe -siN --max-time 2 'http://127.0.0.1:8799/log/stream?n=1') -join "`n"
    Check ($n3 -match '200 OK') "stalled client: new viewers are still accepted"
    $stall.Dispose()
    Remove-Item $outB -ErrorAction SilentlyContinue
    $f.Dispose()
    # Host header (DNS rebinding): every route checks it
    foreach ($r in '/', '/api/snapshot', '/log', '/log/stream', '/events') {
        $code = curl.exe -s -o NUL -w '%{http_code}' --max-time 3 -H 'Host: evil.example:8799' "http://127.0.0.1:8799$r"
        Check ($code -eq '403') "Host: evil.example:8799 on $r -> $code (want 403)"
    }
    Check ((curl.exe -s -o NUL -w '%{http_code}' --max-time 3 -H 'Host: 127.0.0.1:9999' http://127.0.0.1:8799/) -eq '403') "Host with the wrong port -> 403"
    Check ((curl.exe -s -o NUL -w '%{http_code}' --max-time 3 -H 'Host:' http://127.0.0.1:8799/) -eq '400') "no Host header -> 400"
    foreach ($h in 'localhost:8799', '127.0.0.1:8799', '[::1]:8799') {
        Check ((curl.exe -s -o NUL -w '%{http_code}' --max-time 3 -H "Host: $h" http://127.0.0.1:8799/api/snapshot) -eq '200') "Host: $h is accepted"
    }
    # ?after= must not override the Last-Event-ID header
    $id2 = ([regex]::Matches($n3, '(?m)^id: (\S+)') | Select-Object -Last 1).Groups[1].Value
    $ep = $id2.Split('.')[0]
    [IO.File]::AppendAllText($logf, "header wins line`n")
    Start-Sleep -Milliseconds 900
    $hw = (curl.exe -sN --max-time 2 -H "Last-Event-ID: $id2" "http://127.0.0.1:8799/log/stream?n=1&after=$ep.0") -join "`n"
    Check ($hw -match 'header wins line' -and $hw -notmatch 'first line' -and $hw -notmatch 'zzzzzzzzzz' -and $hw -match '"reset":false') "Last-Event-ID header wins over ?after= (no lines 1..N again)"
    $qo = (curl.exe -sN --max-time 2 "http://127.0.0.1:8799/log/stream?n=1&after=$id2") -join "`n"
    Check ($qo -match 'header wins line' -and $qo -match '"reset":false') "?after= alone still resumes"
} finally {
    if (-not $srv.HasExited) { Stop-Process -Id $srv.Id -ErrorAction SilentlyContinue }
    Remove-Item $prof -Recurse -ErrorAction SilentlyContinue
}

if ($fails) { Write-Host "FSPERF SELFTEST: $fails failure(s)"; exit 1 }
Write-Host "FSPERF SELFTEST OK"
exit 0
