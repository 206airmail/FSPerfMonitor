# selftest.ps1 - FSPerf's offline checks against processes it starts itself (never the game):
#   1. a spinning .NET main thread (fsperf --burn): ~100% main-thread busy, CPU-bound verdict, samples at the asked rate
#   2. a native busy main thread (certutil hashing a sparse 2 GB file): attributed to its real module (bcryptprimitives)
#   3. the verdict rules (Collector.Judge) through --once on an idle process
# Exit 0 = all passed. Build first (build.ps1). -Exe tests another build (e.g. the native publish\SVSPerfMonitor.exe).
# -Lan also checks the read-only view from this PC's network address: that listens on all interfaces, and Windows then
# asks to allow the test build through the firewall (a rule for Private AND Public networks if allowed): only on request.
param([string]$Exe, [switch]$Lan)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = if ($Exe) { (Resolve-Path $Exe).Path } else { Join-Path $root 'src\bin\Release\net10.0-windows\win-x64\SVSPerfMonitor.exe' }
if (-not (Test-Path $exe)) { Write-Host "build first: $exe missing"; exit 2 }
$fails = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:fails++ }
}
function Snap([int]$procId) {   # stderr = start-up notices (e.g. a stale session cleaned up); PS 5.1 + 'Stop' would throw on them even with 2>$null
    $ErrorActionPreference = 'Continue'
    & $exe --once --pid $procId --interval-ms 2000 --sample-hz 250 2>$null | ConvertFrom-Json
}

$b = Start-Process $exe -ArgumentList '--burn', '12' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 700
$j = Snap $b.Id
Check ($j.attached -and $j.mainThreadBusyPct -ge 85) "busy .NET main thread seen busy ($([math]::Round($j.mainThreadBusyPct))%)"
Check ($j.verdict.label -eq 'CPU-bound') "verdict CPU-bound ($($j.verdict.reason))"
Check ($j.mainThreadSamples -ge 300 -and $j.sampleHz -eq 250) "sampler rate: $($j.mainThreadSamples) samples in 2 s at 250 Hz (opt-in)"
# the main-thread breakdown is opt-in (#2): by default and with --sample-hz 0 the game's thread is never suspended
foreach ($a in @(@(), @('--sample-hz', '0'))) {
    $j0 = & $exe --once --pid $b.Id --interval-ms 1000 --no-frames @a | ConvertFrom-Json
    Check ($j0.attached -and $j0.sampleHz -eq 0 -and $j0.mainThreadSamples -eq 0 -and @($j0.mainThreadModules).Count -eq 0 -and -not $j0.note -and $j0.mainThreadBusyPct -ge 85) "breakdown off $(if ($a) { 'with --sample-hz 0' } else { 'by default' }): 0 samples, no note, CPU busy still measured"
}
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
# stale ETW session decision (issue #9): SVSPerf_<pid> sessions whose pid is not a live monitor are stopped; others never
$logman = "Name:                 Circular Kernel Context Logger`nName:                 SVSPerf_111`nName:                 SVSPerf_222   `nName:                 PresentMon_333`nName:                 XSVSPerf_444`nName:                 SVSPerf_notapid`n"
$stale = @($logman | & $exe --stale-sessions 222)
$logman2 = "SVSPerf_111                 Trace   Running`nSVSPerf_222                 Trace   Running`nPresentMon_333  Trace Running`n"
$stale2 = @($logman2 | & $exe --stale-sessions 222)
Check (($stale2 -join ',') -eq 'SVSPerf_111') "stale sessions: dead monitor's session stopped, live one and foreign ones kept ($($stale2 -join ','))"
Check ($stale.Count -eq 0) "stale sessions: only lines that start with SVSPerf_<digits> count ($($stale -join ','))"
# the unprivileged cleanup on this machine must not crash the monitor (errors are logged, not fatal)
foreach ($c in @(@('SVSPerfMonitor', 'live'), @('svsperfmonitor', 'live'), @('SVSPerfMonitor.com', 'live'), @('dotnet', 'live'), @('fsperf', 'other'), @('notepad', 'other'))) {
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

# 0.4 tray app (#5), under its own instance name and port (never a real running copy): one copy only, a clean --exit
$inst = 'SVSPerfSelfTest_' + [guid]::NewGuid().ToString('N').Substring(0, 6)
# (--tray-child: the tray itself, never its console-less restart, so $t1 is the process that runs on every Windows)
$t1 = Start-Process $exe -ArgumentList '--tray', '--tray-child', '--no-browser', '--no-frames', '--port', '8798', '--instance', $inst -PassThru
$null = $t1.Handle
$up = $false
for ($i = 0; $i -lt 40 -and -not $up; $i++) { Start-Sleep -Milliseconds 250; try { $null = Invoke-WebRequest 'http://127.0.0.1:8798/api/snapshot' -UseBasicParsing -TimeoutSec 2; $up = $true } catch { } }
Check ($up -and -not $t1.HasExited) "tray app: running, dashboard answers on its port"
$t2 = Start-Process $exe -ArgumentList '--tray', '--tray-child', '--no-browser', '--no-frames', '--port', '8798', '--instance', $inst -PassThru
$null = $t2.Handle
Check ($t2.WaitForExit(5000) -and $t2.ExitCode -eq 0 -and -not $t1.HasExited) "tray app: a second start leaves the first one running and exits (exit $($t2.ExitCode))"
$ex = & $exe --exit --instance $inst
$exCode = $LASTEXITCODE
Check ($exCode -eq 0 -and $t1.WaitForExit(15000) -and $t1.ExitCode -eq 0) "tray app: --exit stops it cleanly ('$ex', exit $($t1.ExitCode))"
$closed = $false; try { $null = Invoke-WebRequest 'http://127.0.0.1:8798/' -UseBasicParsing -TimeoutSec 2 } catch { $closed = $true }
Check $closed "tray app: its port is closed after the exit"
$ex2 = & $exe --exit --instance $inst
Check ($LASTEXITCODE -eq 1) "--exit with nothing running says so ('$ex2')"
if (-not $t1.HasExited) { Stop-Process -Id $t1.Id -Force }

# 0.4 settings (#6): the tray app with its own settings file (never the real one), own instance and ports
function Post([string]$url, [string]$body, [string]$origin, [string]$ctype = 'application/json', [string]$token = $script:tok) {
    $h = @{}; if ($origin) { $h['Origin'] = $origin }; if ($token) { $h['X-SVS-Token'] = $token }
    try { $r = Invoke-WebRequest $url -Method Post -Body $body -ContentType $ctype -Headers $h -UseBasicParsing -TimeoutSec 5; return [int]$r.StatusCode }
    catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode } return -1 }
}
function WaitUp([int]$port) { for ($i = 0; $i -lt 40; $i++) { try { return (Invoke-WebRequest "http://127.0.0.1:$port/api/settings" -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 250 } }; return $null }
$sf = Join-Path $env:TEMP ('svsperf_selftest_settings_' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.json')
'{"port":8798,"openDashboardOnStart":false,"mainLoopBreakdown":false}' | Set-Content $sf -Encoding ascii
$inst = 'SVSPerfSelfTest_' + [guid]::NewGuid().ToString('N').Substring(0, 6)
$stArgs = @('--tray', '--tray-child', '--no-frames', '--settings', "`"$sf`"", '--instance', $inst)   # quoted: PS 5.1 joins -ArgumentList without quoting + @(if ($Lan) { '--lan' })
$st = Start-Process $exe -ArgumentList $stArgs -PassThru
$null = $st.Handle
$v = WaitUp 8798
$script:tok = $v.token
Check ($v -and $v.canEdit -and $v.token.Length -eq 32 -and $v.settings.port -eq 8798 -and -not $v.settings.openDashboardOnStart) "settings: the tray app reads its settings file; editable from this PC"
$page = try { (Invoke-WebRequest 'http://127.0.0.1:8798/settings' -UseBasicParsing).Content } catch { '' }
$css = try { (Invoke-WebRequest 'http://127.0.0.1:8798/assets/styles.css' -UseBasicParsing).StatusCode } catch { 0 }
Check ($page -match '<h1>Settings</h1>' -and $css -eq 200) "settings: the page and its style sheet are served"
$good = 'http://127.0.0.1:8798'
$body = '{"version":1,"port":8798,"openDashboardOnStart":false,"mainLoopBreakdown":true,"profileFolder":""}'
Check ((Post "$good/api/settings" $body '') -eq 403) "settings: a save without an Origin is refused"
Check ((Post "$good/api/settings" $body 'http://evil.example') -eq 403) "settings: a save from another site's page is refused"
Check ((Post "$good/api/settings" $body $good 'text/plain') -eq 415) "settings: a save that is not JSON is refused"
Check ((Post "$good/api/settings" '{"port":70000}' $good) -eq 422) "settings: an impossible port is refused"
Check ((Post "$good/api/settings" $body $good 'application/json' 'not-the-token') -eq 403) "settings: a save without this page's token is refused"
Check ((Post "$good/api/settings" '{"port":8798,"profileFolder":"\\\\server\\share"}' $good) -eq 422) "settings: a network folder (\\server\share) is refused"
Check ((Post "$good/api/settings" '{"port":70000,"profileFolder":null}' $good) -eq 422) "settings: a null folder is no crash (answered, not dropped)"
$lanIp = if ($Lan) { (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' -and $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1).IPAddress }
if ($lanIp) {
    $rv = try { (Invoke-WebRequest "http://${lanIp}:8798/api/settings" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json } catch { $null }
    Check ($rv -and -not $rv.canEdit -and (Post "http://${lanIp}:8798/api/settings" $body "http://${lanIp}:8798") -eq 403) "settings: from the home network (${lanIp}) they are read-only"
}
Check ((Post "$good/api/settings" $body $good) -eq 200) "settings: a save from this PC's dashboard is accepted"
Start-Sleep -Milliseconds 1500
$script:tok = (WaitUp 8798).token   # a restarted server has a new token
$sn = $null; for ($i = 0; $i -lt 12 -and -not ($sn -and $sn.sampleHz -eq 250); $i++) { Start-Sleep -Milliseconds 500; $sn = try { (Invoke-WebRequest "$good/api/snapshot" -UseBasicParsing).Content | ConvertFrom-Json } catch { $null } }
$saved = Get-Content $sf -Raw | ConvertFrom-Json
Check ($saved.mainLoopBreakdown -eq $true -and $sn.sampleHz -eq 250) "settings: saved to the file and applied after the restart (breakdown on: $($sn.sampleHz) Hz)"
$body2 = '{"version":1,"port":8794,"openDashboardOnStart":false,"mainLoopBreakdown":false,"profileFolder":""}'
Check ((Post "$good/api/settings" $body2 $good) -eq 200) "settings: a new port is accepted"
$v2 = WaitUp 8794
$old = try { $null = Invoke-WebRequest "$good/" -UseBasicParsing -TimeoutSec 2; 'open' } catch { 'closed' }
Check ($v2 -and $v2.settings.port -eq 8794 -and $old -eq 'closed') "settings: the dashboard moved to the new port (old one $old)"
# 0.4 start-up (#7) without a Windows permission prompt: start with Windows = a Run entry under the test's own name
$script:tok = (WaitUp 8794).token
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
try {
    $b3 = '{"version":1,"port":8794,"openDashboardOnStart":false,"mainLoopBreakdown":false,"profileFolder":"","startWithWindows":true}'
    $ok3 = (Post 'http://127.0.0.1:8794/api/settings' $b3 'http://127.0.0.1:8794') -eq 200
    $entry = (Get-ItemProperty $runKey -Name $inst -ErrorAction SilentlyContinue).$inst
    Check ($ok3 -and $entry -match '--tray --autostart' -and $entry -match [regex]::Escape("--instance $inst")) "start with Windows: on = a Run entry that starts the tray app quietly ($entry)"
    $script:tok = (WaitUp 8794).token
    $b4 = $b3.Replace('"startWithWindows":true', '"startWithWindows":false')
    $ok4 = (Post 'http://127.0.0.1:8794/api/settings' $b4 'http://127.0.0.1:8794') -eq 200
    Check ($ok4 -and $null -eq (Get-ItemProperty $runKey -Name $inst -ErrorAction SilentlyContinue)) "start with Windows: off = the Run entry is gone"
} finally {
    Remove-ItemProperty $runKey -Name $inst -ErrorAction SilentlyContinue   # never leave a test entry behind
}
$v5 = WaitUp 8794
Check ($v5.frameTimes -in @('ready', 'signout', 'setup', 'missing')) "settings: the page learns the frame-times state ($($v5.frameTimes))"
$null = & $exe --exit --instance $inst
Check ($LASTEXITCODE -eq 0 -and $st.WaitForExit(15000)) "settings: the tray app still exits cleanly"

# frame times: the state matches this login (Performance Log Users in the token, or elevated = ready)
$fa = & $exe --frametimes-access
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Check (($fa -eq 'ready') -eq ($perfLogUser -or $isAdmin)) "frame times: '$fa' matches this login (in the group: $perfLogUser, elevated: $isAdmin)"
# full permissions: the task definition is valid for Windows' Task Scheduler (parsed, never registered)
foreach ($logon in '1', '0') {
    $xml = (& $exe --task-xml $logon SVSPerfSelfTest) -join "`n"
    $parsed = $false; $runLevel = -1; $triggers = -1
    try {
        $svc = New-Object -ComObject Schedule.Service; $svc.Connect()
        $def = $svc.NewTask(0); $def.XmlText = $xml
        $parsed = $true; $runLevel = $def.Principal.RunLevel; $triggers = $def.Triggers.Count
    } catch { $err = $_.Exception.Message }
    Check ($parsed -and $runLevel -eq 1 -and $triggers -eq [int]$logon) "full permissions: the task definition is valid (logon trigger $logon, highest run level) $err"
}
# the admin helper refuses to work without admin rights (it never prompts by itself)
if (-not $isAdmin) { $null = & $exe --admin task-delete SVSPerfSelfTest; Check ($LASTEXITCODE -eq 1) "the admin helper refuses without admin rights" }
Remove-Item $sf -ErrorAction SilentlyContinue

# the published pair (-Exe publish\SVSPerfMonitor.com): the starter SVSPerfMonitor.exe next to it
$starter = [IO.Path]::ChangeExtension($exe, '.exe')
if ($exe -like '*.com' -and (Test-Path $starter)) {
    $inst = 'SVSPerfSelfTest_' + [guid]::NewGuid().ToString('N').Substring(0, 6)
    $s = Start-Process $starter -ArgumentList '--tray', '--no-browser', '--no-frames', '--port', '8798', '--instance', $inst -PassThru
    $null = $s.Handle
    $sOk = $s.WaitForExit(5000) -and $s.ExitCode -eq 0
    Start-Sleep -Milliseconds 1500
    $tc = @(Get-CimInstance Win32_Process -Filter "Name='SVSPerfMonitor.com'" | Where-Object { $_.CommandLine -match $inst -and $_.CommandLine -match '--tray-child' })
    $up = $false; for ($i = 0; $i -lt 20 -and -not $up; $i++) { try { $null = Invoke-WebRequest 'http://127.0.0.1:8798/api/snapshot' -UseBasicParsing -TimeoutSec 2; $up = $true } catch { Start-Sleep -Milliseconds 250 } }
    Check ($sOk -and $tc.Count -eq 1 -and $up) "starter: SVSPerfMonitor.exe starts the tray program without a console window and leaves (copies: $($tc.Count))"
    $ex = & $exe --exit --instance $inst
    Check ($LASTEXITCODE -eq 0) "starter: the tray copy it started stops cleanly with --exit ('$ex')"
    # the command line through the starter: output and exit code pass through (piped, as scripts do)
    $sj = & $starter --once --pid $PID --no-frames | ConvertFrom-Json
    Check ($sj.attached -and $sj.pid -eq $PID) "starter: command line through SVSPerfMonitor.exe passes the output through"
    # a quoted value containing " --tray " is one argument, not the tray option (the starter parses quotes)
    $sq = & $starter --once --pid $PID --no-frames --profile "$env:TEMP\x --tray y" | ConvertFrom-Json
    Check ($sq.attached -and $sq.pid -eq $PID) "starter: --tray inside a quoted value is not the tray option"
    # a bare name finds the .com first (PATHEXT): what `SVSPerfMonitor` in a terminal runs
    Push-Location (Split-Path $exe)
    $resolved = (Get-Command .\SVSPerfMonitor -ErrorAction SilentlyContinue).Source
    Pop-Location
    Check ($resolved -like '*.com') "a bare 'SVSPerfMonitor' resolves to the .com program ($resolved)"
}

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
    Check ($page -match '<title>SVS Performance Monitor log</title>' -and $page -match 'href="/"') "GET /log serves the log page (links back to the dashboard)"
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

if ($fails) { Write-Host "SVSPERFMONITOR SELFTEST: $fails failure(s)"; exit 1 }
Write-Host "SVSPERFMONITOR SELFTEST OK"
exit 0
