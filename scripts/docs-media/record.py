# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "pillow>=10.3",
#   "pyte>=0.8.2",
#   "fonttools>=4.50",
#   "pywinpty>=2.0; sys_platform == 'win32'",
#   "ptyprocess>=0.7; sys_platform != 'win32'",
# ]
# ///
"""Record real tx sessions into PNG screenshots and animated GIFs for the docs.

Every frame comes from the actual CLI: each command runs in a pseudo-terminal
(ConPTY on Windows, a pty elsewhere), its ANSI output is replayed through a
terminal emulator (pyte), and the screen is drawn with Pillow. Nothing is
hand-edited, so re-running after an output change keeps the media honest.

    uv run scripts/docs-media/record.py            # all scenes
    uv run scripts/docs-media/record.py bpa        # one scene

Scenes live in scenes.json next to this script. Output goes to docs/assets/media.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import shlex
import shutil
import string
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

import pyte
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
OUT_DIR = REPO / "docs" / "assets" / "media"

# Theme: a neutral dark terminal. tx colors with the 16 ANSI palette indexes
# (ESC[38;5;0-15m), so this table decides every color in the media.
BG = (22, 24, 29)
FG = (215, 218, 224)
CHROME = (40, 43, 50)
TITLE_FG = (140, 146, 156)
PROMPT_FG = (52, 137, 126)
ANSI = {
    "black": (40, 44, 52), "red": (224, 108, 117), "green": (152, 195, 121),
    "brown": (229, 192, 123), "blue": (97, 175, 239), "magenta": (198, 120, 221),
    "cyan": (86, 182, 194), "white": (215, 218, 224),
    "brightblack": (92, 99, 112), "brightred": (240, 128, 136), "brightgreen": (170, 214, 140),
    "brightbrown": (240, 206, 140), "brightblue": (120, 190, 250), "brightmagenta": (214, 140, 235),
    "brightcyan": (110, 200, 212), "brightwhite": (255, 255, 255),
}

FONT_CANDIDATES = [
    r"C:\Windows\Fonts\CascadiaMono.ttf",
    "/usr/share/fonts/truetype/cascadia-code/CascadiaMono.ttf",
    "/Library/Fonts/CascadiaMono.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
]
FALLBACK_CANDIDATES = [
    r"C:\Windows\Fonts\seguisym.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
]

TYPE_MS = (28, 70)          # per-keystroke delay range
AFTER_TYPE_MS = 450         # pause before "pressing Enter"
MAX_GAP_MS = 700            # compress long waits between output chunks
AFTER_STEP_MS = 1600        # pause after a command finishes
FINAL_HOLD_MS = 4000        # hold the last frame before looping

# Box-drawing glyphs: (up, down, left, right, rounded)
BOX = {
    "─": (0, 0, 1, 1, 0), "│": (1, 1, 0, 0, 0),
    "┌": (0, 1, 0, 1, 0), "┐": (0, 1, 1, 0, 0), "└": (1, 0, 0, 1, 0), "┘": (1, 0, 1, 0, 0),
    "╭": (0, 1, 0, 1, 1), "╮": (0, 1, 1, 0, 1), "╰": (1, 0, 0, 1, 1), "╯": (1, 0, 1, 0, 1),
    "├": (1, 1, 0, 1, 0), "┤": (1, 1, 1, 0, 0), "┬": (0, 1, 1, 1, 0), "┴": (1, 0, 1, 1, 0),
    "┼": (1, 1, 1, 1, 0),
}

Cell = tuple[str, tuple[int, int, int], tuple[int, int, int] | None, bool]
Line = list[Cell]


# ---------------------------------------------------------------- capture

@dataclass
class Capture:
    chunks: list[tuple[float, str]]  # (seconds since start, text)


def capture(argv: list[str], cwd: Path, env: dict[str, str], cols: int) -> Capture:
    """Run a command in a pseudo-terminal and record its output with timings."""
    rows = 400  # tall, so the PTY never scrolls and cursor moves stay in range
    start = time.monotonic()
    chunks: list[tuple[float, str]] = []
    if sys.platform == "win32":
        import winpty

        # Pass the list: pywinpty builds the Windows command line itself.
        proc = winpty.PtyProcess.spawn(argv, cwd=str(cwd), env=env, dimensions=(rows, cols))
    else:
        from ptyprocess import PtyProcessUnicode

        proc = PtyProcessUnicode.spawn(argv, cwd=str(cwd), env=env, dimensions=(rows, cols))
    while True:
        try:
            data = proc.read(4096)
        except EOFError:
            break
        if data:
            chunks.append((time.monotonic() - start, data))
    return Capture(chunks)


# ---------------------------------------------------------------- emulation

def hex_rgb(value: str, default):
    if value == "default":
        return default
    if value in ANSI:
        return ANSI[value]
    if len(value) == 6 and all(c in string.hexdigits for c in value):
        return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))
    return default


# Blend toward the background for faint (SGR 2) text, as most terminals draw it.
DIM_MIX = 0.5


def dimmed(rgb):
    return tuple(round(c * (1 - DIM_MIX) + b * DIM_MIX) for c, b in zip(rgb, BG))


class PaletteScreen(pyte.Screen):
    """pyte with tx's color and faint text: ESC[38;5;n] for n < 16 becomes the
    named ANSI color (so ANSI above themes it), and faint rides in pyte's blink
    attribute, which tx never uses."""

    def select_graphic_rendition(self, *attrs, **kwargs):
        out: list[int] = []
        i = 0
        while i < len(attrs):
            a = attrs[i]
            if a in (38, 48) and i + 2 < len(attrs) and attrs[i + 1] == 5 and attrs[i + 2] < 16:
                n = attrs[i + 2]
                base = 30 if a == 38 else 40
                out.append(base + n if n < 8 else base + 60 + n - 8)
                i += 3
            elif a in (38, 48) and i + 1 < len(attrs) and attrs[i + 1] in (2, 5):
                width = 5 if attrs[i + 1] == 2 else 3
                out.extend(attrs[i:i + width])
                i += width
            else:
                out.extend({2: [5], 22: [22, 25]}.get(a, [a]))
                i += 1
        super().select_graphic_rendition(*out, **kwargs)


def screen_lines(screen: pyte.Screen) -> list[Line]:
    """Visible lines of a per-command screen, trimmed to what has been written."""
    last = -1
    lines: list[Line] = []
    for y in range(screen.lines):
        row = screen.buffer[y]
        line: Line = []
        for x in range(screen.columns):
            ch = row[x]
            fg = hex_rgb(ch.fg, FG)
            if ch.blink:  # faint, see PaletteScreen
                fg = dimmed(fg)
            bg = hex_rgb(ch.bg, None)
            if ch.reverse:
                fg, bg = (bg or BG), fg
            line.append((ch.data or " ", fg, bg, ch.bold))
            if (ch.data not in (" ", "")) or bg is not None:
                last = max(last, y)
        lines.append(line)
    cur = screen.cursor
    used = max(last + 1, cur.y + (1 if cur.x > 0 else 0))
    return lines[:used]


def text_line(parts: list[tuple[str, tuple[int, int, int], bool]], cols: int) -> list[Line]:
    """Build (wrapped) lines from styled text runs, e.g. a prompt plus a command."""
    cells: Line = [(c, fg, None, bold) for text, fg, bold in parts for c in text]
    if not cells:
        return [[]]
    return [cells[i:i + cols] for i in range(0, len(cells), cols)]


# ---------------------------------------------------------------- rendering

class Renderer:
    def __init__(self, cols: int, rows: int, scale: int, title: str):
        self.cols, self.rows, self.scale, self.title = cols, rows, scale, title
        size = 15 * scale
        path = next((p for p in FONT_CANDIDATES if Path(p).exists()), None)
        if path is None:
            sys.exit("No monospace font found; add one to FONT_CANDIDATES.")
        self.font = ImageFont.truetype(path, size)
        self.bold = ImageFont.truetype(path, size)
        try:
            self.font.set_variation_by_name("Regular")
            self.bold.set_variation_by_name("Bold")
        except (OSError, ValueError):
            pass
        self.cmap = set(TTFont(path).getBestCmap())
        fb = next((p for p in FALLBACK_CANDIDATES if Path(p).exists()), None)
        self.fallback = ImageFont.truetype(fb, size) if fb else None
        self.cw = round(self.font.getlength("M"))
        self.ch = round(size * 1.32)
        ascent, descent = self.font.getmetrics()
        self.baseline_pad = (self.ch - size) // 2
        self.baseline = (self.ch - ascent - descent) // 2 + ascent
        self.pad = 20 * scale
        self.bar = 34 * scale
        self.width = self.cols * self.cw + 2 * self.pad
        self.height = self.bar + self.rows * self.ch + 2 * self.pad - 6 * scale

    def box(self, d: ImageDraw.ImageDraw, c: str, px: int, py: int, fg) -> None:
        """Draw box-drawing characters as lines so borders join across the line gap."""
        up, down, left, right, rounded = BOX[c]
        w = max(1, self.scale)
        cx, cy = px + self.cw // 2, py + self.ch // 2
        x0, x1, y0, y1 = px, px + self.cw, py, py + self.ch
        if rounded:
            r = min(self.cw, self.ch) // 2
            # The arc sits in the quadrant between the two joined arms.
            bx = cx if right else cx - 2 * r
            by = cy if down else cy - 2 * r
            start = {(True, True): 180, (True, False): 90, (False, True): 270, (False, False): 0}[(right, down)]
            d.arc([bx, by, bx + 2 * r, by + 2 * r], start, start + 90, fill=fg, width=w)
            if right:
                d.line([cx + r, cy, x1, cy], fill=fg, width=w)
            if left:
                d.line([x0, cy, cx - r, cy], fill=fg, width=w)
            if down:
                d.line([cx, cy + r, cx, y1], fill=fg, width=w)
            if up:
                d.line([cx, y0, cx, cy - r], fill=fg, width=w)
            return
        if left:
            d.line([x0, cy, cx, cy], fill=fg, width=w)
        if right:
            d.line([cx, cy, x1, cy], fill=fg, width=w)
        if up:
            d.line([cx, y0, cx, cy], fill=fg, width=w)
        if down:
            d.line([cx, cy, cx, y1], fill=fg, width=w)

    def render(self, lines: list[Line], cursor: tuple[int, int] | None) -> Image.Image:
        s = self.scale
        img = Image.new("RGB", (self.width, self.height), BG)
        d = ImageDraw.Draw(img)
        d.rectangle([0, 0, self.width, self.bar], fill=CHROME)
        for i, color in enumerate([(255, 95, 87), (254, 188, 46), (40, 200, 64)]):
            cx, cy, r = 20 * s + i * 20 * s, self.bar // 2, 6 * s
            d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)
        tw = d.textlength(self.title, font=self.font)
        d.text(((self.width - tw) / 2, (self.bar - self.ch) / 2 + self.baseline_pad), self.title, fill=TITLE_FG, font=self.font)

        top = self.bar + self.pad - 6 * s
        for y, line in enumerate(lines[-self.rows:]):
            py = top + y * self.ch
            for x, (c, fg, bg, bold) in enumerate(line[: self.cols]):
                px = self.pad + x * self.cw
                if bg is not None:
                    d.rectangle([px, py, px + self.cw - 1, py + self.ch - 1], fill=bg)
                if c == " ":
                    continue
                if c in BOX:
                    self.box(d, c, px, py, fg)
                    continue
                font = self.bold if bold else self.font
                if ord(c) not in self.cmap and self.fallback is not None:
                    font = self.fallback
                # Anchor on the baseline so fallback-font glyphs line up with the rest.
                d.text((px, py + self.baseline), c, fill=fg, font=font, anchor="ls")
        if cursor is not None:
            cx, cy = cursor
            visible_offset = max(0, len(lines) - self.rows)
            cy -= visible_offset
            if 0 <= cy < self.rows:
                px, py = self.pad + cx * self.cw, top + cy * self.ch
                d.rectangle([px, py + 2 * s, px + self.cw - 1, py + self.ch - 2 * s], fill=FG)
        return img


# ---------------------------------------------------------------- scenes

@dataclass
class Frame:
    lines: list[Line]
    cursor: tuple[int, int] | None
    ms: int


def build_frames(scene: dict, tx: list[str], workdir: Path, env: dict[str, str], cols: int) -> list[Frame]:
    rng = random.Random(scene["name"])
    frames: list[Frame] = []
    history: list[Line] = []
    prompt = ("$ ", PROMPT_FG, True)

    def emit(lines, cursor, ms):
        frames.append(Frame([list(l) for l in lines], cursor, int(ms)))

    cwd = workdir / scene.get("cwd", "")
    for step in scene["steps"]:
        command = step["run"]
        argv = shlex.split(command, posix=True)
        if argv[0] == "tx":
            argv = tx + argv[1:]
        print(f"  $ {command}", flush=True)
        cap = capture(argv, cwd, env, cols)
        if step.get("hidden"):
            continue
        if step.get("clear"):
            history = []
        elif history:
            history = history + [[]]  # breathing room between commands

        # Type the command.
        for i in range(len(command) + 1):
            lines = history + text_line([prompt, (command[:i], FG, False)], cols)
            cursor = (len(lines[-1]) % cols if lines[-1] else 0, len(lines) - 1)
            if len(lines[-1]) == cols:
                cursor = (0, len(lines))
            emit(lines, cursor, rng.randint(*TYPE_MS) if i < len(command) else AFTER_TYPE_MS)
        history = history + text_line([prompt, (command, FG, False)], cols)

        # Replay output with its real timing (long gaps compressed).
        screen = PaletteScreen(cols, 400)
        stream = pyte.Stream(screen)
        prev_t = 0.0
        for t, data in cap.chunks:
            gap = min((t - prev_t) * 1000, MAX_GAP_MS)
            if frames:
                frames[-1].ms += int(gap)
            prev_t = t
            stream.feed(data)
            emit(history + screen_lines(screen), None, 0)
        history = history + screen_lines(screen)
        while history and not any(c[0] != " " for c in history[-1]):
            history.pop()
        emit(history + [[]] + text_line([prompt], cols), (2, len(history) + 1), AFTER_STEP_MS)

    if frames:
        frames[-1].ms = FINAL_HOLD_MS
    return frames


def used_width(lines: list[Line]) -> int:
    width = 0
    for line in lines:
        for x in range(len(line) - 1, -1, -1):
            if line[x][0] != " " or line[x][2] is not None:
                width = max(width, x + 1)
                break
    return width


def dedupe(frames: list[Frame]) -> list[Frame]:
    out: list[Frame] = []
    for f in frames:
        if out and out[-1].lines == f.lines and out[-1].cursor == f.cursor:
            out[-1].ms += f.ms
        else:
            out.append(f)
    for f in out:
        f.ms = max(f.ms, 20)  # browsers clamp tiny GIF delays to 100ms
    return out


def save_gif(frames: list[Frame], renderer: Renderer, path: Path) -> None:
    images = [renderer.render(f.lines, f.cursor) for f in frames]
    # One shared palette (from the busiest frames) keeps colors stable across frames.
    sample = sorted(images, key=lambda im: len(im.getcolors(1 << 24) or []))[-4:]
    strip = Image.new("RGB", (sample[0].width, sample[0].height * len(sample)))
    for i, im in enumerate(sample):
        strip.paste(im, (0, i * im.height))
    palette = strip.quantize(colors=128, method=Image.Quantize.MEDIANCUT)
    quantized = [im.quantize(palette=palette, dither=Image.Dither.NONE) for im in images]
    quantized[0].save(
        path, save_all=True, append_images=quantized[1:],
        duration=[f.ms for f in frames], loop=0, optimize=False, disposal=1,
    )


# ---------------------------------------------------------------- workspace

def short_workdir(root: Path) -> tuple[Path, str | None]:
    """On Windows, map the scratch folder to a drive letter so paths in output stay short."""
    if sys.platform != "win32":
        return root, None
    for letter in "TUVWXYZ":
        if not Path(f"{letter}:\\").exists():
            subprocess.run(["subst", f"{letter}:", str(root)], check=True)
            return Path(f"{letter}:\\"), f"{letter}:"
    return root, None


def find_tx(explicit: str | None) -> list[str]:
    if explicit:
        return [explicit]
    exe = "Tomix.Cli.exe" if sys.platform == "win32" else "Tomix.Cli"
    path = REPO / "src" / "Tomix.Cli" / "bin" / "Release" / "net10.0" / exe
    print("Building tx (Release)...", flush=True)
    subprocess.run(["dotnet", "build", str(REPO / "src" / "Tomix.Cli"), "-c", "Release", "-v", "q", "--nologo"], check=True)
    return [str(path)]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("scenes", nargs="*", help="scene names to record (default: all)")
    parser.add_argument("--tx", help="path to a tx executable (default: build src/Tomix.Cli in Release)")
    parser.add_argument("--out", type=Path, default=OUT_DIR)
    args = parser.parse_args()

    config = json.loads((HERE / "scenes.json").read_text(encoding="utf-8"))
    defaults = config["defaults"]
    scenes = [s for s in config["scenes"] if not args.scenes or s["name"] in args.scenes]
    if not scenes:
        sys.exit(f"No matching scenes. Available: {', '.join(s['name'] for s in config['scenes'])}")

    tx = find_tx(args.tx)
    args.out.mkdir(parents=True, exist_ok=True)

    for scene in scenes:
        cols = scene.get("cols", defaults["cols"])
        rows = scene.get("rows", defaults["rows"])
        print(f"Recording {scene['name']}...", flush=True)
        scratch = Path(tempfile.mkdtemp(prefix="tx-media-"))
        (scratch / "work").mkdir()
        root, mapped = short_workdir(scratch / "work")
        try:
            for dest, src in scene.get("setup", {}).items():
                shutil.copytree(REPO / src, root / dest)
            env = dict(os.environ)
            env.update({
                "TOMIX_CONFIG_DIR": str(scratch / "config"),  # isolate sessions, recents, staging
                "TOMIX_NO_UPDATE_CHECK": "1",
                "TERM": "xterm-256color",
                "COLORTERM": "truecolor",
            })
            env.pop("NO_COLOR", None)
            env.pop("CI", None)
            frames = dedupe(build_frames(scene, tx, root, env, cols))
        finally:
            if mapped:
                subprocess.run(["subst", mapped, "/d"], check=False)
            shutil.rmtree(scratch, ignore_errors=True)

        title = scene.get("title", defaults["title"])
        gif = args.out / f"{scene['name']}.gif"
        png = args.out / f"{scene['name']}.png"
        # Trim the window to the widest content so images don't carry dead space.
        cols = min(cols, max(64, max(used_width(f.lines) for f in frames) + 2))
        save_gif(frames, Renderer(cols, rows, 1, title), gif)
        last = frames[-1]
        # The still drops the trailing empty prompt and is rendered at 2x for sharpness.
        still_lines = list(last.lines)
        while still_lines and len(still_lines[-1]) <= 2:  # trailing "$ " prompt and blanks
            still_lines.pop()
        still_rows = min(rows, len(still_lines))
        Renderer(cols, still_rows, 2, title).render(still_lines, None).save(png, optimize=True)
        print(f"  -> {gif.relative_to(REPO)} ({gif.stat().st_size // 1024} KB, {len(frames)} frames)")
        print(f"  -> {png.relative_to(REPO)} ({png.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
