"""make_icons.py - SVS Performance Monitor's icon files from the brand art.

Needs Pillow (dev only: `python -m pip install --user pillow`). Run from anywhere: `python tools/make_icons.py`.
Inputs:
  assets/brand/mark/png/   the SV monogram (Codex 2026-11-08; re-render with `node assets/brand/mark/render.mjs`):
                           sv-mark-16* for 16-20 px, sv-mark-24* for 24 px, sv-mark* for 32 px and up (pixel-hinted)
  assets/brand/svs-logo.png  the SimVizion Studios logo (an illustration: only for large sizes)
Outputs:
  src/app.ico                       exe icon: the monogram at 16-32 px, the full logo at 40-256 px (Explorer's big views)
  src/wwwroot/brand/tray-watching.ico   tray while watching the game: the monogram in colour
  src/wwwroot/brand/tray-waiting.ico    tray while waiting: the monogram in greys
  src/wwwroot/brand/logo-256.png, logo-64.png   dashboard header / welcome screen (the full logo)
  src/wwwroot/brand/favicon-32.png  browser tab (the monogram: the logo is mud at tab size)
"""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
MARK = ROOT / "assets" / "brand" / "mark" / "png"
LOGO = ROOT / "assets" / "brand" / "svs-logo.png"
OUT = ROOT / "src" / "wwwroot" / "brand"


def mark(size: int, waiting: bool = False) -> Image.Image:
    base = "sv-mark-16" if size <= 20 else "sv-mark-24" if size <= 24 else "sv-mark"
    return Image.open(MARK / f"{base}{'-waiting' if waiting else ''}-{size}.png").convert("RGBA")


def logo(size: int) -> Image.Image:
    return Image.open(LOGO).convert("RGBA").resize((size, size), Image.LANCZOS)


def save_ico(path: Path, frames: list[Image.Image]):
    # Pillow writes one ICO entry per frame given in append_images (PNG-compressed): every size is our own render,
    # never a rescale by Pillow
    frames = sorted(frames, key=lambda f: f.width)
    frames[-1].save(path, format="ICO", sizes=[f.size for f in frames], append_images=frames[:-1])


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    save_ico(ROOT / "src" / "app.ico", [mark(s) for s in (16, 20, 24, 32)] + [logo(s) for s in (40, 48, 64, 128, 256)])
    tray = (16, 20, 24, 32, 40, 48, 64)
    save_ico(OUT / "tray-watching.ico", [mark(s) for s in tray])
    save_ico(OUT / "tray-waiting.ico", [mark(s, True) for s in tray])
    for s in (256, 64):
        logo(s).save(OUT / f"logo-{s}.png", optimize=True)
    mark(32).save(OUT / "favicon-32.png", optimize=True)
    for p in sorted([ROOT / "src" / "app.ico", *OUT.iterdir()]):
        print(f"{p.relative_to(ROOT)}  {p.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
