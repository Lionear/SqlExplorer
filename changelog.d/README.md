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

Only for a released section, or to correct something already rolled. For anything unreleased, write a
fragment — that is the whole point.
