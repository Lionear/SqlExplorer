#!/usr/bin/env python3
"""Regenerate THIRD-PARTY-NOTICES.md from the NuGet dependency closure (SE-127).

Why a script and not a hand-kept list: the shipped closure is ~100 packages deep and changes with every
dependency bump, so a manual list rots silently. Why not an off-the-shelf tool: everything needed is
already in `project.assets.json` (the resolved closure, transitives included) plus each package's
`.nuspec` in the local NuGet cache, and ~97% of packages carry a machine-readable SPDX expression.

Scope is the *shipped* set only: projects under `src/`. The `plugins/` projects are Debug-only (see the
conditional ProjectReferences in SqlExplorer.App.csproj) and Store-installed plugins carry their own
notices — bundling those here would mean rewriting this file on every Store install.

Run after changing dependencies, then commit the result:

    python3 tools/generate-third-party-notices.py

`--check` regenerates in memory and exits non-zero if the committed file is out of date, without writing.
A generator you have to remember to run can still rot, so this is the hook to wire into CI once the
workflow builds on the branches where dependencies actually change:

    python3 tools/generate-third-party-notices.py --check

Requires a restore to have run (project.assets.json must exist). Exits non-zero on missing data so a
silently incomplete notices file can't be committed.
"""

import json
import os
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

REPO = pathlib.Path(__file__).resolve().parent.parent
NUGET_CACHE = pathlib.Path(os.environ.get("NUGET_PACKAGES", pathlib.Path.home() / ".nuget" / "packages"))
OUTPUT = REPO / "THIRD-PARTY-NOTICES.md"

# Escape hatch for packages whose .nuspec predates SPDX expressions and only carries the deprecated
# <licenseUrl>: the URL alone is not a licence identifier, so map it by hand. Empty today — every shipped
# package declares an expression or embeds its licence text. Add entries only when a dependency forces it.
LICENSE_URL_FALLBACK: dict[str, str] = {}


def shipped_assets():
    """project.assets.json for every project that ships (src/), skipping build output."""
    found = [p for p in (REPO / "src").glob("**/project.assets.json") if "/bin/" not in str(p)]
    if not found:
        sys.exit("No project.assets.json under src/ — run `dotnet restore` first.")
    return found


def resolve_packages():
    """name -> version for the whole resolved closure of the shipped projects."""
    packages = {}
    for assets in shipped_assets():
        data = json.loads(assets.read_text())
        for key, lib in data.get("libraries", {}).items():
            if lib.get("type") != "package":
                continue
            name, version = key.split("/", 1)
            # Several projects can resolve the same package at different versions; keep the highest so
            # the notice matches what actually ends up next to the executable.
            if name not in packages or _newer(version, packages[name]):
                packages[name] = version
    return packages


def _newer(a, b):
    def parts(v):
        return [int(x) if x.isdigit() else x for x in re.split(r"[.\-+]", v)]

    try:
        return parts(a) > parts(b)
    except TypeError:
        return a > b  # pre-release tags etc. — string order is good enough to pick one


def nuspec_path(name, version):
    return NUGET_CACHE / name.lower() / version / f"{name.lower()}.nuspec"


def read_license(name, version):
    """(spdx_or_label, project_url, embedded_licence_text_or_None)."""
    path = nuspec_path(name, version)
    if not path.exists():
        return None, None, None

    # Strip the default namespace so ElementTree lookups stay readable.
    xml = re.sub(r'\sxmlns="[^"]+"', "", path.read_text(encoding="utf-8", errors="replace"), count=1)
    meta = ET.fromstring(xml).find("metadata")
    if meta is None:
        return None, None, None

    project_url = (meta.findtext("projectUrl") or "").strip() or None
    license_el = meta.find("license")

    if license_el is not None and license_el.get("type") == "expression":
        return (license_el.text or "").strip(), project_url, None

    if license_el is not None and license_el.get("type") == "file":
        # The licence text ships inside the package; include it verbatim rather than guessing an SPDX id.
        text_path = path.parent / (license_el.text or "").strip()
        text = text_path.read_text(encoding="utf-8", errors="replace") if text_path.exists() else None
        return "See licence text below", project_url, text

    if meta.findtext("licenseUrl"):
        return LICENSE_URL_FALLBACK.get(name, "See project URL"), project_url, None

    return None, project_url, None


def main():
    packages = resolve_packages()
    rows, embedded, unknown = [], [], []

    for name in sorted(packages, key=str.lower):
        version = packages[name]
        spdx, url, text = read_license(name, version)
        if spdx is None:
            unknown.append(f"{name} {version}")
            continue
        rows.append((name, version, spdx, url))
        if text:
            embedded.append((name, version, text.strip()))

    if unknown:
        sys.exit("No licence metadata for:\n  " + "\n  ".join(unknown))

    out = [
        "# Third-party notices",
        "",
        "Lionear SQL Explorer bundles the open-source packages below. Each remains under its own licence;",
        "this file reproduces the attribution those licences require.",
        "",
        "> Generated by `tools/generate-third-party-notices.py` from the NuGet dependency closure —",
        "> do not edit by hand. Re-run it after changing dependencies.",
        "",
        "Plugins installed from the Plugin Store are not listed here: they ship their own dependency",
        "closure and carry their own notices.",
        "",
        f"## Packages ({len(rows)})",
        "",
        "| Package | Version | Licence |",
        "|---|---|---|",
    ]
    for name, version, spdx, url in rows:
        label = f"[{name}]({url})" if url else name
        out.append(f"| {label} | {version} | {spdx} |")

    if embedded:
        out += ["", "## Licence texts", "",
                "Packages that ship their licence as a file rather than an SPDX expression:", ""]
        for name, version, text in embedded:
            out += [f"### {name} {version}", "", "```", text, "```", ""]

    rendered = "\n".join(out).rstrip() + "\n"

    if "--check" in sys.argv:
        current = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else ""
        if current != rendered:
            sys.exit(f"{OUTPUT.name} is out of date — re-run {pathlib.Path(__file__).name} and commit the result.")
        print(f"{OUTPUT.name} is up to date — {len(rows)} packages.")
        return

    OUTPUT.write_text(rendered, encoding="utf-8")
    print(f"Wrote {OUTPUT.relative_to(REPO)} — {len(rows)} packages, {len(embedded)} embedded licence texts.")


if __name__ == "__main__":
    main()
