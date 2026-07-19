#!/usr/bin/env python3
"""Generate Icons.g.cs from the vendored Lucide SVGs in tools/lucide/.

Lucide icons are 24x24, stroke-based (fill="none", stroke-width 2, round caps) — the same
line-icon idiom SQL Explorer already draws by hand in NodeIcons.cs. Rather than pull in an
SVG-rendering runtime dependency (and lose the DynamicResource theme-brush tinting that a plain
Path gives us), we flatten each icon's primitives into a single StreamGeometry path string at
build time. The app keeps rendering <Path Data="{x:Static Icons.X}" Stroke="..."/> exactly as it
does today; only the source of the geometry changes from hand-drawn to Lucide.

Lucide uses seven primitive elements. Each is converted to SVG path commands:
  path      -> its d attribute, verbatim
  line      -> M x1 y1 L x2 y2
  polyline  -> M p0 L p1 ...
  polygon   -> M p0 L p1 ... Z
  rect      -> sharp or (with rx/ry) rounded-rectangle path
  circle    -> two relative arcs
  ellipse   -> two relative arcs

Run:  python3 tools/generate-icons.py
Regenerate whenever you add or remove an SVG under tools/lucide/.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SVG_DIR = ROOT / "tools" / "lucide"
OUT = ROOT / "src" / "SqlExplorer.App" / "ViewModels" / "Icons.g.cs"

# Match a self-closing SVG primitive element and capture its tag + raw attribute blob.
ELEMENT_RE = re.compile(
    r"<(path|line|polyline|polygon|rect|circle|ellipse)\b([^>]*?)/>",
    re.DOTALL,
)
ATTR_RE = re.compile(r'([\w:-]+)\s*=\s*"([^"]*)"')


def fmt(value: float) -> str:
    """Format a number the way SVG path data does: no trailing zeros, no bare '.0'."""
    if value == int(value):
        return str(int(value))
    return f"{value:.4f}".rstrip("0").rstrip(".")


def attrs(blob: str) -> dict[str, str]:
    return {k: v for k, v in ATTR_RE.findall(blob)}


def num(a: dict[str, str], key: str, default: float = 0.0) -> float:
    raw = a.get(key)
    return float(raw) if raw not in (None, "") else default


def points_to_path(raw: str, close: bool) -> str:
    coords = [float(n) for n in re.findall(r"-?\d*\.?\d+", raw)]
    pairs = list(zip(coords[0::2], coords[1::2]))
    if not pairs:
        return ""
    segs = [f"M {fmt(pairs[0][0])} {fmt(pairs[0][1])}"]
    segs += [f"L {fmt(x)} {fmt(y)}" for x, y in pairs[1:]]
    if close:
        segs.append("Z")
    return " ".join(segs)


def rect_to_path(a: dict[str, str]) -> str:
    x, y = num(a, "x"), num(a, "y")
    w, h = num(a, "width"), num(a, "height")
    rx = a.get("rx")
    ry = a.get("ry")
    rxv = float(rx) if rx not in (None, "") else (float(ry) if ry not in (None, "") else 0.0)
    ryv = float(ry) if ry not in (None, "") else rxv
    rxv = min(rxv, w / 2)
    ryv = min(ryv, h / 2)
    if rxv <= 0 or ryv <= 0:
        return f"M {fmt(x)} {fmt(y)} h {fmt(w)} v {fmt(h)} h {fmt(-w)} Z"
    return (
        f"M {fmt(x + rxv)} {fmt(y)} "
        f"h {fmt(w - 2 * rxv)} "
        f"a {fmt(rxv)} {fmt(ryv)} 0 0 1 {fmt(rxv)} {fmt(ryv)} "
        f"v {fmt(h - 2 * ryv)} "
        f"a {fmt(rxv)} {fmt(ryv)} 0 0 1 {fmt(-rxv)} {fmt(ryv)} "
        f"h {fmt(-(w - 2 * rxv))} "
        f"a {fmt(rxv)} {fmt(ryv)} 0 0 1 {fmt(-rxv)} {fmt(-ryv)} "
        f"v {fmt(-(h - 2 * ryv))} "
        f"a {fmt(rxv)} {fmt(ryv)} 0 0 1 {fmt(rxv)} {fmt(-ryv)} Z"
    )


def circle_to_path(a: dict[str, str]) -> str:
    cx, cy, r = num(a, "cx"), num(a, "cy"), num(a, "r")
    return (
        f"M {fmt(cx - r)} {fmt(cy)} "
        f"a {fmt(r)} {fmt(r)} 0 1 0 {fmt(2 * r)} 0 "
        f"a {fmt(r)} {fmt(r)} 0 1 0 {fmt(-2 * r)} 0 Z"
    )


def ellipse_to_path(a: dict[str, str]) -> str:
    cx, cy = num(a, "cx"), num(a, "cy")
    rx, ry = num(a, "rx"), num(a, "ry")
    return (
        f"M {fmt(cx - rx)} {fmt(cy)} "
        f"a {fmt(rx)} {fmt(ry)} 0 1 0 {fmt(2 * rx)} 0 "
        f"a {fmt(rx)} {fmt(ry)} 0 1 0 {fmt(-2 * rx)} 0 Z"
    )


def absolutize_leading_moveto(d: str) -> str:
    """Make a path fragment self-anchored so it survives concatenation.

    SVG treats a leading relative moveto (``m``) as absolute only when it is the very first
    command of a path. Once we join several elements into one geometry string, a later fragment
    that opens with ``m`` would otherwise be placed relative to the previous subpath's end point.
    We rewrite that opening ``m`` to an absolute ``M`` and re-express any trailing coordinate
    pairs of the moveto run (implicit relative linetos) as an explicit ``l`` so their meaning is
    preserved.
    """
    d = d.strip()
    if not d or d[0] != "m":
        return d
    run_match = re.match(r"m([^A-Za-z]*)(.*)", d, re.DOTALL)
    if not run_match:
        return d
    nums = re.findall(r"-?\d*\.?\d+", run_match.group(1))
    if len(nums) < 2:
        return d
    out = f"M{nums[0]} {nums[1]}"
    extra = nums[2:]
    if extra:
        out += " l " + " ".join(extra)
    remainder = run_match.group(2).strip()
    if remainder:
        out += " " + remainder
    return out


def element_to_path(tag: str, a: dict[str, str]) -> str:
    if tag == "path":
        return absolutize_leading_moveto(" ".join(a.get("d", "").split()))
    if tag == "line":
        return f"M {fmt(num(a, 'x1'))} {fmt(num(a, 'y1'))} L {fmt(num(a, 'x2'))} {fmt(num(a, 'y2'))}"
    if tag == "polyline":
        return points_to_path(a.get("points", ""), close=False)
    if tag == "polygon":
        return points_to_path(a.get("points", ""), close=True)
    if tag == "rect":
        return rect_to_path(a)
    if tag == "circle":
        return circle_to_path(a)
    if tag == "ellipse":
        return ellipse_to_path(a)
    return ""


def svg_to_geometry(text: str) -> str:
    parts = []
    for tag, blob in ELEMENT_RE.findall(text):
        frag = element_to_path(tag, attrs(blob))
        if frag:
            parts.append(frag)
    return " ".join(parts)


def pascal(name: str) -> str:
    return "".join(word.capitalize() for word in name.split("-"))


def main() -> int:
    svgs = sorted(SVG_DIR.glob("*.svg"))
    if not svgs:
        print(f"no SVGs found in {SVG_DIR}", file=sys.stderr)
        return 1

    icons: list[tuple[str, str, str]] = []  # (csharp name, lucide name, geometry)
    for svg in svgs:
        lucide = svg.stem
        geometry = svg_to_geometry(svg.read_text(encoding="utf-8"))
        if not geometry:
            print(f"warning: {lucide} produced no geometry", file=sys.stderr)
            continue
        icons.append((pascal(lucide), lucide, geometry))

    icons.sort(key=lambda t: t[0])

    lines = [
        "// <auto-generated>",
        "//     Generated by tools/generate-icons.py from the vendored Lucide SVGs in tools/lucide/.",
        "//     Do NOT edit by hand — add or remove an SVG under tools/lucide/ and re-run the generator.",
        "//     Lucide (https://lucide.dev) is licensed ISC; see THIRD-PARTY-NOTICES.md.",
        "// </auto-generated>",
        "",
        "using Avalonia.Media;",
        "",
        "namespace SqlExplorer.App.ViewModels;",
        "",
        "/// <summary>",
        "/// Line-icon geometries flattened from Lucide SVGs, drawn as stroked Paths — the same idiom as",
        "/// the hand-drawn <see cref=\"NodeIcons\"/>. NodeIcons maps app concepts (a schema-tree node kind,",
        "/// a toolbar action) onto these raw icons; render with a themed Stroke brush, Stretch=\"Uniform\".",
        "/// </summary>",
        "public static class Icons",
        "{",
    ]
    for cs, lucide, geometry in icons:
        lines.append(f"    /// <summary>Lucide <c>{lucide}</c>.</summary>")
        lines.append(f'    public static readonly Geometry {cs} = Parse("{geometry}");')
        lines.append("")
    lines.append("    private static Geometry Parse(string data) => StreamGeometry.Parse(data);")
    lines.append("}")

    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)} — {len(icons)} icons")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
