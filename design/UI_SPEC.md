# SVS Performance Monitor interface specification

Design artifacts only · 8 November 2026 · SimVizion Studios

Open [dashboard.html](dashboard.html) directly from disk. No server, build, libraries, fonts or internet connection are required. The bottom **Design preview** strip changes sample states and appearance; omit this strip from the shipped interface. All permission and save actions are simulations. No operating-system settings are changed.

## Authority and decisions

Followed the exact skill package named in the brief: `C:/Users/nsbro/AppData/Local/Temp/claude/ab91fb4739b71dce/f711f070-c8d4-47b2-ac35-6efff678996c/scratchpad/simvizion-ui-skill/`. Its SKILL is version 1.5; some foundations still identify themselves as 1.4. The installed catalog package is older (1.4). Canonical references take precedence over examples. Read the brief, product design, README, both current pages and the four source records. Inspected the three original visual references and wide/narrow DFTracker peer cards.

Transferable identity: teal-black canvas, unified raised surfaces, recessed controls, near-white text, lime actions and featured FPS, cyan selection and focus, crisp geometry. The large editor workspace and catalog artwork are specific to those applications. Here, a restrained identity-header wash and a clearly composed verdict/metrics section provide character; artwork and glowing data cards would distract from the readings.

Product display token: `PRODUCT` in `build_mockups.py` and `app.js`. Change these two matching definitions and regenerate the HTML/fixtures with `python design/build_mockups.py`. There is no runtime build requirement. Generated product names always appear as the single word **SVS Performance Monitor**. SimVizion Studios is the explicit parent identity. The waveform app emblem is an original product icon, not an invented replacement for the studio's official logo.

## Files

| File | Purpose |
| --- | --- |
| dashboard.html | Player verdict and metrics; disclosure for every existing technical reading |
| settings.html | PC-editable settings; phone read-only view |
| log.html | Portrait-first log with local sample updates |
| welcome.html | Short first-run offer and startup choice |
| tray.html | Tray menu and actual-size icon previews |
| styles.css / tokens.css / light.css | Responsive components / copied canonical base / proposed light override |
| app.js / fixtures.js | Local interactions / exact snapshot-shaped fixtures |
| icons/app.svg | Outline product emblem, 16-unit grid |
| icons/tray-waiting.svg | Neutral waiting tray state |
| icons/tray-watching.svg | Green outline and check, watching tray state |
| icons/tray-*-light.svg | Higher-contrast waiting/watching variants for a light Windows tray |
| sample_snapshot_waiting.json | Unmodified real supplied capture |
| sample_snapshot_gpu.json | Synthetic connected capture; no claim of measured performance |
| DESIGN_BRIEF_CODEX.md | Copy of the supplied brief |
| build_mockups.py | Optional artifact regeneration helper |
| verify.mjs / qa/ | Local browser checks and render evidence, where available |

Root brief and capture were copied, rather than moved: the user's explicit write boundary permits modifications only inside `design/`. No source, test, build script, commit or push is part of this work.

## Layout and reading order

One coherent identity/navigation header, then the task. No editor sidebar. Whole-document scrolling for dashboard, welcome and settings. The log deliberately uses one independently scrolling pane with a fixed toolbar, because reading older lines must not move the controls away.

| Available width | Layout |
| --- | --- |
| 1280 desktop | 1120px maximum content; 24px minimum gutters; 3 equal headline metrics; 2 Advanced columns |
| 1024 laptop | Fluid 976px content; same hierarchy and metric columns; controls wrap by group |
| 761 and wider | Header identity and navigation on one row when they fit; technical modules/threads side by side |
| 760 and narrower | 12px gutters; header navigation on a second row; Advanced and plugin cards stack; settings remain one column |
| 450 and narrower | Metric rows stack with value on right; preserves label/value association and large readable numerals |
| 390 portrait | 366px content; no sideways page scroll; two-column technical readouts; wrapping paths and plugin fields |

The 760/450 thresholds are content-fit choices, not device detection. Form max width 760px, welcome max width 720px. Do not infer phone authorization from viewport width; the server decides settings access. At 200% zoom, reflow uses the same constraints. Safe-area aware viewport is declared; log uses `100dvh` so browser chrome and the keyboard resize the tool.

Dashboard order: page/status → game verdict → FPS/1% low/average frame time → contextual setup or status → five-minute frame trend → “What does this mean?” → Advanced. Normal player text never says ETW, PDH, PresentMon, IP sampling or main thread. Internal fields, raw reasons and plugin-published text appear only under Advanced.

## Tokens and visual roles

`tokens.css` is the supplied skill token base, copied locally. `styles.css` consumes it, preserving dark values.

| Role | Dark token / value |
| --- | --- |
| Canvas | `--sv-window: #050C0E` |
| Panel / unified content | `--sv-panel: #0D1B1F`; `--sv-surface-content-raised: #101F23` |
| Elevated / recessed | `--sv-elevated: #14272B`; `--sv-recessed: #071215` |
| Boundary | `--sv-border: #294147` |
| Primary / readable secondary | `--sv-text: #EDF4F5`; `--sv-text-readable-secondary: #C5D4D6` |
| Labels / metadata | `--sv-text-secondary: #A6B8BC`; `--sv-text-muted: #91A6AB` |
| Affirmative / hover / pressed | `--sv-lime: #9AF000`; `#ADFF24`; `#7DC900` |
| Focus / selected | `--sv-teal: #42CDD2`; selected surface `#194047`, boundary `#42C4CB` |
| Success / warning / error | `#A7D5A0` / `#F0B84B` / `#F07171`, always with text or shape |
| Controls / panels / dialog radius | 4px / 6px / 8px |
| Spacing | 2, 4, 6, 8, 12, 16, 20, 24, 32px |
| Standard / touch target | 32px standard; minimum 40px on coarse pointer |
| Font | Segoe UI Variable → Segoe UI → platform sans-serif; Consolas → Cascadia Mono → monospace for logs/code |
| Content typography | 24px page/verdict; 18px sections; 16px card headings; 14–15px explanations; 13px controls/tables; 12px metadata |
| Numbers | Tabular; 36px featured metrics on wide screens, 32px phone rows; ordinary table numbers neutral |
| Shadow | `0 2px 8px rgba(0,0,0,.40)`, floating elements only |
| Identity wash | Skill's named `--sv-brand-wash`; no gradients behind analytical rows |

Chart-scoped additions: processor `--fp-chart-cpu:#8CB9ED` dashed; graphics `--fp-chart-gpu:#D4AFEE` dotted; frame time `--fp-chart-frame:#86CDD0` solid. These are categorical, not severity colors. Shared identities in both frame and load charts. Zero baselines, explicit units and last-five-minutes axis labels. Accessible title plus current-value summary and exact technical readings provide a nonvisual alternative. Synthetic trend is schematic and intentionally labeled sample data. Integration must keep actual history; do not generate this schematic curve from a current snapshot.

### Proposed light adaptation — review required

The skill defines no canonical light values although the reference preferences screen mentions a light theme. Default to the device color scheme, retaining the canonical dark theme for dark devices. The following **app-specific light proposal** preserves roles and readable contrast; it is not a claim of a canonical SVS light palette. Review with the owner before shipping. The appearance switch belongs to the preview, not an unrequested persistent product preference.

| Role | Proposed light value |
| --- | --- |
| Canvas / panel / content | `#EFF4F4` / `#FFFFFF` / `#F7FAFA` |
| Elevated / recessed / border | `#E3EEEE` / `#F0F5F5` / `#687F85` |
| Primary / readable secondary / labels / metadata | `#10262C` / `#304E55` / `#48636A` / `#506B72` |
| Link / focus / selection border | `#17666E` / `#176F78` / `#217780` |
| Selected surface / hover / hover edge | `#D6EDEE` / `#DDEBEC` / `#397780` |
| Warning / error / success | `#815600` / `#AA3030` / `#31632B` |
| CPU / GPU / frame chart lines | `#2765AA` / `#8051A1` / `#17666E` |
| Featured FPS | `#396700`, because raw lime text is unreadable on light surfaces |
| Lime actions / checked controls | Original lime with original dark foreground |

## Components and interaction

Use semantic headings, real links, labelled controls, native checkboxes/selects and native `<details>` disclosure. Selected navigation has teal background and defined border. Focus has a 2px teal outline independent of hover/selection; inputs use teal boundary; lime buttons also have an inset dark ring. Neutral borders and explicit associated warning text for invalid fields. Native form validation is supplemented with the port error. Disabled uses dedicated tokens; read-only values stay readable/copyable. There is no blanket opacity reduction.

Primary buttons use lime, secondary buttons recessed surfaces with crisp borders. Hover and pressed are distinct. No continuous motion; reduced-motion disables transitions. Informational banners remain in document flow. Preview toasts have a labelled dismiss button and `role=status`, do not steal focus, and do not dismiss actionable information automatically.

Checkbox targets are padded. Log level filters are view-selection buttons with teal pressed treatment and a visible check; checkboxes in settings use lime. Status dots have text counterparts. Do not announce all one-second readings; announce only significant connection/verdict state changes in the production integration to avoid screen-reader chatter.

## State catalog

### Dashboard

Use the preview selector or `dashboard.html?state=cpu`. Add `&advanced=1` to initially open Advanced.

| State | Reading and behavior |
| --- | --- |
| gpu / cpu | Full frame metrics, corresponding plain verdict, full Advanced data |
| waiting | Real supplied snapshot, unavailable game metrics, no fake zeros, saved plugin metadata explicitly dated |
| setup | Basic loads and estimate; “Set up” links to frame-times settings |
| basic | Frame times explicitly off; loads still available; setup link |
| neither / both | Neither-limited and dual-limit wording; preserve backend verdict rules |
| mixed / unknown | “The limit is changing” / insufficient information; unavailable counters shown as em dash |
| starting / no-frames | Capture startup / capture running with no frames; no invented FPS |
| missing / error / exited | Friendly missing tool / failed / stopped messages; raw hint only in Advanced |
| disconnected | Clearly stale timestamp and reconnect wording; no advancing chart; retain last readings |
| no-host | Explicit absence; other monitoring continues |
| sampling-off | Zero samples is not zero plugin cost; empty explanation points to Settings |

Treat `attached:false` as waiting even if `fsp.present:true`, `elevated:true` or the last file lists active plugins. On detach, reset both histories and per-game readings. Full mode requires valid frames this interval. When frame capture has no valid values, display em dash. Never coerce null, empty string or JSON string `"NaN"` to zero. Backend `mixed` is not equivalent to `both near the limit`.

### Settings and welcome

Settings selector or `settings.html?view=phone`: PC, phone, ready, signout, cancelled, failure. Phone shows current On/Off as readable text, read-only copyable path/port, no Save or Set up. Never let CSS width decide edit rights. General contains startup; Frame times contains setup; Phone and home network contains sharing and the log privacy warning; Advanced contains elevated startup, sampling opt-in, settings folder, port and browser-on-start.

Frame setup is an offer, not a checkbox that silently grants permission. On PC click, the real implementation performs one permission prompt for account-group membership. Progress must prevent duplicate actions; success requires sign out and back in. Cancellation leaves configuration unchanged and retry available. Ready must be confirmed by the host, not inferred from a successful request. Startup while full permissions is enabled requires the elevated scheduled task, not just a Run entry. Network sharing needs the private-subnet firewall rule and one prompt. Never turn on sampling by default.

Welcome gives two explanatory sentences, optional frame setup, unchecked startup choice and a dashboard link. In production persist that choice when continuing, even when setup is deferred. Preview does not create tasks, grant permission, sign out, save settings or open the network. Do not sign a player out automatically. After setup, the dashboard remains usable in basic mode until the next sign-in.

### Log

Local sample line every four seconds is only a demonstration. Cases: live, paused, scrolled, reconnecting, empty, restarted, missed lines. Filter case-insensitively. Selected Errors / Warnings / FAIL categories combine as OR; text filter combines with that result as AND. With none selected, show all. Markers always survive filters. FAIL outranks Error, Warning, PASS when classifying. Preserve exact source text with `textContent` or escaped markup; never parse a log line as HTML.

Pause buffers incoming lines; Resume appends buffered lines and updates counts. Scrolling up stops follow; Jump to live restores it. Clear clears local model/buffer only, not the game's log file or server history. Game restart clears prior run and its held buffer; new epoch begins at the restart marker. A ring gap gets a missed-lines marker. Reconnection uses existing sequence semantics; no duplicate replay. Preserve server's 8-viewer limit and meaningful retry feedback. Existing caps: 10,000 retained client lines, 3,000 DOM lines, 5,000 server lines. Mockup includes the client/DOM caps but is not a replacement SSE transport implementation.

### Tray

App emblem: compact monitor outline and frame trace, shared with header. Neutral tray icon means waiting; green with a check means watching. **No CPU/GPU badges**: neither the skill nor the product defines their visual vocabulary, and small labels fail at 16px. CPU/GPU information is in the status text and tooltip. SVG sources use a 16-unit grid and explicit stroke colors, legible at 16 and 32px; scale-preserving preview is in tray.html. Native owner can rasterize to ICO sizes for Shell_NotifyIcon.

Menu: noninteractive status line, Open dashboard, Settings, Exit. In the real Windows menu, use native keyboard arrows/Enter/Escape, flip near screen edges and native focus restoration. This HTML is a review mockup, with links showing destinations; Exit only explains the intended behavior. Actual Exit cleanly stops capture, removes the icon and terminates. Double-click opens the dashboard; starting another copy opens the existing copy. Open browser as normal user even when the process has full permissions.

## Integration map

No new endpoints are presumed. Preserve the current `/events` snapshot stream and `/log/stream` behavior; the ~1s snapshot cadence is suitable. Settings/authorization transport is still to be designed by the owner. Mockups have no live connection, fetch or external request.

| Snapshot field (camelCase) | Element / formatting |
| --- | --- |
| time | Diagnostics snapshot time; localized stale-reading time when disconnected; retain time-zone offset |
| attached | Gate game metrics; waiting/capture/graph reset behavior |
| mode | Basic/full badge; combine with valid frame count |
| verdict.label | Plain-copy verdict lookup; never print raw label as the player headline |
| verdict.reason | Diagnostics under Advanced; do not forward technical reason to main player copy |
| pid, process | Advanced Loads and memory header |
| processCpuPct, cores | Game CPU percentage normalized across all logical cores; denominator label |
| mainThreadBusyPct | Main thread busy, of one logical core; processor-load history |
| gpu3DPct | GPU 3D busiest engine; graphics-load history |
| gpuDedicatedMb | VRAM dedicated, convert MiB / 1024 to GiB |
| workingSetMb, privateMb | Working set and committed private memory, MiB / 1024 → GiB |
| topThreads[].tid, busyPct, main | Every thread row, numeric busy%, main label; no rows omitted |
| mainThreadModules[].name, kind, percent, samples | Every module row with kind, percent and exact sample count; do not truncate to top 14 |
| mainThreadSamples | Count and sampling availability explanation |
| note | Advanced diagnostics only; null/empty omitted |
| elevated | Advanced capture-permission label; not proof frame capture is ready |
| frames | Nullable; false availability must not leave previous displayed values |
| frames.state, hint | Map state to friendly banner; exact hint/state under Advanced |
| frames.frames | Frames in this interval; zero suppresses timing/FPS values |
| frames.fps, low1PctFps | Featured FPS and 1% low; one decimal; backend already derives this value |
| frames.frameTimeAvgMs | Featured average frame time and actual frame history |
| frames.frameTimeP50Ms, frameTimeP99Ms | Advanced median and p99, plus trend summary p99 |
| frames.cpuBusyMs, cpuWaitMs | Advanced per-frame processor work/wait; CPU busy trend |
| frames.gpuBusyMs, gpuWaitMs | Advanced per-frame graphics work/wait; GPU busy trend |
| frames.gpuLatencyMs, displayLatencyMs | Separate Advanced latency values; never merge them |
| frames.presentMode, runtime | Advanced presentation/runtime description |
| fsp.present | Plugins visible/absent, independent of game attachment |
| fsp.version, channel, build | Exact host metadata under Advanced |
| fsp.written | File-written timestamp, especially important while waiting; not a freshness guarantee |
| fsp.services[] | All service strings, wrapping without truncation |
| fsp.plugins[].name, version, state, reason | Per-plugin identity, version, labelled state and full reason |
| fsp.plugins[].fields | Every arbitrary key=value string, escaped and wrapped; no fixed whitelist |

Full frame verdict rules remain in backend: neither when max busy <80% frame; both when both >=80% and within 10%; otherwise bigger busy side. Basic verdict uses source `Judge`, including mixed and unavailable GPU cases. Do not recalculate from rounded display numbers. 1% low follows backend `low1PctFps`; do not invent a different statistic from the displayed FPS. Sampling is leaf attribution and includes waits; never call it inclusive mod cost.

Settings are future configuration, not Snapshot properties. Proposed conceptual bindings (not a server contract): startup, enableFrames/setupState, allowLan, elevatedStartup, enableSampling, profileDir, port, openBrowser. LocalPC authorization, saved/draft state, permission cancellation, failure details and restart-needed flags must come from the host. Read-only must be enforced on every server mutation, even if a phone forges the UI.

## Open questions and explicit adaptations

1. Approve the proposed light palette or provide canonical SVS light tokens. Dark is canonical; light is deliberately labelled a proposal.
2. Supply an official studio vector mark if an emblem lockup beyond the exact studio name is required. No standalone studio logo asset was provided; the original waveform is the product mark only.
3. Confirm settings request/response schema, local-PC authorization and whether Save applies all settings together. This mockup uses explicit Save for editable values and immediate separate setup actions, which need permission-result feedback. Decide which changes require process restart beyond port/listener changes.
4. Product name remains a working name; rename tokens are isolated. Plugin-published states and values remain verbatim under Advanced.
5. There is no explicit setting/config field that distinguishes sampling off from access denied in Snapshot. Show “No samples” and diagnostic note, not an invented permission diagnosis; production should expose a reason if desired.
6. Saved host status has no guaranteed staleness threshold. Always show the file time; while detached explicitly call it saved. Do not infer “running” from plugin `active` fields.

Adaptations permitted by the skill: content typography, web gutters and max widths, touch hit areas, stacked metrics, chart palette, native checkbox/select controls. Proposed light colors are the only visual extension needing palette approval. Read-only settings intentionally use plain On/Off values instead of disabled-looking switches. Omitted tiny tray limit badges under the brief's conditional icon requirement. No intentional dark-token deviations.

## Copy deck

The single table at the end of this specification contains static and state-dependent user-facing strings, including mockup-only controls and observed sample data. Arbitrary numeric values, paths, timestamps, process/module/plugin/service names and plugin fields are data, not translatable product copy. Interpolated values use braces in the table. Technical backend hints/reasons remain data under Advanced. Keep this deck aligned when changing copy.

## Verification

See [QA.md](QA.md) for separate functional checks, rendered visual observations, remaining limitations and evidence paths. Source inspection alone does not establish visual compliance. These are static review artifacts, not a production implementation of settings, capture or log transport.

<!-- COPY_TABLE -->

## Complete mockup copy table

Observed strings include source-data examples so reviewers can audit every state. Replace numeric, timestamp and plugin values with live data. Braced rows define dynamic templates; preview-only strings must not ship.

| Area | User-facing string |
| --- | --- |
| Dashboard · dynamic | Across {cores} logical cores |
| Dashboard · dynamic | Capture: {state} · {hint}. {permissions}. |
| Dashboard · dynamic | Current average {average} ms · slowest 1% boundary {p99} ms. Sample trend includes an occasional spike. |
| Dashboard · dynamic, dashboard | Frame times in milliseconds, synthetic five-minute sample |
| Dashboard · dynamic | Full Windows permissions |
| Dashboard · dynamic | Host {version} · channel {channel} · build {build} |
| Dashboard · dynamic, dashboard, dashboard · Advanced | Now |
| Dashboard · dynamic, dashboard | Processor and graphics load in percent, synthetic five-minute sample |
| Dashboard · dynamic | Saved status; this does not mean the game is running. Status file written {timestamp}. |
| Dashboard · dynamic | Services: None reported |
| Dashboard · dynamic | Services: {services} |
| Dashboard · dynamic | Snapshot {time} · Mode {mode} · Attached {attached} · Verdict {label}: {reason} · {note} |
| Dashboard · dynamic | Standard Windows permissions |
| Dashboard · dynamic | Status file written {timestamp}. |
| Dashboard · dynamic | The last readings below are from {time}. Waiting to reconnect to the PC running {product}. |
| Dashboard · dynamic | This is an estimate from current loads. |
| Dashboard · dynamic | Version {version} |
| Dashboard · dynamic | {count} samples |
| Dashboard · dynamic | {count} samples this interval. Shares reflect sampled wall time, including waits. |
| Dashboard · dynamic | {process} · PID {pid} |
| Dashboard · dynamic | {tid} · main |
| Dashboard · dynamic, dashboard, dashboard · Advanced | −5 min |
| Log · dynamic, log | ERROR |
| Log · dynamic, log | FAIL |
| Log · dynamic | Game restarted · {time} |
| Log · dynamic | Live · paused (+{count}) |
| Log · dynamic, log | PASS |
| Log · dynamic | Reconnected. Showing the newest available lines. |
| Log · dynamic, log | Resume |
| Log · dynamic | Resume ({count}) |
| Log · dynamic, log | Some lines were missed while this page was disconnected. |
| Log · dynamic, log | WARN |
| Log · dynamic | {count} lines · {shown} shown |
| Settings · action results, settings | Off |
| Settings · action results, settings | On |
| Settings · action results | Preview address: http://192.168.1.42:8765/ · home network only |
| Settings · action results | Preview changes applied to this page. Nothing was saved to your PC. |
| Settings · action results | Preview: Windows would ask for permission here. No system changes were made. |
| Settings · action results | Preview: Windows would ask once to allow access on your private home network. |
| Settings · action results | Preview: Windows would ask once to update how {product} starts. |
| Shared, dashboard, log, settings, tray, welcome | Dismiss message |
| Shared | SimVizion Studios · {product} |
| Shared | {product} · {page} |
| Shared | ✓ |
| Tray · action results | App icon, 16 pixels |
| Tray · action results | App icon, 32 pixels |
| Tray · action results | Preview only. Exit would stop monitoring, stop frame capture and remove the tray icon. |
| Tray · action results, tray | Waiting · light Windows tray |
| Tray · action results | Waiting · light Windows tray, 16 pixels |
| Tray · action results | Waiting · light Windows tray, 32 pixels |
| Tray · action results | Waiting, 16 pixels |
| Tray · action results | Waiting, 32 pixels |
| Tray · action results, tray | Watching · light Windows tray |
| Tray · action results | Watching · light Windows tray, 16 pixels |
| Tray · action results | Watching · light Windows tray, 32 pixels |
| Tray · action results | Watching, 16 pixels |
| Tray · action results | Watching, 32 pixels |
| Welcome · action results | Preview: permission granted. Sign out of Windows and back in to finish. |
| Welcome · action results, settings | Sign out needed |
| dashboard, dashboard · Advanced | 0 samples this interval. Shares reflect sampled wall time, including waits. |
| dashboard, dashboard · Advanced | 0.7 ms |
| dashboard | 1% low |
| dashboard, dashboard · Advanced | 1.2 ms |
| dashboard, dashboard · Advanced | 1.3 ms |
| dashboard, dashboard · Advanced | 10.0 ms |
| dashboard, dashboard · Advanced | 11.4 ms |
| dashboard, dashboard · Advanced | 11.7 GiB |
| dashboard, dashboard · Advanced | 13.1 GiB |
| dashboard, dashboard · Advanced | 16 samples |
| dashboard, dashboard · Advanced | 16.0 ms |
| dashboard, dashboard · Advanced | 16.5 ms |
| dashboard, dashboard · Advanced | 16.9 ms |
| dashboard, dashboard · Advanced | 17.2 ms |
| dashboard, dashboard · Advanced | 174 samples |
| dashboard, dashboard · Advanced | 18.1 ms |
| dashboard | 19432 · main |
| dashboard, dashboard · Advanced | 20.1 ms |
| dashboard, dashboard · Advanced | 21.0 ms |
| dashboard, dashboard · Advanced | 24.8 ms |
| dashboard, dashboard · Advanced | 248 samples this interval. Shares reflect sampled wall time, including waits. |
| dashboard, dashboard · Advanced | 31.8 ms |
| dashboard, dashboard · Advanced | 34.4 ms |
| dashboard, dashboard · Advanced | 40 samples |
| dashboard, dashboard · Advanced | 5 samples |
| dashboard, dashboard · Advanced | 7.0 ms |
| dashboard, dashboard · Advanced | 7.5 GiB |
| dashboard, dashboard · Advanced | 7.7 ms |
| dashboard, dashboard · Advanced | 8 samples |
| dashboard, dashboard · Advanced | 8.1 ms |
| dashboard, dashboard · Advanced | 9.0 ms |
| dashboard, dashboard · Advanced | 9.1 ms |
| dashboard, dashboard · Advanced | 9.7 ms |
| dashboard | A guide to the slowest frames |
| dashboard, dashboard · Advanced | A plugin’s share counts its own code. Game code it calls counts as the game. Lua mods also count as the game. These shares are not the total cost of a plugin. |
| dashboard, dashboard · Advanced | Across 24 logical cores |
| dashboard, dashboard · Advanced | active |
| dashboard, settings | Advanced |
| dashboard, log, settings, tray, welcome | Appearance |
| dashboard | Average · lower is smoother |
| dashboard | Basic mode · loads only |
| dashboard | Both are working hard. Try one change at a time and compare the same place in the game. |
| dashboard | Both near the limit |
| dashboard, dashboard · Advanced | Busiest 3D engine |
| dashboard, dashboard · Advanced | Busiest game threads |
| dashboard | Busy · one core |
| dashboard | Capture error |
| dashboard | Capture stopped |
| dashboard, dashboard · Advanced | Capture: denied · Performance Log Users membership required. Standard Windows permissions. |
| dashboard, dashboard · Advanced | Capture: disabled. Full Windows permissions. |
| dashboard, dashboard · Advanced | Capture: disabled. Standard Windows permissions. |
| dashboard, dashboard · Advanced | Capture: error · Unable to start capture. Standard Windows permissions. |
| dashboard, dashboard · Advanced | Capture: exited · PresentMon stopped. Standard Windows permissions. |
| dashboard, dashboard · Advanced | Capture: off · PresentMon not found. Standard Windows permissions. |
| dashboard, dashboard · Advanced | Capture: running · No frames received. Standard Windows permissions. |
| dashboard, dashboard · Advanced | Capture: running. Standard Windows permissions. |
| dashboard, dashboard · Advanced | Capture: starting · Starting capture. Standard Windows permissions. |
| dashboard, dashboard · Advanced | captures=0 |
| dashboard, dashboard · Advanced | capturesMax=1024 |
| dashboard | Check that FSPerf is running on your PC and that your home network is connected. |
| dashboard, dashboard · Advanced | Committed private bytes |
| dashboard, dashboard · Advanced | Composed: Flip / DXGI |
| dashboard | Connection lost |
| dashboard, dashboard · Advanced | CPU busy / frame |
| dashboard, dashboard · Advanced | CPU wait / frame |
| dashboard | Current average 16.7 ms · slowest 1% boundary 17.2 ms. Sample trend includes an occasional spike. |
| dashboard | Current average 17.2 ms · slowest 1% boundary 24.8 ms. Sample trend includes an occasional spike. |
| dashboard | Current average 21.4 ms · slowest 1% boundary 31.8 ms. Sample trend includes an occasional spike. |
| dashboard, log, settings, tray, welcome | Dark |
| dashboard, log, settings, tray, welcome | Dashboard |
| dashboard, dashboard · Advanced | Dedicated graphics memory |
| dashboard, log, settings, tray, welcome | Design preview controls |
| dashboard, log, settings, tray, welcome | Design preview · sample data |
| dashboard, dashboard · Advanced | Diagnostics |
| dashboard, dashboard · Advanced | Display latency |
| dashboard | Farming Simulator 25 |
| dashboard, dashboard · Advanced | FarmingSimulator2025Game · PID 18420 |
| dashboard, dashboard · Advanced | FarmingSimulator2025Game.exe |
| dashboard | FPS |
| dashboard | FPS counts frames each second. Higher usually feels smoother. Frame time is how long each frame takes; lower is better. The 1% low helps you spot uneven performance even when the average FPS looks good. |
| dashboard | FPS, frame times and loads will appear when the game is running. |
| dashboard | Frame time |
| dashboard, settings | Frame times |
| dashboard | Frame times are off |
| dashboard | Frame times could not start |
| dashboard | Frame times need a missing file |
| dashboard | Frame times not set up · basic |
| dashboard | Frame times off · basic |
| dashboard | Frame times stopped |
| dashboard | Frame tool missing |
| dashboard | Frames each second |
| dashboard, dashboard · Advanced | Frames this interval |
| dashboard, dashboard · Advanced | FS25_3dTerrain |
| dashboard, dashboard · Advanced | fsp.dll |
| dashboard, log, settings, tray, welcome | FSPerf |
| dashboard, dashboard · Advanced | FSPluginHost plugins |
| dashboard, dashboard · Advanced | FSPluginHost was not found. FSPerf works without it. |
| dashboard, dashboard · Advanced | FSPMesh |
| dashboard | Full mode · frame times |
| dashboard, dashboard · Advanced | Game |
| dashboard, dashboard · Advanced | Game CPU |
| dashboard, log, settings, tray, welcome | Game log |
| dashboard, dashboard · Advanced | GPU 3D |
| dashboard, dashboard · Advanced | GPU 3D · dotted |
| dashboard, dashboard · Advanced | GPU busy / frame |
| dashboard, dashboard · Advanced | GPU latency |
| dashboard, dashboard · Advanced | GPU wait / frame |
| dashboard, dashboard · Advanced | Graphics |
| dashboard | Graphics card limit · full |
| dashboard | Graphics readings are unavailable. The other readings are still shown below. This is an estimate from current loads. |
| dashboard | Graphics work · dotted |
| dashboard, dashboard · Advanced | heldMB=0.0 |
| dashboard, dashboard · Advanced | hook=1 |
| dashboard, dashboard · Advanced | Host |
| dashboard, dashboard · Advanced | Host 0.14.0 · channel on · build 1.25.1.0 Steam |
| dashboard | Keep the game open. Load readings are available while frame times start. |
| dashboard | Last 5 minutes · milliseconds |
| dashboard, dashboard · Advanced | Last 5 minutes · percent |
| dashboard, log, settings, tray, welcome | Light (proposed) |
| dashboard, dashboard · Advanced | Live trend paused. Waiting to reconnect. |
| dashboard, dashboard · Advanced | Load over time |
| dashboard | Load readings are still available. Open Settings to check setup, then restart FSPerf. |
| dashboard | Load readings are still available. Restart FSPerf to try again. |
| dashboard | Load unavailable · basic |
| dashboard, dashboard · Advanced | Loads and memory |
| dashboard | Loads, memory, threads and plugins |
| dashboard, dashboard · Advanced | madeRefused=0/0 |
| dashboard, log, settings, tray, welcome | Main navigation |
| dashboard, dashboard · Advanced | Main thread busy |
| dashboard, dashboard · Advanced | Main thread busy – dashed |
| dashboard, dashboard · Advanced | Main thread: where the time goes |
| dashboard, dashboard · Advanced | Median frame time · p50 |
| dashboard | Mixed loads · basic |
| dashboard | Module / kind |
| dashboard | ms |
| dashboard | Neither is the limit |
| dashboard | Neither is the limit right now |
| dashboard | No frames have arrived yet. Bring the game to the foreground and keep playing. |
| dashboard | No frames yet |
| dashboard, dashboard · Advanced | No game connected |
| dashboard | No plugins installed |
| dashboard | No readings yet |
| dashboard, dashboard · Advanced | No samples while waiting for the game. |
| dashboard, dashboard · Advanced | No samples. Turn on the breakdown in Settings to collect them. |
| dashboard, dashboard · Advanced | No thread readings yet. |
| dashboard, dashboard · Advanced | ntdll.dll |
| dashboard, dashboard · Advanced | nvwgf2umx.dll |
| dashboard, dashboard · Advanced | Of one logical core |
| dashboard, dashboard · Advanced | Open game log → |
| dashboard | Open Settings |
| dashboard, dashboard · Advanced | Per-frame detail |
| dashboard, dashboard · Advanced | Plugin |
| dashboard, dashboard · Advanced | Presentation / runtime |
| dashboard, dashboard · Advanced | Private memory |
| dashboard | Processor limit · full |
| dashboard | Processor work – dashed |
| dashboard, dashboard · Advanced | profileDir=1 |
| dashboard, dashboard · Advanced | RaiseTipLimit |
| dashboard, dashboard · Advanced | RAM · game |
| dashboard | Ready when you are |
| dashboard, log | Reconnecting… |
| dashboard | Sampling off |
| dashboard, dashboard · Advanced | Saved status; this does not mean the game is running. Status file written 2026-11-08T06:24:29.5961973-09:00. |
| dashboard, log | Scenario |
| dashboard, welcome | See FPS and spot stutter |
| dashboard, dashboard · Advanced | Services: FSPMesh.api 1.0 (FSPMesh) · FSPMesh.shapes 1.0 (FSPMesh) |
| dashboard, settings | Set up |
| dashboard | Set up frame times on this PC. Windows will ask for permission once, then sign out and back in. |
| dashboard, log, settings, tray, welcome | Settings |
| dashboard, dashboard · Advanced | shapes=on |
| dashboard, dashboard · Advanced | shapesB=ok |
| dashboard | Share / samples |
| dashboard, log, settings, tray, welcome | SimVizion Studios |
| dashboard, settings, tray, welcome | SimVizion Studios · FSPerf |
| dashboard, log, settings, tray, welcome | Skip to content |
| dashboard, dashboard · Advanced | Slowest 1% boundary · p99 |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T10:51:44.6851037-09:00 · Mode basic · Attached false · Verdict waiting: the game is not running |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode basic · Attached true · Verdict GPU-bound: GPU 3D 98%, main thread 62% busy |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode basic · Attached true · Verdict mixed: main thread 87%, GPU 3D 86% |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode basic · Attached true · Verdict unknown: no GPU counters on this machine |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode full · Attached true · Verdict both near the limit: CPU busy 16.0 ms and GPU busy 16.5 ms, frame 17.2 ms |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode full · Attached true · Verdict CPU-bound: CPU busy 20.1 ms, GPU busy 10.0 ms, frame 21.4 ms |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode full · Attached true · Verdict GPU-bound: GPU busy 16.5 ms, CPU busy 8.1 ms, frame 17.2 ms |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode full · Attached true · Verdict GPU-bound: GPU busy 16.5 ms, CPU busy 8.1 ms, frame 17.2 ms · Sampling disabled |
| dashboard, dashboard · Advanced | Snapshot 2026-11-08T14:32:05-09:00 · Mode full · Attached true · Verdict neither: Both busy times are below 80% of the frame time |
| dashboard | Start Farming Simulator 25. This page will pick it up automatically. |
| dashboard | Starting frame capture |
| dashboard | Starting frame times |
| dashboard, dashboard · Advanced | Status file written 2026-11-08T14:32:05-09:00. |
| dashboard, dashboard · Advanced | t3dBuild=1.25.1.0 Steam |
| dashboard | The frame-times tool is missing. Check the files that came with FSPerf. |
| dashboard | The game is waiting for graphics work to finish. Try lowering graphics settings, then compare the same place in the game. |
| dashboard | The game is waiting for graphics work to finish. Try lowering graphics settings, then compare the same place in the game. This is an estimate from current loads. |
| dashboard | The game may be holding a steady frame rate, showing a menu or loading. |
| dashboard | The game needs more time to finish its calculations. Try reducing the number of active vehicles or objects, then compare the same place. |
| dashboard | The last readings below are from 2:32:05 PM. Waiting to reconnect to the PC running FSPerf. |
| dashboard | The limit is changing |
| dashboard | The readings are close. Keep playing in the same area to see a clearer pattern. This is an estimate from current loads. |
| dashboard, dashboard · Advanced | The trend will begin when the game starts. |
| dashboard | The verdict is a guide for this moment. A frame rate cap, a menu or loading can change it. Compare the same place in the game when testing a change. |
| dashboard | There is not enough information yet |
| dashboard | These readings are out of date |
| dashboard | Thread ID |
| dashboard, log, settings, tray, welcome | Tray preview |
| dashboard, log, settings, tray, welcome | Use device setting |
| dashboard, dashboard · Advanced | Version 0.3.0 |
| dashboard, dashboard · Advanced | Version 1.0.0.0 |
| dashboard, dashboard · Advanced | Version 1.0.1 |
| dashboard, dashboard · Advanced | voxelApi=48 |
| dashboard, dashboard · Advanced | VRAM · game |
| dashboard | Waiting for frame times |
| dashboard, tray | Waiting for the game |
| dashboard, settings, tray, welcome | Watches the game from outside. Nothing is added to the game. |
| dashboard, tray | Watching the game |
| dashboard, log, settings, tray, welcome | Welcome preview |
| dashboard | What does this mean? |
| dashboard, dashboard · Advanced | Windows |
| dashboard, dashboard · Advanced | Working set |
| dashboard | You can still see processor and graphics loads. Turn on frame times to see FPS and stutter. |
| dashboard, tray | Your graphics card is the limit |
| dashboard | Your processor and graphics card are both near the limit |
| dashboard, tray | Your processor is the limit |
| dashboard | ⓘ |
| dashboard · Advanced | 19432 · main	62.0% |
| dashboard · Advanced | 19432 · main	97.0% |
| dashboard · Advanced | 19484	37.0% |
| dashboard · Advanced | 20112	24.0% |
| dashboard · Advanced | 20136	12.0% |
| dashboard · Advanced | Advanced Loads, memory, threads and plugins |
| dashboard · Advanced | Module / kind	Share / samples |
| dashboard · Advanced | Thread ID	Busy · one core |
| log | 0 lines · 0 shown |
| log | 11 lines · 11 shown |
| log | 13 lines · 13 shown |
| log | 2 lines · 2 shown |
| log | 2026-11-08 14:30:00 Farming Simulator 25 · Version 1.25.1.0 |
| log | 2026-11-08 14:30:01 Settings: C:\Users\Player\Documents\My Games\FarmingSimulator2025 |
| log | 2026-11-08 14:30:03 Loading map: Riverbend Springs |
| log | 2026-11-08 14:30:04 [FSPluginHost] FSPMesh 0.3.0 active |
| log | 2026-11-08 14:30:06 Warning: Missing optional texture for FS25_DemoTrailer/decals/dirt_diffuse.dds |
| log | 2026-11-08 14:30:08 [FSPMesh] shapes=on captures=128 heldMB=42.6 |
| log | 2026-11-08 14:30:09 [FSPAutoTest] PASS: vehicle loaded |
| log | 2026-11-08 14:30:10 Error: Cannot open file mods/FS25_DemoTrailer/sounds/coupling.xml |
| log | 2026-11-08 14:30:11 [FSPAutoTest] FAIL: trailer coupling sound was not found |
| log | 2026-11-08 14:30:12 Game saved successfully. |
| log | 2026-11-08 14:31:00 [FSPMesh] captured shape 129 |
| log | 2026-11-08 14:31:01 [FSPMesh] captured shape 130 |
| log | 2026-11-08 14:31:02 [FSPMesh] captured shape 131 |
| log | 2026-11-08 14:31:03 [FSPMesh] captured shape 132 |
| log | 2026-11-08 14:31:04 [FSPMesh] captured shape 133 |
| log | 2026-11-08 14:31:05 [FSPMesh] captured shape 134 |
| log | 2026-11-08 14:31:06 [FSPMesh] captured shape 135 |
| log | 2026-11-08 14:31:07 [FSPMesh] captured shape 136 |
| log | 2026-11-08 14:31:08 [FSPMesh] captured shape 137 |
| log | 2026-11-08 14:31:09 [FSPMesh] captured shape 138 |
| log | 2026-11-08 14:31:10 [FSPMesh] captured shape 139 |
| log | 2026-11-08 14:31:11 [FSPMesh] captured shape 140 |
| log | 2026-11-08 14:31:12 [FSPMesh] captured shape 141 |
| log | 2026-11-08 14:31:13 [FSPMesh] captured shape 142 |
| log | 2026-11-08 14:31:14 [FSPMesh] captured shape 143 |
| log | 2026-11-08 14:31:15 [FSPMesh] captured shape 144 |
| log | 2026-11-08 14:31:16 [FSPMesh] captured shape 145 |
| log | 2026-11-08 14:31:17 [FSPMesh] captured shape 146 |
| log | 2026-11-08 14:31:18 [FSPMesh] captured shape 147 |
| log | 2026-11-08 14:31:19 [FSPMesh] captured shape 148 |
| log | 2026-11-08 14:31:20 [FSPMesh] captured shape 149 |
| log | 2026-11-08 14:31:21 [FSPMesh] captured shape 150 |
| log | 2026-11-08 14:31:22 [FSPMesh] captured shape 151 |
| log | 2026-11-08 14:31:23 [FSPMesh] captured shape 152 |
| log | 2026-11-08 14:31:24 [FSPMesh] captured shape 153 |
| log | 2026-11-08 14:31:25 [FSPMesh] captured shape 154 |
| log | 2026-11-08 14:31:26 [FSPMesh] captured shape 155 |
| log | 2026-11-08 14:31:27 [FSPMesh] captured shape 156 |
| log | 2026-11-08 14:31:28 [FSPMesh] captured shape 157 |
| log | 2026-11-08 14:31:29 [FSPMesh] captured shape 158 |
| log | 2026-11-08 14:31:30 [FSPMesh] captured shape 159 |
| log | 2026-11-08 14:31:31 [FSPMesh] captured shape 160 |
| log | 2026-11-08 14:31:32 [FSPMesh] captured shape 161 |
| log | 2026-11-08 14:31:33 [FSPMesh] captured shape 162 |
| log | 2026-11-08 14:31:34 [FSPMesh] captured shape 163 |
| log | 2026-11-08 14:31:35 [FSPMesh] captured shape 164 |
| log | 2026-11-08 14:31:36 [FSPMesh] captured shape 165 |
| log | 2026-11-08 14:31:37 [FSPMesh] captured shape 166 |
| log | 2026-11-08 14:31:38 [FSPMesh] captured shape 167 |
| log | 2026-11-08 14:31:39 [FSPMesh] captured shape 168 |
| log | 2026-11-08 14:31:40 [FSPMesh] captured shape 169 |
| log | 2026-11-08 14:31:41 [FSPMesh] captured shape 170 |
| log | 2026-11-08 14:31:42 [FSPMesh] captured shape 171 |
| log | 2026-11-08 14:31:43 [FSPMesh] captured shape 172 |
| log | 2026-11-08 14:31:44 [FSPMesh] captured shape 173 |
| log | 2026-11-08 14:31:45 [FSPMesh] captured shape 174 |
| log | 2026-11-08 14:31:46 [FSPMesh] captured shape 175 |
| log | 2026-11-08 14:31:47 [FSPMesh] captured shape 176 |
| log | 2026-11-08 14:31:48 [FSPMesh] captured shape 177 |
| log | 2026-11-08 14:31:49 [FSPMesh] captured shape 178 |
| log | 2026-11-08 14:33:10 Reconnected. Showing the newest available lines. |
| log | 2026-11-08 14:34:00 Loading a new game. Previous run cleared. |
| log | 61 lines · 61 shown |
| log | Clear |
| log | Errors |
| log | Filter lines… |
| log | Find text in the log |
| log | Game log lines |
| log | Game restarted |
| log | Game restarted · 2:30:00 PM |
| log | Game restarted · 2:34:00 PM |
| log | Jump to live ↓ |
| log | Lines were missed |
| log | Live |
| log | Live · paused |
| log | Live · sample lines |
| log | Pause |
| log | Paused |
| log | Reading older lines |
| log | Reconnecting |
| log | Selected levels are combined. Text ignores letter case. Clear affects this view only. |
| log | Waiting for lines |
| log | Waiting for lines… |
| log | Warnings |
| settings | Briefly pauses the game hundreds of times a second; a tiny cost. Leave off for timing-sensitive comparisons, or keep it the same for both runs. |
| settings | Changing this address takes effect after FSPerf restarts. |
| settings | Farming Simulator settings folder |
| settings | Found automatically. Change only if you use a different folder. |
| settings | Frame times could not be set up |
| settings | Frame times ready |
| settings | General |
| settings | Home network sharing is off. |
| settings | Keep it ready when you want to play. |
| settings | Let my phone or other computers at home open the dashboard |
| settings | No changes were made. You can try Set up again when you are ready. |
| settings | Not set up |
| settings | One more step |
| settings | Only needed for the detailed mod breakdown. Windows asks once. |
| settings | Open it in your usual browser. |
| settings | Open the dashboard when FSPerf starts |
| settings | Permission cancelled |
| settings | Permission was cancelled |
| settings | Phone and home network |
| settings | Phone · read-only |
| settings | Port |
| settings | Preview only. Changes are not saved to your PC. |
| settings | Ready |
| settings | Run with full permissions |
| settings | Sample error: account group update failed. No system changes were made. |
| settings | Save changes |
| settings | Settings can be changed on the PC running FSPerf |
| settings | Setup failed |
| settings | Show frame times (FPS and stutter) |
| settings | Show what the game’s main loop spends time on |
| settings | Sign out of Windows and back in to finish setting up frame times. |
| settings, welcome | Start FSPerf when Windows starts |
| settings | These values are read-only. You can select and copy the folder and port. |
| settings | They can also see your game log, which shows your Windows user name and mod list. Use a trusted private home network. Windows will ask once. |
| settings | This PC |
| settings | This PC · not set up |
| settings | Try again on the PC running FSPerf. Open Advanced for technical details. |
| settings | View |
| settings | Viewing settings from another device. |
| settings | Windows will ask for permission once, then sign out and back in. After that, FSPerf can show FPS without full permissions. |
| settings | ⚠ Enter a whole number from 1 to 65535. |
| tray | App icon |
| tray | Exit |
| tray | Icon sources · 16 and 32 px |
| tray | Notification area |
| tray | Open dashboard |
| tray | Open the dashboard with a double-click. Right-click for this menu. |
| tray | Status |
| tray | The actual Windows menu uses native keyboard and dismissal behavior. |
| tray | Tray menu |
| tray | Tray menu preview |
| tray | Waiting |
| tray | Waiting uses a neutral outline. Watching uses a green outline with a check. Read the verdict in the tooltip; tiny limit badges would be hard to tell apart. |
| tray | Watching |
| tray | Windows tray · design preview |
| welcome | FSPerf shows whether your processor or graphics card is limiting Farming Simulator 25. It watches from outside the game, so nothing is added to the game. |
| welcome | FSPerf stays in the notification area while it runs. |
| welcome | Know what holds your game back. |
| welcome | Open dashboard → |
| welcome | Set up frame times |
| welcome | Set up frame times to see how smoothly the game runs. Windows will ask for permission once, then sign out and back in. |
| welcome | Welcome · SimVizion Studios |
| welcome | You can also do this later in Settings. |
