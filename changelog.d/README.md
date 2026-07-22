# changelog.d — one file per change

Put your changelog entry here instead of editing `CHANGELOG.md`.

Every branch used to append to the same `### Added` block, so two branches landing in parallel
conflicted on that one line — reliably, and on a file where the conflict carries no information: both
sides are almost always pure additions and the resolution is "keep both". A fragment is a file per
change, named after its ticket, so two branches never touch the same path and there is nothing to
conflict on.

## The format

```
changelog.d/<ticket>.<category>.md
```

`<category>` is one of `added`, `changed`, `deprecated`, `removed`, `fixed`, `security` — the
[Keep a Changelog](https://keepachangelog.com/) sections. The file holds the markdown bullet(s):

```markdown
- **A query that ends in a semicolon can be paged again.** `SELECT * FROM Donations;` failed with
  "Incorrect syntax near the keyword 'ORDER'", because paging appends its `ORDER BY … OFFSET` *after*
  the statement — semicolon and all.
```

Same writing rules as before: user-facing, describing what changed for the person *using* SQL
Explorer. Several bullets in one file is fine, and one ticket may have a file per category
(`SE-197.added.md` and `SE-197.fixed.md`).

## What happens to them

Nothing, until a release. `tools/changelog-render.py` folds every fragment into `CHANGELOG.md`'s
`## [Unreleased]` section, in Keep a Changelog order, and deletes the fragments. The release workflow
runs it before generating the notes, so the existing pipeline — release notes, `update.json`, and the
roll into a dated version heading — is unchanged and still reads `CHANGELOG.md`.

To see what the section will look like without changing anything:

```bash
python tools/changelog-render.py --dry-run
```

CI runs `--check`, which only validates the filenames — a fragment named `SE-190.fix.md` (no such
category) fails the build rather than being silently skipped at release time.

## Still edit CHANGELOG.md directly?

For a released section, or to correct something already rolled — and for one case that is easy to miss:
**a fragment can only add, never amend.** When a later change makes an *already written* unreleased entry
wrong, the fix is to edit that entry, wherever it lives.

That matters more than it sounds. Work lands in pieces, so a feature's first entry ("SQLite is a planned
follow-up") is routinely overtaken by the piece that follows it, and two bullets that contradict each
other are worse than one that is merely incomplete. Before a release, read the whole `[Unreleased]`
section as a user would — as one description of what changed, not as a pile of commits:

```bash
python tools/changelog-render.py --dry-run
```

Two things to look for, both of which bit us before 0.5.0:

- **Superseded claims.** The first Copy Table entry still said indexes, foreign keys and SQLite were
  follow-ups, three entries above the one that added them.
- **"Fixed" for something that never shipped.** A bug introduced and fixed between two releases was never
  a bug anyone had. Fold what it says into the feature's own entry and drop the Fixed bullet — check the
  previous version's section to see whether the thing had actually shipped.
