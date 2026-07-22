# Contributing to SQL Explorer

Thanks for your interest. SQL Explorer is maintained by one person, so the bar for contributions is
high and the rules below are not negotiable. Reading them before you open a pull request saves
everyone time.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Not writing code? Still useful

The high bar below applies to **code in pull requests**. A clear, reproducible bug report or a
well-argued feature idea costs you little and is one of the most helpful things you can send. Open an
[issue](https://github.com/Lionear/SqlExplorer/issues) for either.

## Pull request policy

This project is built in an AI-assisted workflow itself (direction, architecture and acceptance
testing are the maintainer's; a large share of the implementation is written by AI agents
orchestrated through Claude Code). So to be clear: PRs are judged on the result, not on what tool
produced them. That cuts both ways.

- **PRs that have not been checked by the author are rejected outright.** If you used an AI
  assistant, you are responsible for the result: it must build with zero warnings, stay consistent
  with the surrounding code, and read like code you would sign your name to. "My assistant generated
  it" is not a review.
- **PRs that exist to force an AI review or "another opinion" are closed without discussion.**
  Project direction rests with the maintainer; drive-by rewrites do not.
- **Stay in scope.** One PR, one topic. No unrequested refactors, renames or "while I was here"
  changes bundled in.

A PR that does not meet the bar is closed, not line-by-line reviewed. Don't take it personally —
it keeps a one-person project alive.

## Before you open a PR

1. **Open an issue first** for anything beyond a trivial fix, so the approach can be agreed before
   you spend time on it.
2. **Match the codebase.** Conventions to hold: C# / .NET 10, Avalonia + MVVM (CommunityToolkit.Mvvm),
   code and comments in English, one top-level type per file, no new third-party dependencies without
   prior agreement in the issue, comments explain *why* rather than restating the code. House style:
   file-scoped namespaces, nullable enabled, Allman braces, primary constructors, `Async` suffix,
   `ct` as the last parameter.
3. **Build clean.** `dotnet build` with zero warnings. There is no automated test suite yet, so
   verify behaviour by running the affected flow (`dotnet run --project src/SqlExplorer.Desktop`);
   UI changes should be checked visually.
4. **Respect the plugin boundary.** A new database engine is **not** a change to the host — it is a
   new `src/SqlExplorer.Providers.*` project (or an external plugin) that references **only**
   `SqlExplorer.Sdk`, the public contract. No UI change, no `Core` dependency. Read
   [`docs/PLUGINS.md`](docs/PLUGINS.md) before adding provider or tool plugins.
5. **Mind the credential trust boundary.** Connection secrets are stored in the OS keychain via
   `ISecretStore` and never written to disk in plaintext, logged, or transmitted anywhere other than
   the database being connected to. Anything that would change that needs an issue first.
6. **Record it in the changelog.** When the work is finished, add a fragment under
   [`changelog.d/`](changelog.d/README.md) — see *Changelog* below. A finished item that leaves no
   trace there is not finished.

## Commit style

This repository uses [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <subject>

[optional body — the WHY, only when the diff doesn't make it obvious]
```

Types: `feat` · `fix` · `refactor` · `chore` · `docs` · `test` · `style` · `perf` · `ci`. Scope
(optional but encouraged) is the component or module, e.g. `feat(providers):`, `fix(updater):`.
Subject: short, imperative, lowercase, no trailing period.

## Changelog

Every finished work item lands in the changelog, so that from one release to the next it is clear what
actually changed — without reading the git log. The file follows
[Keep a Changelog](https://keepachangelog.com/).

**Write a fragment, not `CHANGELOG.md` itself.** One file per change, named after its ticket:

```
changelog.d/SE-190.fixed.md
```

`<category>` is the part before `.md`: `added`, `changed`, `deprecated`, `removed`, `fixed` or
`security`. The file holds the markdown bullet(s). See
[`changelog.d/README.md`](changelog.d/README.md).

This exists because every branch used to append to the same `### Added` block, so two branches landing
in parallel conflicted on that one line — every time, on a file where the conflict carries no
information (both sides are additions; the resolution is always "keep both"). Separate files can't
collide.

- Pick the right category: **Added** for new features, **Changed** for changes in existing behaviour,
  **Fixed** for bug fixes, **Removed** for removed features. The commit types map straight onto these:
  `feat:` → **added**, `fix:` → **fixed**, `refactor:`/`perf:` → **changed**.
- Keep it user-facing: describe what changed for the person *using* SQL Explorer, not the class that
  changed.
- Never write a version heading yourself. `python tools/changelog-render.py --dry-run` shows what the
  next release's `[Unreleased]` section will look like; the release workflow folds the fragments in and
  deletes them.
- **Releasing is a tag.** The maintainer bumps `<Version>` in `Directory.Build.props`, then pushes a
  `v<semver>` tag (`git tag v0.3.0 && git push origin v0.3.0`). The Release workflow rolls
  `[Unreleased]` into a dated `## [0.3.0]` section and uses the same text as the GitHub release notes
  and the in-app updater. The tag is the version — the About dialog, the updater and the changelog
  all read it.

## Dependencies & notices

Bundled open-source dependencies keep their own licenses; the required attribution lives in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), generated from the NuGet closure. If your change
adds, removes or bumps a dependency, regenerate it (`python tools/generate-third-party-notices.py`;
`--check` verifies the committed file is current).

## License of contributions

The project is source-available under a split license: `src/SqlExplorer.Sdk` is
[MIT](src/SqlExplorer.Sdk/LICENSE) (the public plugin contract), everything else is
[Apache-2.0 with the Commons Clause](LICENSE). By submitting a contribution you agree that it is
licensed under the same terms as the part of the tree it touches. If that doesn't work for you, open
an issue before contributing.
