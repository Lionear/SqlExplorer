#!/usr/bin/env python3
"""Fold the changelog fragments in changelog.d/ into CHANGELOG.md's [Unreleased] section.

Why fragments at all: every branch used to append to the same `### Added` block, so two branches
landing in parallel conflicted on that one line — reliably, and on a file whose conflicts are pure
noise. A fragment is a file per change, named after the ticket, so two branches never touch the same
path and there is nothing to conflict on.

    changelog.d/SE-190.fixed.md      ->  a bullet under "### Fixed"
    changelog.d/SE-192.added.md      ->  a bullet under "### Added"

The category is the second-to-last dot-part of the filename; the body is markdown, normally one `- `
bullet but any number is fine. Everything else about the changelog is unchanged: this only *writes*
the section the existing release tooling already reads (`changelog-unreleased.sh`) and archives
(`changelog-roll.sh`), so the release pipeline needed no new concepts.

Usage:
    python tools/changelog-render.py            # fold fragments in, delete them
    python tools/changelog-render.py --dry-run  # print the new [Unreleased] section, change nothing
    python tools/changelog-render.py --check    # validate fragment names only (CI); no writes

Exit codes: 0 ok, 1 a fragment is unusable (bad category, empty body).
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Keep a Changelog's order. A fragment's category must be one of these, lowercased in the filename.
CATEGORIES = ["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"]
CATEGORY_BY_KEY = {c.lower(): c for c in CATEGORIES}

PLACEHOLDER = "_Nothing yet._"
UNRELEASED = "## [Unreleased]"


def fragments(folder: Path) -> tuple[dict[str, list[str]], list[Path], list[str]]:
    """Read every fragment into {Category: [body, ...]}, plus the files read and any complaints."""
    found: dict[str, list[str]] = {}
    files: list[Path] = []
    problems: list[str] = []

    for path in sorted(folder.glob("*.md")):
        if path.name.lower() == "readme.md":
            continue

        parts = path.name[: -len(".md")].split(".")
        key = parts[-1].lower() if len(parts) >= 2 else ""
        category = CATEGORY_BY_KEY.get(key)
        if category is None:
            problems.append(
                f"{path.name}: no category — name it <ticket>.<{'|'.join(CATEGORY_BY_KEY)}>.md"
            )
            continue

        body = path.read_text(encoding="utf-8").strip("\n").rstrip()
        if not body.strip():
            problems.append(f"{path.name}: empty")
            continue

        found.setdefault(category, []).append(body)
        files.append(path)

    return found, files, problems


def split_changelog(text: str) -> tuple[str, str, str]:
    """The changelog around its [Unreleased] body: (before, unreleased body, after).

    Anchored to the start of a line: the file's own header explains the convention and contains the
    literal `## [Unreleased]` in prose, so a plain substring search matches that first and splits the
    file in the wrong place.
    """
    heading = re.search(rf"^{re.escape(UNRELEASED)}\s*$", text, flags=re.MULTILINE)
    if heading is None:
        raise ValueError(f"no '{UNRELEASED}' heading")

    start = heading.end()
    rest = text[start:]

    # The body runs to the next version heading, or to the reference-link block when it is last.
    match = re.search(r"^(## \[|\[[^\]]+\]:)", rest, flags=re.MULTILINE)
    end = start + (match.start() if match else len(rest))
    return text[:start], text[start:end], text[end:]


def parse_body(body: str) -> tuple[list[str], dict[str, list[str]]]:
    """Existing [Unreleased] content as (preamble lines, {Category: [entry, ...]}).

    An entry is a bullet plus its continuation lines, so a wrapped bullet stays intact.
    """
    preamble: list[str] = []
    sections: dict[str, list[str]] = {}
    current: str | None = None

    for line in body.splitlines():
        heading = re.match(r"^### (.+?)\s*$", line)
        if heading:
            current = heading.group(1).strip()
            sections.setdefault(current, [])
            continue

        if line.strip() == PLACEHOLDER:
            continue

        if current is None:
            preamble.append(line)
        elif line.startswith("- "):
            sections[current].append(line)
        elif line.strip() and sections[current]:
            sections[current][-1] += "\n" + line          # continuation of the previous bullet
        elif line.strip():
            sections[current].append(line)                 # stray prose inside a section

    return preamble, sections


def render(existing: dict[str, list[str]], new: dict[str, list[str]]) -> str:
    """The [Unreleased] body with the fragments folded in, sections in Keep a Changelog order."""
    merged: dict[str, list[str]] = {}
    for category in CATEGORIES:
        entries = list(existing.get(category, []))
        entries += new.get(category, [])
        if entries:
            merged[category] = entries

    # A section the changelog uses but Keep a Changelog doesn't name keeps its place at the end,
    # rather than being silently dropped.
    for category, entries in existing.items():
        if category not in CATEGORIES and entries:
            merged[category] = entries

    if not merged:
        return f"\n\n{PLACEHOLDER}\n\n"

    out = ["", ""]
    for category, entries in merged.items():
        out.append(f"### {category}")
        out.append("")
        out.extend(entries)
        out.append("")

    return "\n".join(out) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--changelog", default=str(ROOT / "CHANGELOG.md"))
    parser.add_argument("--fragments", default=str(ROOT / "changelog.d"))
    parser.add_argument("--dry-run", action="store_true", help="print the result; write nothing")
    parser.add_argument("--check", action="store_true", help="validate fragment names only")
    args = parser.parse_args()

    folder = Path(args.fragments)
    if not folder.is_dir():
        print(f"No fragment folder at {folder}", file=sys.stderr)
        return 0

    found, files, problems = fragments(folder)
    for problem in problems:
        print(f"changelog.d/{problem}", file=sys.stderr)
    if problems:
        return 1

    if args.check:
        print(f"{len(files)} fragment(s) OK")
        return 0

    if not files:
        print("No fragments to fold in.")
        return 0

    path = Path(args.changelog)
    text = path.read_text(encoding="utf-8")
    try:
        before, body, after = split_changelog(text)
    except ValueError as error:
        print(f"{path}: {error}", file=sys.stderr)
        return 1

    preamble, existing = parse_body(body)
    rendered = render(existing, found)
    if preamble and any(line.strip() for line in preamble):
        rendered = "\n" + "\n".join(preamble).strip("\n") + "\n" + rendered

    if args.dry_run:
        print(UNRELEASED + rendered.rstrip())
        return 0

    path.write_text(before + rendered + after.lstrip("\n"), encoding="utf-8")
    for file in files:
        file.unlink()

    print(f"Folded {len(files)} fragment(s) into {path.name} and removed them.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
