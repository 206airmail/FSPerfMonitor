# SVS Performance Monitor

A performance monitor for Farming Simulator 25 that watches the game from outside: is the game CPU-bound or GPU-bound,
where the main thread's time goes (the game, each FSPluginHost plugin, the graphics driver, Windows), and what the
plugins report about themselves. A dashboard opens in the browser.

SVS Performance Monitor (a SimVizion Studios product; formerly FSPerf, command `SVSPerfMonitor`) is its own tool, with its own version and release cycle (repository: 206airmail/FSPerfMonitor; until 2026-11-08
it lived in the FSPluginHost repository, so issue numbers in older commit messages are FSPluginHost's). It shares no
code with FSPluginHost. Its only link is reading the
documented runtime file `<profile>\fsp\fsp.status.xml` (FSPluginHost `docs/ABI.md`, "Runtime files"). It works
without FSPluginHost too.

## Use

```
build.ps1 -FetchPresentMon     (needs the .NET 10 SDK; downloads PresentMon 2.6.0 for full mode, hash-checked;
                                -Publish = one native SVSPerfMonitor.exe (~8 MB) + PresentMon in publish\)
SVSPerfMonitor                  dashboard on http://127.0.0.1:8765/, opens the browser, waits for the game
SVSPerfMonitor --record run.csv the same, plus one CSV line per tick
SVSPerfMonitor --once           one JSON snapshot to stdout (for scripts and test runners)
SVSPerfMonitor --lan            the dashboard also on the network: http://<this PC's IP>:8765/ (0.2.2)
   options: --port <n> --interval-ms <ms> --sample-hz <hz> --pid <n> --profile <dir> --seconds <n> --no-browser
            --presentmon <exe> --no-frames --lan
```

`--lan` listens on all interfaces (no URL reservation needed). Other computers also need one inbound firewall rule,
in an admin PowerShell: `New-NetFirewallRule -DisplayName 'SVS Performance Monitor dashboard' -Direction Inbound -Protocol TCP -LocalPort
8765 -Action Allow -Profile Private -RemoteAddress LocalSubnet` (the monitor prints it). The rule works only while the
network is set to Private. The dashboard is read-only.

## Game log view (0.3)

`http://127.0.0.1:8765/log` (linked from the dashboard, and back) streams the game's `<profile>\log.txt` live. It is built
for a phone in portrait (`--lan`): wrapping monospace text, no sideways scroll, auto-scroll that pauses when you scroll
up with a "Jump to live" button, a case-insensitive filter, Errors / Warnings / FAIL toggles (FS25 `Error:` / `Warning:`
lines and FSPAutoTest `FAIL`; errors, warnings, FAIL and PASS lines are coloured), Pause, and Clear (clears the view
only). The page keeps the last 10,000 lines and shows at most 3,000 at a time.

- **Server:** every 400 ms it reads only the bytes the game appended (a stat-sized check when nothing changed; a 100+ MB
  log is never read whole: it starts from the last 1 MB). The newest 5,000 lines are kept in memory, so a client gets the
  recent history at once. Partial last lines wait for their newline.
- **Restart:** the game rewrites `log.txt` on every start. The monitor notices (file identity, size shrinking, or a changed
  header) and sends a "log restarted" marker, then the new lines. The page then clears and starts fresh from that marker,
  and a page opened later starts at the newest restart, not in the previous run (0.3.1).
- **Endpoint:** `GET /log/stream?n=500` is server-sent events in batches: `id: <epoch>.<seq>`, data
  `{"e","reset","gap","lines":[[seq,text,marker],...]}`. A reconnecting browser sends `Last-Event-ID` (or `&after=<id>`)
  and gets exactly the missed lines, none twice; if the ring no longer holds them the page shows a "lines were missed"
  marker (the header wins over `after`; the page also drops lines it already has, by sequence). At most 8 log viewers at
  a time. Each viewer has its own small queue and writer thread: a phone that stops reading is dropped (and reconnects)
  and never slows the tailer or the other viewers.
- **Host header:** every route accepts only `127.0.0.1`, `localhost`, `[::1]` (and with `--lan` this PC's own addresses
  and hostname) at the dashboard's port; any other Host gets 403, none gets 400 (guards against DNS rebinding).
- **Privacy:** the view shows the whole `log.txt` (file paths, mod names, user names in paths) to everyone who can reach
  the dashboard. With `--lan` that is everyone on your private network. It is read-only.

## Full mode (0.2): per-frame CPU and GPU time

When the monitor may read Windows' presentation events, it starts Intel's PresentMon console (MIT, signed by Intel; found
next to `SVSPerfMonitor.exe`, in `tools\`, or at `--presentmon`). PresentMon reports every frame of the game:

- **Frame time:** FPS, the p99 frame time, and the 1% low (the frame rate of the slowest 1% of frames).
- **CPU busy / wait:** the main thread's work per frame and its waits.
- **GPU busy / wait:** the GPU's work on the frame and its idle time.
- **Display latency.**

The dashboard shows "full mode", and the verdict then comes from the frames:
- **Neither** (frame cap, vsync or waiting): the larger of CPU busy and GPU busy is under 80% of the frame time.
- **Both near the limit:** both are at least 80% of the frame time and within 10% of each other.
- Otherwise the bigger side is the limit: **CPU-bound** or **GPU-bound**.

**Permission:** full mode needs EITHER of these:
- **Run the monitor as administrator:** open an admin terminal and run `SVSPerfMonitor --no-browser`. Open the printed URL in your
  normal browser, because a browser the monitor starts would run as admin too.
- **The "Performance Log Users" group,** set up once in an admin terminal:
  `net localgroup "Performance Log Users" <your account> /add`. Sign out and back in. After that, no admin is needed.

Without either, the dashboard says so and the monitor keeps running in basic mode. When the monitor exits, the capture session is
stopped; `--stop_existing_session` cleans up after a crash.

## What it measures (0.1, basic mode: no admin rights, nothing injected)

| Number | How |
|---|---|
| Main thread busy % | CPU time of the game's main thread (its oldest thread) per second of wall time |
| GPU 3D % | Windows' "GPU Engine" counters for the game's process: its busiest 3D engine (Task Manager's "3D") |
| VRAM | "GPU Process Memory" dedicated usage of the game's process |
| Game CPU, RAM | process CPU time over all logical cores, working set, private bytes |
| Main thread: where the time goes (**off by default**, `--sample-hz 250` turns it on) | the main thread is suspended for microseconds `--sample-hz` times a second, its instruction pointer read and the address attributed to the module it falls in: the game exe, each `x64\fs25plugins\<Name>\` plugin, FSPluginHost (`fsp.dll`) and its loader, graphics (driver, D3D12, DXGI), Windows, runtimes |
| Busiest threads | the same CPU-time measure per game thread |
| Plugins | `fsp.status.xml`: host version, each plugin's state and the counters it publishes as status fields |

**The verdict:**
- **CPU-bound:** the main thread is at least 90% busy while the GPU's 3D load is below 85%.
- **GPU-bound:** the GPU's 3D load is at least 90% while the main thread is below 85% busy (it waits for the GPU).
- **Both near the limit:** both are high.
- **Neither:** both are low, e.g. a frame cap, vsync, a menu or loading.

These are load-based rules: without per-frame data they tell you which side is saturated, not by how much a frame
waits.

**Limits of basic mode:**
- No FPS or frame times: those need full mode, above.
- A plugin's share is the time spent in its own code. Game code its hooks call counts as the game, because this is a
  leaf-only measure; inclusive time needs stack walks, which come later.
- Lua mods all run inside the game's script engine, so they show as the game.

**Sampling cost (why it is off by default, 0.4):** each sample pauses the game's main thread. Measured 2026-11-08 on a
single-threaded CPU-bound job (certutil hashing 2 GB, 5 runs each): about 2-6% slower at 250 Hz and 8-10% at 1000 Hz
(noisy; the earlier "well under 0.5%" estimate was wrong). `--sample-hz 0` (the default) never opens or suspends the
thread. Don't sample during timing-sensitive A/B test runs unless both arms are sampled.

## Planned

- **Inclusive per-plugin time:** sampled stacks, so time a plugin's hook causes inside game code counts too.
- **Lua mod timing:** an optional helper mod.
- **Test runners:** recording a trace per run, compared across runs.

## Tests

`test\selftest.ps1` runs against processes it starts itself, never the game:
- a busy .NET main thread is seen busy and CPU-bound
- a native busy thread is attributed to its real module
- an idle process gets "neither"
- the full-mode verdict rules on synthetic frames
- without permission, full mode reports why and basic mode is used
- the log tailer on temp files (appends, a partial line completed later, truncate and recreate = restart marker, ring
  bounds, resume from a sequence id, a huge first read, an endless line), then the log stream end to end on port 8799
  with a fake profile
- the monitored process exiting mid-run is no crash (0.2.1: a game that quits or is killed between two reads used to end
  the monitor with "Process has exited"; now it goes back to "waiting for the game")

MIT licence, Copyright (c) 2026 Jared Brown: see `LICENSE`. PresentMon (downloaded, not in this repository) is Intel's,
under its own MIT licence.
