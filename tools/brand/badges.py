"""Writes HUD-styled status badges as static SVGs. Usage: python -I badges.py <out_dir>

Badges are plain SVG with system monospace fonts, because GitHub shows SVGs as
images and web fonts inside them do not load. textLength pins each label to a
computed width so the layout is the same whichever monospace font is used.
"""
import pathlib
import sys

BG = "#10131b"
LINE = "#262c3d"
DIM = "#8a93a8"
COLORS = {"yellow": "#fcee0a", "cyan": "#19f3ff", "green": "#5dffa1"}

BADGES = [
    ('status', 'status', 'in use', 'green'),
    ('wolvenkit', 'wolvenkit', '9.1', 'cyan'),
    ('dotnet', 'dotnet', '10', 'cyan'),
    ('license', 'license', 'GPL-3.0', 'green'),
    ('docs', 'docs', 'read the docs', 'yellow'),
]

CHAR = 7.0  # px per character at 11px monospace
PAD = 8
CUT = 5
HEIGHT = 22


def badge(label, value, color):
    lw = round(len(label) * CHAR + 2 * PAD)
    vw = round(len(value) * CHAR + 2 * PAD)
    w = lw + vw
    accent = COLORS[color]
    outline = f"M0,0 H{w - CUT} L{w},{CUT} V{HEIGHT} H{CUT} L0,{HEIGHT - CUT} Z"
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{HEIGHT}" viewBox="0 0 {w} {HEIGHT}" role="img" aria-label="{label}: {value}">
  <title>{label}: {value}</title>
  <path d="{outline}" fill="{BG}" stroke="{LINE}"/>
  <path d="M{lw},0.5 V{HEIGHT - 0.5}" stroke="{LINE}"/>
  <path d="M{CUT},{HEIGHT - 1} H{lw}" stroke="{accent}" stroke-width="2"/>
  <g font-family="Consolas, 'DejaVu Sans Mono', Menlo, monospace" font-size="11" text-anchor="middle">
    <text x="{lw / 2}" y="15" fill="{DIM}" textLength="{len(label) * CHAR}" lengthAdjust="spacingAndGlyphs">{label.upper()}</text>
    <text x="{lw + vw / 2}" y="15" fill="{accent}" textLength="{len(value) * CHAR}" lengthAdjust="spacingAndGlyphs">{value.upper()}</text>
  </g>
</svg>
"""


out = pathlib.Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
for name, label, value, color in BADGES:
    (out / f"{name}.svg").write_text(badge(label, value, color), encoding="utf-8")
    print(f"Wrote {out / (name + '.svg')}")
