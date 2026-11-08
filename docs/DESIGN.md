# FSPerf design (2026-11-08)

FSPerf tells a Farming Simulator 25 player what limits their frame rate (the CPU, the GPU, or neither) and, for
modders, what each FSPluginHost plugin costs. It watches the game from outside: nothing is injected into the game.

## Audience
Players and testers first, modders second. A player should be able to download one file, double-click it, and
understand what they see without knowing what ETW, PDH or a "main thread" is.

**Wording rule:** everything a player sees (tray menu, settings, dashboard, setup prompts) uses plain words. Technical
detail goes in an "Advanced" section or a "What does this mean?" line, never in the main text. Example: "Show frame
times (FPS and stutter)", not "Enable PresentMon full mode (ETW)".

## Shape: one exe, two ways to start it
- **Double-click = tray app.** An icon in the notification area while it runs (grey = waiting for the game, green =
  watching it; the tooltip shows the verdict). Menu: *Open dashboard*, *Settings*, *Exit*.
- **With arguments = command-line tool** as before (`--once`, `--record`, `--lan`, ...), for scripts and test runners.
  It attaches to the parent console so the output still shows.
- **One copy at a time.** Starting it again opens the dashboard of the running copy.
- **Exit is a clean shutdown**: the frame-capture session is stopped, so no leftovers (the old kill-based restarts
  left one behind and starved the next start).
- The dashboard stays a web page (also usable from a phone). **Settings is a page of the same dashboard**
  (`/settings`), opened by the tray. It can be changed only from this PC; other devices see it read-only.
- The tray opens the browser as the normal user even when FSPerf runs with full permissions.

## Technology
C# / .NET 8, compiled with **NativeAOT**: one native exe, no .NET install needed, target size about 10 MB plus
PresentMon (to be confirmed by the first spike). No Windows Forms (it can't be compiled this way): the tray icon is
drawn with the Windows API directly (Shell_NotifyIcon, a popup menu), about 150 lines. JSON uses the source
generator. Decided 2026-11-08 over a Rust or C/C++ rewrite: same native result, no rewrite of tested code.

## Settings (plain words; stored in `%APPDATA%\FSPerf\settings.json`)
| What the player sees | What it does |
|---|---|
| **Start FSPerf when Windows starts** | logon start: a Run entry, or a scheduled task when "full permissions" is on (Windows doesn't auto-start admin apps from the Run entry) |
| **Show frame times (FPS and stutter)** — status + a **Set up** button: "Windows will ask for permission once, then sign out and back in" | adds the account to the Performance Log Users group (one admin prompt); this is the recommended way, no admin afterwards |
| **Run with full permissions** (Advanced) — "Only needed for the detailed mod breakdown. Windows asks once." | the logon scheduled task "run with highest privileges"; created/removed via one admin prompt |
| **Let my phone or other computers at home open the dashboard** — "They can also see your game log, which shows your Windows user name and mod list." | `--lan` + the firewall rule (one admin prompt) |
| **Show what the game's main loop spends time on** (Advanced) — "Briefly pauses the game hundreds of times a second; a tiny cost." | main-thread sampling, off by default (#2) |
| **Farming Simulator settings folder** (Advanced) | the profile folder, found automatically |
| Port, open the dashboard on start (Advanced) | |

Admin approach (Nick 2026-11-08): the group is the recommended path; "always run with full permissions" is the
advanced option via Task Scheduler.

## Distribution
- Public repo (Nick 2026-11-08: fine), GitHub Releases: one zip with `FSPerf.exe`, PresentMon (Intel, MIT) and both
  licences. Built by GitHub Actions so a release is reproducible.
- Signing: free via SignPath Foundation if accepted (OSI licence, public repo, credit line; the certificate's publisher
  is "SignPath Foundation"). Until then unsigned. Note: even signed, SmartScreen warnings fade only as the file gains
  reputation.
- Antivirus: suspending another process's thread looks suspicious to some scanners; another reason sampling is off by
  default.

## Milestones
- **0.4 tray shell:** sampling opt-in (#2); NativeAOT spike; tray + single copy + clean Exit + CLI mode; settings
  file + `/settings` page; start with Windows / frame-times setup / full permissions; bundled PresentMon.
- **0.5 player-ready:** first-run welcome; player-first dashboard (verdict and FPS in plain words first, plugin and
  module detail under Advanced); phone/LAN toggle with firewall setup; release workflow + signing application.
- **Later:** run history and A/B comparison; inclusive per-plugin time from event tracing with call stacks
  (full permissions).
- **Not in FSPerf:** an optional Lua timing helper would be its own FS25 mod; FSPerf would only read a file it writes.
