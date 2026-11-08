# GUI design brief for Codex (from fsperfmonitor-84, the FSPerf owner session; approved by Nick 2026-11-08)

## Your job
Design the user interface of FSPerf, a **SimVizion Studios (SVS) branded product**, using the SimVizion UI skill.
You produce DESIGN ARTIFACTS ONLY, all inside `design/` in this worktree. Do not edit `src/`, `test/`, `build.ps1`
or anything outside `design/` (move `sample_snapshot_waiting.json` and this brief into `design/` first). Do not commit,
do not push. The owner session integrates your design into `src/wwwroot/` afterwards.

## Read first
1. The SimVizion skill: `C:\Users\nsbro\AppData\Local\Temp\claude\ab91fb4739b71dce\f711f070-c8d4-47b2-ac35-6efff678996c\scratchpad\simvizion-ui-skill\`
   - start with `SKILL.md`, then `QUICK_REFERENCE.md`, `VISUAL_REFERENCE.md` (+ `assets/*.png`), and the references
     you need: `identity-composition.md`, `web-layouts.md`, `responsive.md`, `colors.md`, `typography-spacing.md`,
     `controls.md`, `dialogs-popups.md`, `data-visualization.md`, `notifications.md`, `icons-motion.md`;
     `examples/tokens.css` is the token base. Follow the skill; where it is desktop-first, note it also says how to adapt
     to web.
2. `docs/DESIGN.md` (product design: audience, tray, settings table, wording rule, milestones).
3. `README.md` (what every number means), the current pages `src/wwwroot/index.html` and `src/wwwroot/log.html`
   (all data they show must still have a home), and the snapshot data shape: records `Snapshot` in
   `src/Collector.cs`, `FrameStats` in `src/FrameMonitor.cs`, `PluginStatus`/`FspStatusInfo` in `src/FspStatus.cs`,
   `MainThreadSampler.Share` in `src/MainThreadSampler.cs`; JSON is camelCase. `sample_snapshot_waiting.json` is a real
   snapshot while the game is not running.

## Audience and wording (Nick's rule)
Players and testers first, modders second. Everything a player sees uses plain words, no jargon (no "ETW", "PDH",
"PresentMon", "IP sampling", "main thread" in main text). Technical detail lives under an "Advanced" section or a
"What does this mean?" line. Example: "Show frame times (FPS and stutter)", not "Enable PresentMon full mode".

## Deliverables in `design/`
Static HTML mockups (open from disk, no build step, no CDN; inline or local CSS; tokens from the skill) with realistic
sample data, each page in its states:
1. `dashboard.html` — player-first: the verdict ("Your graphics card is the limit" / processor / neither / waiting for
   the game) and FPS / 1% low / frame time first; a simple trend if it fits the skill; then an **Advanced** area with
   everything today's page shows (threads, main-thread module shares, GPU %, VRAM, RAM, plugin states + fields, the
   log link). States: waiting for the game, CPU-bound, GPU-bound, frame times not set up (with a friendly "Set up"
   call to action), basic vs full mode.
2. `settings.html` — the settings table in docs/DESIGN.md in plain words, grouped (General / Frame times / Phone and
   home network / Advanced); toggles that need Windows' permission say so ("Windows will ask once"); read-only state
   for a phone viewing it ("Settings can be changed on the PC running FSPerf").
3. `log.html` — the phone log view restyled: portrait-first, wrapping monospace, filter, Errors / Warnings / FAIL
   toggles, Pause, Clear, Jump to live, "game restarted" marker, "lines were missed" marker.
4. `welcome.html` — a short first-run screen: what FSPerf does in two sentences, the frame-times setup offer, start
   with Windows choice.
5. Tray icon: `icons/` SVG sources for the app icon and the tray states (waiting = neutral, watching, plus CPU-limit /
   GPU-limit if the skill's icon guidance supports state badges), legible at 16 and 32 px; the tray menu
   (Open dashboard / Settings / Exit, plus the status line) as a mockup.
6. `UI_SPEC.md` — the tokens used, components, layout grid and breakpoints (desktop ~1280, laptop ~1024, phone 390
   portrait), states, copy deck (all user-facing strings in one table), and notes for the integrator (which data field
   feeds which element). Mark anything you were unsure about.

## Branding
SimVizion Studios (SVS) product. Product name for now: "FSPerf" (working name; Nick may rename), shown with the
SimVizion Studios identity as the skill prescribes. Keep the product name a single token in the mockups so it is easy
to change.

## Constraints
- Pages must work in a phone browser in portrait (the dashboard is opened from phones over the home network).
- Pages are served by FSPerf's own tiny web server: no frameworks, no external requests (fonts must be system fonts or
  shipped files; if the skill names a font, state the fallback).
- Respect light/dark per the skill.
- Keep it implementable as plain HTML/CSS/vanilla JS fed by a JSON snapshot every ~1 s.

When done, print a short summary: files made, open questions, anything in the skill you deviated from and why.
