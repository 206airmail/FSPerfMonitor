# Icon brief for Codex: an "SV" monogram mark for SVS Performance Monitor (approved by Nick 2026-11-08)

## Why
The product uses the SimVizion Studios logo (`assets/brand/svs-logo.png`: a glowing "SV" with "STUDIOS" inside a
cyan eye, lime/cyan circuit art on a dark rounded square). It is an illustration: at tray size (16-32 px) it turns to
mud, and even the centre crop (`src/wwwroot/brand/tray-*.ico`) is busy. Nick: "an artistic SV or SVS or something".

## Deliverables (write ONLY inside `assets/brand/mark/` in this worktree; no commits, no pushes)
1. `sv-mark.svg` — the master: a bold, simple **SV** monogram (letters only, or letters in a simple shape), designed for
   16 px first. Recognisably the same brand as the logo: its lime (around #9BE22C / #C6F33A) and cyan (around #2CD4E0)
   on a dark rounded-square tile (around #0B1416), the S lime and the V cyan as in the logo. Thick strokes, no thin
   lines, no glow/gradient that disappears when small, no text other than S and V.
   It may add ONE simple nod to the logo (e.g. a minimal eye/lens outline) only if it still reads at 16 px.
2. `sv-mark-waiting.svg` — the same mark in neutral greys (the tray's "waiting for the game" state). It must stay
   clearly visible on a dark Windows taskbar (#202020) AND a light one (#F3F3F3).
3. Pixel-hinted small versions if the master doesn't render crisply: `sv-mark-16.svg`, `sv-mark-24.svg` (+ waiting
   variants) drawn on the pixel grid (integer coordinates, strokes on whole pixels).
4. PNG renders of every variant at 16, 20, 24, 32, 40, 48, 64, 128, 256 px (transparent outside the tile), named
   `png/<variant>-<size>.png`, rendered with headless Chrome/Edge (no new installs; Node + a local browser are
   available — see `design/verify.mjs` in this repo for how it drives Chrome) — or any renderer already on the machine.
5. `preview.html` + `preview.png`: all variants at actual size AND 4x (nearest-neighbour) on a dark and a light taskbar
   background, next to the current tray icon (`src/wwwroot/brand/tray-watching.ico` / `tray-waiting.ico`) for comparison.
6. `README.md`: the design rationale in 5-10 lines, colours used, which file is for which size.

## Read for context
- `assets/brand/svs-logo.png` (the brand), `design/UI_SPEC.md` and `design/tokens.css` (the app's palette, built with
  Nick's SimVizion UI skill), `tools/make_icons.py` (how the .ico files are built today — the owner will switch it to
  your PNGs).

When done, print: the files, and one line on how it reads at 16 px on dark and light taskbars.
