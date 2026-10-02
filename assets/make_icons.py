#!/usr/bin/env python3
"""Generates the plugin's icon art (design A6: outlined folder, zipper, plus badge).

  assets/icon.svg       24 px button icon; folder and zipper use currentColor, the badge uses
                        var(--am-accent, currentColor), so the page can make it one color or two.
  assets/catalog.svg    1920x1080 plugin catalog card art (Quill and Gear orange and purple).

Render the PNG with:  inkscape --export-type=png --export-filename=assets/catalog.png -w 1920 -h 1080 assets/catalog.svg
The web script (Web/additional-material.js) carries the same 24 px drawing; keep them in step.
"""
from pathlib import Path

FOLDER_O = ("M9.17 6l2 2H20v10H4V6h5.17M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8"
            "c0-1.1-.9-2-2-2h-8l-2-2z")
# Badge kept fully inside the 24x24 box: outer edge at 17.6 + 4.9 + 0.85 = 23.35 (A6 mockup reached 24.25 and clipped).
BX, BY, BR, BW = 17.6, 17.4, 4.9, 1.7
CUT = 6.6   # radius cut out of the folder around the badge
ZX, Z0, Z1 = 9.5, 8.0, 18.0


def zipper_simple(color: str) -> str:
    pts, left, y = [], True, Z0 + 3.6
    while y <= Z1 + 0.01:
        pts.append(f"{ZX - 0.85 if left else ZX + 0.85:.2f},{y:.2f}")
        left, y = not left, y + 1.3
    return (f'<polyline points="{" ".join(pts)}" fill="none" stroke="{color}" stroke-width="1.35" stroke-linejoin="miter"/>'
            f'<rect x="{ZX - 1.5}" y="{Z0}" width="3" height="3.1" rx="1.1" fill="{color}"/>')


def zipper_detail(color: str) -> str:
    """Large sizes: interlocking teeth the full height below the slider."""
    s, y = [], Z0 + 3.2
    while y + 1.05 <= Z1:
        s.append(f'<rect x="{ZX - 1.15:.2f}" y="{y:.2f}" width="1.4" height="0.58" rx="0.22" fill="{color}"/>')
        s.append(f'<rect x="{ZX - 0.25:.2f}" y="{y + 0.55:.2f}" width="1.4" height="0.58" rx="0.22" fill="{color}"/>')
        y += 1.1
    s.append(f'<rect x="{ZX - 1.5}" y="{Z0}" width="3" height="2.9" rx="0.9" fill="{color}"/>')
    return "".join(s)


def plus(cx, cy, a):
    return f"M{cx - a} {cy}H{cx + a}M{cx} {cy - a}V{cy + a}"


def mark(ink: str, accent: str, badge_fill: str | None = None, plus_color: str | None = None,
         detail: bool = False, mask_id: str = "am-cut") -> str:
    """The icon's shapes in a 24x24 box."""
    body = (f'<mask id="{mask_id}" maskUnits="userSpaceOnUse" x="-2" y="-2" width="28" height="28">'
            f'<rect x="-2" y="-2" width="28" height="28" fill="#fff"/><circle cx="{BX}" cy="{BY}" r="{CUT}" fill="#000"/></mask>'
            f'<g mask="url(#{mask_id})"><path d="{FOLDER_O}" fill="{ink}" fill-rule="evenodd"/>'
            f'{zipper_detail(ink) if detail else zipper_simple(ink)}</g>')
    if badge_fill:   # catalog art: a solid disc with the plus on it
        body += (f'<circle cx="{BX}" cy="{BY}" r="{BR + BW / 2}" fill="{badge_fill}"/>'
                 f'<path d="{plus(BX, BY, 2.6)}" stroke="{plus_color}" stroke-width="2.1" stroke-linecap="round" fill="none"/>')
    else:            # button: an outlined circle and plus in the accent color
        body += (f'<circle cx="{BX}" cy="{BY}" r="{BR}" fill="none" stroke="{accent}" stroke-width="{BW}"/>'
                 f'<path d="{plus(BX, BY, 2.3)}" stroke="{accent}" stroke-width="{BW}" fill="none"/>')
    return body


def icon_svg() -> str:
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">'
            + mark("currentColor", "var(--am-accent, currentColor)") + "</svg>\n")


ORANGE, PURPLE = "#DB781B", "#3D2058"   # Quill and Gear master (QuillAndGear.svg)


def catalog_svg() -> str:
    scale = 22.0                       # 24 -> 528 px mark
    mx, my = 300, 540 - 12 * scale     # vertically centered: stays inside the band phones show
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#241733"/><stop offset="0.55" stop-color="#15101c"/><stop offset="1" stop-color="#101010"/>
    </linearGradient>
  </defs>
  <rect width="1920" height="1080" fill="url(#bg)"/>
  <g transform="translate({mx} {my}) scale({scale})">{mark("#F2F2F2", ORANGE, badge_fill=ORANGE, plus_color=PURPLE, detail=True, mask_id="cut")}</g>
  <text x="940" y="520" font-family="Helvetica Neue, Helvetica, Arial, sans-serif" font-size="128" font-weight="700" fill="#FFFFFF">Additional</text>
  <text x="940" y="660" font-family="Helvetica Neue, Helvetica, Arial, sans-serif" font-size="128" font-weight="700" fill="#FFFFFF">Material</text>
  <text x="944" y="740" font-family="Helvetica Neue, Helvetica, Arial, sans-serif" font-size="46" fill="#BDB2C7">Course material downloads for Jellyfin</text>
</svg>
'''


if __name__ == "__main__":
    here = Path(__file__).resolve().parent
    (here / "icon.svg").write_text(icon_svg())
    (here / "catalog.svg").write_text(catalog_svg())
    print("wrote", here / "icon.svg", "and", here / "catalog.svg")
