# FSPerf

A performance monitor for Farming Simulator 25 that watches the game from outside: is the game CPU-bound or GPU-bound,
where the main thread's time goes (the game, each FSPluginHost plugin, the graphics driver, Windows), and what the
plugins report about themselves. A dashboard opens in the browser.

FSPerf is its own tool, with its own version and release cycle. It lives in the FSPluginHost repository for now and is
built to move to its own repository unchanged. It shares no code with FSPluginHost. Its only link is reading the
documented runtime file `<profile>\fsp\fsp.status.xml` (FSPluginHost `docs/ABI.md`, "Runtime files"). It works
without FSPluginHost too.

## Use

```
build.ps1                      (needs the .NET 8 SDK; -Publish = one self-contained fsperf.exe in publish\)
fsperf                          dashboard on http://127.0.0.1:8765/, opens the browser, waits for the game
fsperf --record run.csv         the same, plus one CSV line per tick
fsperf --once                   one JSON snapshot to stdout (for scripts and test runners)
   options: --port <n> --interval-ms <ms> --sample-hz <hz> --pid <n> --profile <dir> --seconds <n> --no-browser
```

## What it measures (0.1, basic mode: no admin rights, nothing injected)

| Number | How |
|---|---|
| Main thread busy % | CPU time of the game's main thread (its oldest thread) per second of wall time |
| GPU 3D % | Windows' "GPU Engine" counters for the game's process: its busiest 3D engine (Task Manager's "3D") |
| VRAM | "GPU Process Memory" dedicated usage of the game's process |
| Game CPU, RAM | process CPU time over all logical cores, working set, private bytes |
| Main thread: where the time goes | the main thread is suspended for microseconds about 250 times a second, its instruction pointer read and the address attributed to the module it falls in: the game exe, each `x64\fs25plugins\<Name>\` plugin, FSPluginHost (`fsp.dll`) and its loader, graphics (driver, D3D12, DXGI), Windows, runtimes |
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
- No FPS or frame times: those need the frame events in full mode, below.
- A plugin's share is the time spent in its own code. Game code its hooks call counts as the game, because this is a
  leaf-only measure; inclusive time needs stack walks, which come later.
- Lua mods all run inside the game's script engine, so they show as the game.

**Sampling cost:** each sample pauses the game's main thread for a few microseconds; at 250 Hz that's well under 0.5%.
Don't sample during timing-sensitive A/B test runs unless both arms are sampled.

## Planned

- **Full mode:** per-frame CPU busy / GPU busy / frame time from Windows' presentation events (PresentMon, MIT). This is
  the exact CPU/GPU-bound answer plus FPS and percentiles. It needs admin rights or the "Performance Log Users" group,
  set up once.
- **Inclusive per-plugin time:** sampled stacks, so time a plugin's hook causes inside game code counts too.
- **Lua mod timing:** an optional helper mod.
- **Test runners:** recording a trace per run, compared across runs.

## Tests

`test\selftest.ps1` runs against processes it starts itself, never the game:
- a busy .NET main thread is seen busy and CPU-bound
- a native busy thread is attributed to its real module
- an idle process gets "neither"

MIT licence, Copyright (c) 2026 Jared Brown.
