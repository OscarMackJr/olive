---
phase: 01-foundation-and-walking-skeleton
plan: 07
subsystem: ci-devex
tags: [ci, github-actions, dotenv, readme]
requires: [01-06]
provides: [ubuntu CI definition, .env.example, README development flow, open coordination items]
affects: [.github/workflows/ci.yml, .env.example, README.md, .gitattributes]
key-files:
  created: [.github/workflows/ci.yml, .env.example, tests/Olive.Tests/CiAndDevConfigTests.cs]
  modified: [README.md, .gitattributes, .planning/STATE.md, .planning/REQUIREMENTS.md]
requirements-completed: [FND-07]
metrics:
  completed: 2026-10-21
---

# Phase 1 Plan 07: CI and developer run path Summary

Ubuntu GitHub Actions workflow (locked restore, Release build, unit and full Testcontainers tests, vocabulary SHA-256 check, vulnerable-package gate), a loader-verified `.env.example`, and a README development flow.

## Commits
- 8d847b7: CI workflow, .env.example, static tests, .gitattributes lockfile LF entry
- Task 2 commit: README, STATE.md open items (Open coordination items subsection, 7 entries), REQUIREMENTS.md DB-03/DB-04 set to Partial (checkboxes left unchecked)

## Local rehearsal
`dotnet restore --locked-mode` exit 0; `dotnet build -c Release --no-restore` 0 errors; `dotnet test -c Release --no-build`: 226 passed, 0 failed, 0 skipped, about 8 s. 8 new tests (FND_07 / FND_04_env_example) pass.

## Notes
- Action versions: `actions/checkout@v4` and `actions/setup-dotnet@v4` were used per RESEARCH A7. They could not be verified as current from this environment (no network check done); confirm on the first run.
- Windows/Ubuntu lockfile risk: `*.json text eol=lf` already covered lockfiles; added explicit `packages.lock.json text eol=lf` plus a test. If `--locked-mode` still fails on Ubuntu, regenerate lockfiles on Linux (Pitfall 7).
- The workflow was not run on GitHub and not pushed. Workflow YAML was checked only via static tests; no YAML linter ran.
- The vulnerability gate command was not executed locally.
- ROADMAP.md and STATE.md progress fields untouched.

## Human check (pending)
After pushing to GitHub, confirm the `ci` workflow is green on ubuntu-latest, including the locked restore and Testcontainers tests.

## Deviations
None.

## Self-Check: PASSED
