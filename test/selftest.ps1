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
# every tray app the test starts uses its own settings file: never the user's real %APPDATA% one
$testSettings = Join-Path $env:TEMP ('svsperf_selftest_tray_' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.json')
$fails = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:fails++ }
}
# runs the program for its JSON output. stderr = start-up notices (a stale session cleaned up, logman busy): ignored.
# Windows PowerShell 5.1 with 'Stop' would throw on them even with 2>$null, so 'Continue' inside.
function Json([string]$file, [object[]]$a) { $ErrorActionPreference = 'Continue'; & $file @a 2>$null | ConvertFrom-Json }
function Snap([int]$procId) { Json $exe @('--once', '--pid', $procId, '--interval-ms', '2000', '--sample-hz', '250') }

$b = Start-Process $exe -ArgumentList '--burn', '20' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 700
# (#12) on a busy shared PC a spinning thread can measure just under the 90% CPU-bound line: up to two more
# measurements, but only for a load-like shortfall (still clearly busy) - a real regression stays a failure
$j = Snap $b.Id
for ($try = 0; $try -lt 2 -and $j.attached -and $j.mainThreadBusyPct -gt 50 -and ($j.mainThreadBusyPct -lt 90 -or $j.verdict.label -ne 'CPU-bound'); $try++) {
    Write-Host "  ..   busy thread measured $([math]::Round($j.mainThreadBusyPct))% (machine load?): measuring again"
    $j = Snap $b.Id
}
Check ($j.attached -and $j.mainThreadBusyPct -ge 85) "busy .NET main thread seen busy ($([math]::Round($j.mainThreadBusyPct))%)"
Check ($j.verdict.label -eq 'CPU-bound') "verdict CPU-bound ($($j.verdict.reason))"
Check ($j.mainThreadSamples -ge 300 -and $j.sampleHz -eq 250) "sampler rate: $($j.mainThreadSamples) samples in 2 s at 250 Hz (opt-in)"
# the main-thread breakdown is opt-in (#2): by default and with --sample-hz 0 the game's thread is never suspended
foreach ($a in @(@(), @('--sample-hz', '0'))) {
    $j0 = Json $exe (@('--once', '--pid', $b.Id, '--interval-ms', '1000', '--no-frames') + $a)
    Check ($j0.attached -and $j0.sampleHz -eq 0 -and $j0.mainThreadSamples -eq 0 -and @($j0.mainThreadModules).Count -eq 0 -and -not $j0.note -and $j0.mainThreadBusyPct -gt 50) "breakdown off $(if ($a) { 'with --sample-hz 0' } else { 'by default' }): 0 samples, no note, CPU busy still measured ($([math]::Round($j0.mainThreadBusyPct))%)"   # (#12: measured, not a verdict threshold)
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
$j = Json $exe @('--once', '--pid', $PID)
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
$t1 = Start-Process $exe -ArgumentList '--tray', '--tray-child', '--no-browser', '--no-frames', '--port', '8798', '--instance', $inst, '--settings', "`"$testSettings`"" -PassThru
$null = $t1.Handle
$up = $false
for ($i = 0; $i -lt 40 -and -not $up; $i++) { Start-Sleep -Milliseconds 250; try { $null = Invoke-WebRequest 'http://127.0.0.1:8798/api/snapshot' -UseBasicParsing -TimeoutSec 2; $up = $true } catch { } }
Check ($up -and -not $t1.HasExited) "tray app: running, dashboard answers on its port"
$t2 = Start-Process $exe -ArgumentList '--tray', '--tray-child', '--no-browser', '--no-frames', '--port', '8798', '--instance', $inst, '--settings', "`"$testSettings`"" -PassThru
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
# in a folder of its own: a hosted group's files (relay-members.json, relay-key.bin) go next to the settings
$sfDir = Join-Path $env:TEMP ('svsperf_selftest_settings_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory $sfDir | Out-Null
$sf = Join-Path $sfDir 'settings.json'
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
# a port another program listens on is refused before anything changes (#9: the firewall rule would follow it)
$busy = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 8793); $busy.Start()
try { Check ((Post "$good/api/settings" $body2.Replace('8794', '8793') $good) -eq 422) "settings: a port another program uses is refused" } finally { $busy.Stop() }
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
# 0.6 (#18) the host PC in its own group: hosting + "Also share this PC", invite links from the public address
$script:tok = $v5.token
$b6 = '{"version":1,"port":8794,"openDashboardOnStart":false,"hostGroup":true,"groupPort":8792,"groupInviteAddress":"perf.example.com","hostSharesSelf":true,"hostSelfName":"Host"}'
Check ((Post 'http://127.0.0.1:8794/api/settings' $b6 'http://127.0.0.1:8794') -eq 200) "host: hosting a group and sharing this PC with it is accepted"
$v6 = $null; for ($i = 0; $i -lt 20 -and -not ($v6 -and $v6.sharing -eq 'sharing'); $i++) { Start-Sleep -Milliseconds 500; $v6 = WaitUp 8794 }
Check ($v6.settings.shareWithGroup -and $v6.settings.groupAddress -eq 'http://127.0.0.1:8792' -and $v6.settings.inviteCode.Length -eq 29 -and
       @($v6.members | Where-Object name -eq 'Host').Count -eq 1 -and $v6.sharing -eq 'sharing' -and $v6.sharingAs -eq 'Host') "host: this PC joined its own group with a code nobody handles, and shares ($($v6.sharing))"
$script:tok = $v6.token
$addr = try { (Invoke-WebRequest 'http://127.0.0.1:8794/api/group-members' -Method Post -Body '{"add":"Friend"}' -ContentType 'application/json' -Headers @{ Origin = 'http://127.0.0.1:8794'; 'X-SVS-Token' = $script:tok } -UseBasicParsing).Content | ConvertFrom-Json } catch { $null }
Check ($addr.link -match '^https://perf\.example\.com/join#([A-Z2-9]{4}-){5}[A-Z2-9]{4}$' -and $addr.link.EndsWith($addr.code)) "host: adding someone gives a one-click invite link ($($addr.link -replace '#.*', '#…'))"
$jp = try { (Invoke-WebRequest 'http://127.0.0.1:8792/join' -UseBasicParsing).Content } catch { '' }
Check ($jp -match 'svsperf://join/' -and $jp -match '/api/signin') "invite: the group serves the join page"
$who = try { (Invoke-WebRequest 'http://127.0.0.1:8792/api/whoami' -Headers @{ Authorization = "Bearer $($addr.code)" } -UseBasicParsing).Content | ConvertFrom-Json } catch { $null }
$who401 = try { $null = Invoke-WebRequest 'http://127.0.0.1:8792/api/whoami' -Headers @{ Authorization = 'Bearer AAAA-BBBB-CCCC-DDDD-EEEE-FFFF' } -UseBasicParsing; 200 } catch { [int]$_.Exception.Response.StatusCode }
Check ($who.name -eq 'Friend' -and $who401 -eq 401) "invite: the app can ask who a code belongs to (and a wrong code gets 401)"
# "Stop sharing" on the host (the page sends sharing off, the tick still on) must stick (Codex #18)
$script:tok = (WaitUp 8794).token
$bStop = $b6.TrimEnd('}') + ',"shareWithGroup":false,"groupAddress":"http://127.0.0.1:8792","inviteCode":"' + $v6.settings.inviteCode + '"}'
$vs = $null
if ((Post 'http://127.0.0.1:8794/api/settings' $bStop 'http://127.0.0.1:8794') -eq 200) { Start-Sleep -Milliseconds 1500; $vs = WaitUp 8794 }
Check ($vs -and -not $vs.settings.shareWithGroup -and -not $vs.settings.hostSharesSelf -and $vs.sharing -eq 'off') "host: Stop sharing sticks (it also turns 'Also share this PC' off)"
$script:tok = $vs.token
# after Leave (address and code cleared), "Also share this PC" works again under the same name (Codex #18 re-check)
$bLeave = $b6.Replace('"hostSharesSelf":true', '"hostSharesSelf":false').TrimEnd('}') + ',"shareWithGroup":false,"groupAddress":"","inviteCode":""}'
$vl = $null
if ((Post 'http://127.0.0.1:8794/api/settings' $bLeave 'http://127.0.0.1:8794') -eq 200) { Start-Sleep -Milliseconds 1200; $vl = WaitUp 8794; $script:tok = $vl.token }
$vr = $null
if ($vl -and (Post 'http://127.0.0.1:8794/api/settings' $b6 'http://127.0.0.1:8794') -eq 200) { for ($i = 0; $i -lt 20 -and -not ($vr -and $vr.sharing -eq 'sharing'); $i++) { Start-Sleep -Milliseconds 500; $vr = WaitUp 8794 } }
Check ($vr -and $vr.settings.shareWithGroup -and @($vr.members | Where-Object name -eq 'Host').Count -eq 1 -and $vr.sharing -eq 'sharing') "host: after Leave, 'Also share this PC' works again under the same name ($($vr.sharing))"
$script:tok = $vr.token
$b7 = $b6.Replace('"hostSharesSelf":true', '"hostSharesSelf":false')
$v7 = $null
if ((Post 'http://127.0.0.1:8794/api/settings' $b7 'http://127.0.0.1:8794') -eq 200) { Start-Sleep -Milliseconds 1500; $v7 = WaitUp 8794 }
Check ($v7 -and -not $v7.settings.shareWithGroup -and $v7.sharing -eq 'off') "host: 'Also share this PC' off stops this PC's sharing"
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
if (-not $isAdmin) {
    $null = & $exe --admin task-delete SVSPerfSelfTest; $c1 = $LASTEXITCODE
    $null = & $exe --admin firewall-add 8765; $c2 = $LASTEXITCODE
    Check ($c1 -eq 1 -and $c2 -eq 1) "the admin helper refuses without admin rights (task, firewall)"
}
# phone / home network (#9) off = no phone address offered (turning it on needs a permission prompt: manual test)
Check (@($v5.networkUrls).Count -eq 0 -and -not $v5.settings.homeNetwork) "home network off: the page offers no phone address"
Remove-Item $sfDir -Recurse -ErrorAction SilentlyContinue

# the published pair (-Exe publish\SVSPerfMonitor.com): the starter SVSPerfMonitor.exe next to it
$starter = [IO.Path]::ChangeExtension($exe, '.exe')
if ($exe -like '*.com' -and (Test-Path $starter)) {
    $inst = 'SVSPerfSelfTest_' + [guid]::NewGuid().ToString('N').Substring(0, 6)
    $s = Start-Process $starter -ArgumentList '--tray', '--no-browser', '--no-frames', '--port', '8798', '--instance', $inst, '--settings', "`"$testSettings`"" -PassThru
    $null = $s.Handle
    $sOk = $s.WaitForExit(5000) -and $s.ExitCode -eq 0
    Start-Sleep -Milliseconds 1500
    $tc = @(Get-CimInstance Win32_Process -Filter "Name='SVSPerfMonitor.com'" | Where-Object { $_.CommandLine -match $inst -and $_.CommandLine -match '--tray-child' })
    $up = $false; for ($i = 0; $i -lt 20 -and -not $up; $i++) { try { $null = Invoke-WebRequest 'http://127.0.0.1:8798/api/snapshot' -UseBasicParsing -TimeoutSec 2; $up = $true } catch { Start-Sleep -Milliseconds 250 } }
    Check ($sOk -and $tc.Count -eq 1 -and $up) "starter: SVSPerfMonitor.exe starts the tray program without a console window and leaves (copies: $($tc.Count))"
    $ex = & $exe --exit --instance $inst
    Check ($LASTEXITCODE -eq 0) "starter: the tray copy it started stops cleanly with --exit ('$ex')"
    # the command line through the starter: output and exit code pass through (piped, as scripts do)
    $sj = Json $starter @('--once', '--pid', $PID, '--no-frames')
    Check ($sj.attached -and $sj.pid -eq $PID) "starter: command line through SVSPerfMonitor.exe passes the output through"
    # a quoted value containing " --tray " is one argument, not the tray option (the starter parses quotes)
    $sq = Json $starter @('--once', '--pid', $PID, '--no-frames', '--profile', "$env:TEMP\x --tray y")
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
    Check ($page -match 'Game log</title>' -and $page -match 'href="/"') "GET /log serves the log page (links back to the dashboard)"
    Check (((curl.exe -s http://127.0.0.1:8799/) -join "`n") -match 'href="log"') "the dashboard links to the log (relative: the group relay serves it under /s/<id>/)"
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

# 0.6 invites (#18): the forms an invite link may take; anything else is refused
foreach ($c in @(@('https://perf.example.com/join#abcd-efgh-jkmn-pqrs-tuvw-xyz2', 'https://perf.example.com/|ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2'),
                 @('svsperf://join/?a=https%3A%2F%2Fperf.example.com%3A9443&c=ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2', 'https://perf.example.com:9443/|ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2'),
                 @('http://perf.example.com/join#ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2', 'no'), @('https://perf.example.com/other#ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2', 'no'),
                 @('https://perf.example.com/join#ABCD', 'no'), @('svsperf://join/?a=javascript%3Aalert(1)&c=ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2', 'no'),
                 @('https://user:pw@perf.example.com/join#ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2', 'https://perf.example.com/|ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2'))) {   # (a user:password part is dropped)
    $got = (& { $ErrorActionPreference = 'Continue'; $c[0] | & $exe --parse-invite 2>$null }) -join ''
    $ok = if ($c[1] -eq 'no') { $got -like 'no:*' } else { $got -eq $c[1] }
    Check $ok "invite link: [$($c[0] -replace '#.*', '#…' -replace '&c=.*', '&c=…')] -> $($got -replace '\|.*', '|…')"
}

# an invite link's command line must be exactly --tray --join <link> (a web page picks the link text)
$okLink = 'svsperf://join/?a=https%3A%2F%2Fperf.example.com&c=ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2'
foreach ($c in @(@(@('--tray', '--join', $okLink), $okLink), @(@('--tray', '--join', $okLink, '--tray-child'), $okLink),
                 @(@('--tray', '--join', 'svsperf://join/?a=x"', '--exit', '--join', '"svsperf:x'), 'refused'),
                 @(@('--tray', '--join', $okLink, '--settings', '\\attacker\share\s.json'), 'refused'),
                 @(@('--settings', '\\attacker\share\s.json', '--tray', '--join', $okLink), 'refused'),
                 @(@('--tray', '--join', 'https://perf.example.com/join#x'), 'refused'), @(@('--no-browser', 'svsperf://x'), 'refused'),
                 @(@('--tray', '--no-browser'), 'none'))) {
    $got = (& { $ErrorActionPreference = 'Continue'; ($c[0] -join "`n") | & $exe --check-join 2>$null }) -join ''
    Check ($got -eq $c[1]) "invite command line: [$($c[0] -join ' ' -replace '&c=.*', '&c=…')] -> $($got -replace '&c=.*', '&c=…')"
}

# 0.6 dedicated server (#17): the game with -server is a server: no frame capture, a main-thread verdict
foreach ($c in @(@('"C:\Games\FS25\FarmingSimulator2025Game.exe" -server -autoStartSavegameId 1', 'server'), @('game.exe -serverWithGui', 'server'),
                 @('"D:\my-server stuff\game.exe" -name x', 'game'), @('game.exe -servers', 'game'), @('game.exe', 'game'),
                 @('game.exe -log "D:\logs\ -server capture\out.txt"', 'game'), @('game.exe "-server"', 'server'), @('C:\x\-server', 'game'))) {
    $got = (& { $ErrorActionPreference = 'Continue'; $c[0] | & $exe --is-server-cmdline 2>$null }) -join ''
    Check ($got -eq $c[1]) "server detection: [$($c[0])] -> $got (want $($c[1]))"
}
$sb = Start-Process $exe -ArgumentList '--burn', '10', '-server' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 700
$sj = Json $exe @('--once', '--pid', $sb.Id, '--interval-ms', '2000')
Check ($sj.server -eq $true -and $sj.verdict.label -eq 'server-limit' -and $null -eq $sj.frames) "a busy process started with -server = a dedicated server at its limit, no frame capture ($($sj.verdict.label))"
if (-not $sb.HasExited) { Stop-Process -Id $sb.Id -ErrorAction SilentlyContinue }

# 0.6 group relay (#14): a relay on 127.0.0.1:8796 and a copy sharing a temp log with it, on this PC only
$gdir = Join-Path $env:TEMP ('svsperf_selftest_group_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory $gdir | Out-Null
$members = Join-Path $gdir 'members.json'
$gprof = Join-Path $gdir 'profile'; New-Item -ItemType Directory $gprof | Out-Null
$docs = [Environment]::GetFolderPath('MyDocuments')
[IO.File]::WriteAllText((Join-Path $gprof 'log.txt'), "group first line`nLoading $docs\My Games\mods\x.zip`nfrom C:\Users\$env:USERNAME\AppData\y on $env:COMPUTERNAME`nE:/Users/Jane Doe/z.txt`n<script>alert(1)</script>`n")
# #15: FSPluginHost's logs (docs/ABI.md "Runtime files"); a plugin with a path-like name must never become a path
$gfsp = Join-Path $gprof 'fsp'
New-Item -ItemType Directory (Join-Path $gfsp 'T3D') | Out-Null
[IO.File]::WriteAllText((Join-Path $gfsp 'fsp.status.xml'), '<fsp version="0.13" channel="test" build="1"><plugin name="T3D" version="1" state="active"/><plugin name="..\..\evil" version="1" state="active"/></fsp>')
[IO.File]::WriteAllText((Join-Path $gfsp 'fsp.log'), "host line one`n")
[IO.File]::WriteAllText((Join-Path $gfsp 'fs25plugins.log'), "loader line one`n")
[IO.File]::WriteAllText((Join-Path $gfsp 'T3D\T3D.log'), "t3d line from C:\Users\$env:USERNAME\Documents`n")
[IO.File]::WriteAllText((Join-Path $gdir 'evil.log'), "must never be served`n")
$add = (& $exe --group-add 'Tester One' --group-members $members) -split ' '
$gid = $add[0]; $gcode = $add[-1]
Check ($LASTEXITCODE -eq 0 -and $gcode -match '^([A-Z2-9]{4}-){5}[A-Z2-9]{4}$') "group: --group-add prints an invite code ($gid)"
$dup = & { $ErrorActionPreference = 'Continue'; (& $exe --group-add 'tester one' --group-members $members 2>&1 | ForEach-Object { "$_" }) -join ' ' }
Check ($dup -match 'already has that name') "group: names are unique (any case)"
Check ((Get-Content $members -Raw) -notmatch [regex]::Escape($gcode) -and (Get-Content $members -Raw) -match '"codeHash"') "group: the members file keeps only a hash of the code"
$gbase = 'http://127.0.0.1:8796'
$ghdr = Join-Path $gdir 'h.txt'; $gbody = Join-Path $gdir 'b.json'
function GReq([string]$method, [string]$path, [string]$body, [string[]]$headers) {
    $a = @('-s', '-X', $method, '-D', $ghdr, '-w', '%{http_code}', '-m', '5')
    if ($null -ne $body -and $body.Length -gt 0) { [IO.File]::WriteAllText($gbody, $body); $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$gbody") }
    foreach ($h in $headers) { $a += @('-H', $h) }
    $out = (& curl.exe @a "$gbase$path") -join "`n"
    return @{ code = $out.Substring($out.Length - 3); body = $out.Substring(0, $out.Length - 3); headers = (Get-Content $ghdr -Raw) }
}
$grelay = Start-Process $exe -ArgumentList '--no-browser', '--no-frames', '--seconds', '60', '--port', '8797', '--group-port', '8796', '--group-members', "`"$members`"" -PassThru -WindowStyle Hidden
$gshare = Start-Process $exe -ArgumentList '--no-browser', '--no-frames', '--seconds', '60', '--port', '8795', '--profile', "`"$gprof`"", '--share', 'http://127.0.0.1:8796', '--share-code', $gcode -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 4500
    $listen = @(Get-NetTCPConnection -State Listen -LocalPort 8796 -ErrorAction SilentlyContinue | ForEach-Object LocalAddress)
    Check ($listen.Count -gt 0 -and -not ($listen | Where-Object { $_ -ne '127.0.0.1' })) "group: the relay listens on 127.0.0.1 only ($($listen -join ', '))"
    Check ((GReq GET '/api/group').code -eq '401') "group: the list needs a sign-in"
    $rb = GReq GET '/robots.txt'
    Check ($rb.code -eq '200' -and $rb.body -match 'Disallow: /' -and $rb.headers -match 'X-Robots-Tag: noindex') "group: crawlers are asked to stay out (robots.txt, X-Robots-Tag)"
    Check ((GReq POST '/api/signin' ('{"code":"' + $gcode + '"}')).code -eq '403') "group: sign-in without this site's Origin -> 403"
    Check ((GReq POST '/api/signin' '{"code":"AAAA-BBBB-CCCC-DDDD-EEEE-FFFF"}' @("Origin: $gbase")).code -eq '401') "group: a wrong code -> 401"
    $si = GReq POST '/api/signin' ('{"code":"' + $gcode.ToLower().Replace('-', ' ') + '"}') @("Origin: $gbase")
    $setc = ($si.headers -split "`r?`n" | Where-Object { $_ -like 'Set-Cookie:*' }) -join ''
    Check ($si.code -eq '200' -and $setc -match 'HttpOnly' -and $setc -match 'Secure' -and $setc -match 'SameSite=Strict') "group: the code signs in (any case, spaces) with a HttpOnly Secure SameSite cookie"
    $gcookie = 'Cookie: ' + (($setc -replace '^Set-Cookie: ', '') -split ';')[0]
    $grp = GReq GET '/api/group' $null @($gcookie)
    $gj = $grp.body | ConvertFrom-Json
    Check ($grp.code -eq '200' -and $gj.me -eq 'Tester One' -and @($gj.sharing).Count -eq 1 -and $gj.sharing[0].id -eq $gid) "group: the sharer is listed"
    $dash = GReq GET "/s/$gid/" $null @($gcookie)
    Check ($dash.code -eq '200' -and $dash.body -match 'data-relay="1"' -and $dash.body -notmatch 'href="/settings"' -and $dash.body -match 'Tester One') "group: the member's dashboard (their name, no relay Settings)"
    Check ($dash.body -match 'href="http://127\.0\.0\.1:8795/settings"' -and ($gj.PSObject.Properties.Name -contains 'myPort') -and $gj.myPort -eq 8795) "group: 'Settings' opens the viewer's own PC's settings page (its dashboard port 8795), never through the group"
    Check ((GReq GET "/s/$gid/log/stream").code -eq '401') "group: a member's live log needs a sign-in"
    $glog = (curl.exe -sN --max-time 2 -H $gcookie "$gbase/s/$gid/log/stream?n=50") -join "`n"
    Check ($glog -match 'group first line' -and $glog -match 'alert\(1\)') "group: the shared log arrives through the relay"
    $leak = @($env:USERNAME, $env:COMPUTERNAME, 'Jane Doe') + @(($docs -split '[\\/]') | Where-Object { $_.Length -ge 4 -and $_ -notin 'Users', 'Documents' })
    $leaked = @($leak | Where-Object { $_.Length -ge 3 -and $glog -match [regex]::Escape($_) })
    Check ($leaked.Count -eq 0 -and $glog -match '\[Documents\]' -and $glog -match 'Users/user/z') "group: user name, computer name and personal folders never leave the PC (leaked: $($leaked -join ', '))"
    $dl = GReq GET "/s/$gid/log/download" $null @($gcookie)
    Check ($dl.code -eq '200' -and $dl.headers -match 'attachment; filename="Tester_One-log-\d{8}-\d{4}\.txt"' -and $dl.body -match 'group first line' -and $dl.body -notmatch [regex]::Escape($env:COMPUTERNAME)) "group: a member's log downloads as a text file"
    Check ((GReq GET "/s/$gid/log/download").code -eq '401') "group: downloading needs a sign-in"
    $ldl = (curl.exe -s -D - http://127.0.0.1:8795/log/download) -join "`n"
    Check ($ldl -match 'attachment; filename="game-log-' -and $ldl -match 'group first line') "the local log page downloads too"
    # #15: FSPluginHost's logs, on this PC and through the group
    $ll = (curl.exe -s http://127.0.0.1:8795/api/logs) | ConvertFrom-Json
    Check ((($ll | ForEach-Object id) -join ',') -eq 'game,fsp,loader,plugin:T3D') "plugin logs: this PC lists game, host, loader and T3D, never a path-like plugin name ($((($ll | ForEach-Object id) -join ',')))"
    $t3 = (curl.exe -sN --max-time 2 'http://127.0.0.1:8795/log/stream?n=50&src=plugin:T3D') -join "`n"
    Check ($t3 -match 't3d line') "plugin logs: a plugin's log streams on this PC (?src=plugin:T3D)"
    $bad = curl.exe -s -o NUL -w '%{http_code}' --max-time 2 'http://127.0.0.1:8795/log/stream?src=plugin:..%5C..%5Cevil'
    Check ($bad -eq '404') "plugin logs: a path-like source is refused ($bad)"
    $gl = ((GReq GET "/s/$gid/api/logs" $null @($gcookie)).body | ConvertFrom-Json)
    Check ((($gl | ForEach-Object id) -join ',') -eq 'game,fsp,loader,plugin:T3D') "plugin logs: the group lists the member's logs ($((($gl | ForEach-Object id) -join ',')))"
    $gt3 = (curl.exe -sN --max-time 2 -H $gcookie "$gbase/s/$gid/log/stream?n=50&src=plugin:T3D") -join "`n"
    Check ($gt3 -match 't3d line' -and $gt3 -notmatch [regex]::Escape($env:USERNAME)) "plugin logs: a member's plugin log arrives through the group, cleaned"
    $gfd = GReq GET "/s/$gid/log/download?src=fsp" $null @($gcookie)
    Check ($gfd.code -eq '200' -and $gfd.headers -match 'filename="Tester_One-FSPluginHost-log-' -and $gfd.body -match 'host line one') "plugin logs: a member's FSPluginHost log downloads"
    # #16 full logs on request: the zip (all logs, cleaned), then ask -> the sharer's answer carries it -> send -> download
    $fz = Join-Path $gdir 'full.zip'
    & $exe --full-logs-zip $gprof $fz
    $fzx = Join-Path $gdir 'fullx'
    try { Expand-Archive $fz $fzx -Force } catch { }
    $fzNames = @(Get-ChildItem $fzx -Recurse -File | ForEach-Object { $_.FullName.Substring($fzx.Length + 1).Replace('\', '/') } | Sort-Object)
    $fzText = (Get-ChildItem $fzx -Recurse -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    Check (($fzNames -join ',') -eq 'fs25plugins.log,fsp.log,log.txt,plugins/T3D.log' -and $fzText -match 't3d line' -and $fzText -notmatch [regex]::Escape($env:USERNAME) -and $fzText -notmatch [regex]::Escape($env:COMPUTERNAME)) "full logs: one zip with every log, cleaned ($($fzNames -join ', '))"
    Check ((GReq POST '/api/full-logs/ask' ('{"id":"' + $gid + '"}') @("Origin: $gbase")).code -eq '401') "full logs: asking needs a sign-in"
    Check ((GReq POST '/api/full-logs/ask' ('{"id":"' + $gid + '"}') @("Origin: $gbase", $gcookie)).code -eq '200') "full logs: a member asks"
    $fs1 = (GReq GET "/s/$gid/api/full-logs" $null @($gcookie)).body | ConvertFrom-Json
    Check ($fs1.pending -and $fs1.by -eq 'Tester One' -and -not $fs1.ready) "full logs: the request is pending"
    # (the real sharer pushes every 2 s: a push within 0.5 s of it is "too fast", so try a few times)
    $pa = $null
    for ($i = 0; $i -lt 8 -and -not ($pa -and $pa.code -eq '200'); $i++) { Start-Sleep -Milliseconds 300; $pa = GReq POST '/api/push' '{"run":"fullrun","logs":[]}' @("Authorization: Bearer $gcode") }
    $rid = if ($pa.body -match '"request":\{"id":"([0-9A-F]+)"') { $matches[1] } else { '' }
    Check ($rid.Length -eq 16) "full logs: the sharer's next answer carries the request"
    $up = { param($id, $file, $ctype) curl.exe -s -o NUL -w '%{http_code}' -m 20 -X POST -H "Authorization: Bearer $gcode" -H "X-SVS-Request: $id" -H "Content-Type: $ctype" --data-binary "@$file" "$gbase/api/full-logs/send" }
    Check ((& $up 'FFFFFFFFFFFFFFFF' $fz 'application/zip') -eq '409') "full logs: a zip nobody asked for is refused"
    Check ((& $up $rid (Join-Path $gprof 'log.txt') 'application/zip') -eq '415') "full logs: only a zip is taken"
    Check ((& $up $rid $fz 'application/zip') -eq '200') "full logs: the requested zip is accepted"
    $fs2 = (GReq GET "/s/$gid/api/full-logs" $null @($gcookie)).body | ConvertFrom-Json
    $dz = Join-Path $gdir 'dl.zip'
    $dcode = curl.exe -s -o $dz -w '%{http_code}' -H $gcookie "$gbase/s/$gid/full-logs.zip"
    Check ($fs2.ready -and -not $fs2.pending -and $dcode -eq '200' -and (Get-FileHash $dz).Hash -eq (Get-FileHash $fz).Hash) "full logs: any member downloads them (status ready, the same zip)"
    Check ((curl.exe -s -o NUL -w '%{http_code}' "$gbase/s/$gid/full-logs.zip") -eq '401') "full logs: downloading needs a sign-in"
    $gev = (curl.exe -sN --max-time 3 -H $gcookie "$gbase/s/$gid/events") -join "`n"
    Check ($gev -match '^data: \{"time"' -or $gev -match '(?m)^data: \{"time"') "group: the member's live numbers arrive"
    Check ((GReq POST '/api/push' '{"epoch":"x","lines":[]}' @('Authorization: Bearer AAAA-BBBB-CCCC-DDDD-EEEE-FFFF')).code -eq '401') "group: a push with an unknown code -> 401"
    Check ((curl.exe -s -o NUL -w '%{http_code}' -m 5 -X POST -H 'Content-Type: application/json' -H 'Content-Length: 900000' --data-binary '{}' "$gbase/api/push") -match '413|400') "group: an oversized push is refused"
    Check ((GReq POST '/api/stop' '{}' @("Authorization: Bearer $gcode")).code -eq '200' -and @(((GReq GET '/api/group' $null @($gcookie)).body | ConvertFrom-Json).sharing).Count -le 1) "group: stop sharing is accepted"
    $rs = GReq POST '/api/push' '{"run":"testrun01","logs":[{"id":"game","epoch":"newrun01","after":5,"lines":[]}]}' @("Authorization: Bearer $gcode")
    Check ($rs.code -eq '200' -and $rs.body -match '"resync":\["game"\]') "group: a log the relay doesn't know asks for its history again (resync)"
    Check ((GReq POST '/api/stop' '{"run":"testrun01"}' @("Authorization: Bearer $gcode")).code -eq '200') "group: stop names its run"
    Start-Sleep -Milliseconds 600
    $late = GReq POST '/api/push' '{"run":"testrun01","logs":[{"id":"game","epoch":"newrun01","after":-1,"lines":[[1,"late",0]]}]}' @("Authorization: Bearer $gcode")
    Check ($late.code -eq '409' -and $late.body -match 'stopped') "group: a late push of a stopped run is refused"
    # the real sharer comes back by itself (its own run: the relay asks it to resync, then accepts it)
    $back = $false
    for ($i = 0; $i -lt 30 -and -not $back; $i++) { Start-Sleep -Milliseconds 300; $back = @(((GReq GET '/api/group' $null @($gcookie)).body | ConvertFrom-Json).sharing).Count -eq 1 }
    Check $back "group: the sharer resumes after the relay forgot its run"
    # (the list shows them from their first push; the history follows with the next one, ~0.6 s later)
    $hist = ''
    for ($i = 0; $i -lt 6 -and $hist -notmatch 'group first line'; $i++) { Start-Sleep -Milliseconds 500; $hist = (curl.exe -sN --max-time 2 -H $gcookie "$gbase/s/$gid/log/stream?n=50") -join "`n" }
    Check ($hist -match 'group first line') "group: ...and resends its history (nothing it still has is skipped)"
    # a removed member's open live views are closed at once (here: their view of their own log)
    $vjob = Start-Job { param($ck, $u) $sw = [Diagnostics.Stopwatch]::StartNew(); curl.exe -sN --max-time 10 -H $ck $u | Out-Null; $sw.ElapsedMilliseconds } -ArgumentList $gcookie, "$gbase/s/$gid/log/stream?n=1"
    Start-Sleep -Milliseconds 1500
    & $exe --group-remove $gid --group-members $members
    Check ($LASTEXITCODE -eq 0) "group: --group-remove"
    # the relay notices the file on its next look at the members (any request): ask for the list once
    GReq GET '/api/group' $null @($gcookie) | Out-Null
    $vms = [int](Receive-Job $vjob -Wait -AutoRemoveJob | Select-Object -Last 1)
    Check ($vms -lt 8000) "group: removing a member closes their open live views ($vms ms of a 10 s stream)"
    Start-Sleep -Milliseconds 300
    Check ((GReq GET '/api/group' $null @($gcookie)).code -eq '401') "group: a removed member's session ends at once"
    Check ((GReq POST '/api/push' '{"epoch":"x","lines":[]}' @("Authorization: Bearer $gcode")).code -eq '401') "group: a removed member's code no longer shares"
} finally {
    foreach ($p in $gshare, $grelay) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue } }
    Start-Sleep -Milliseconds 300
    Remove-Item $gdir -Recurse -ErrorAction SilentlyContinue
}

Remove-Item $testSettings -ErrorAction SilentlyContinue
if ($fails) { Write-Host "SVSPERFMONITOR SELFTEST: $fails failure(s)"; exit 1 }
Write-Host "SVSPERFMONITOR SELFTEST OK"
exit 0
