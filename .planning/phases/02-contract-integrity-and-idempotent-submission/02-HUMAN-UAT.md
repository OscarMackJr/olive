---
status: partial
phase: 02-contract-integrity-and-idempotent-submission
source: [02-VERIFICATION.md]
started: 2026-10-22T12:54:21Z
updated: 2026-10-22T13:54:40Z
---

## Current Test

[awaiting human testing]

## Tests

### 1. Hometown confirms the shared JCS golden vectors in contracts/jcs/v1 (Q20)
expected: hometown runs contracts/jcs/v1/vectors.json against its JCS and every canonical case matches byte-for-byte and SHA-256; every reject case is rejected by its gate. The two-verdict design and assumption A1 (lossless-number rule) are accepted.
result: [pending]

### 2. Decide disposition of code-review CR-01 / CR-02 (catalog vetting gaps)
expected: user decides whether CR-01 (parameters subtree not closed: true / {} / array without items accepted) and CR-02 (dangling local $ref passes API-10 startup guard, then 500s) block phase 2 or are closed by a gap plan before a second catalog entry is added. Also decide WR-02 (idempotent replay ignores on_behalf_of / purpose).
result: passed — user chose "fix now"; CR-01, CR-02, WR-01..WR-06 fixed (02-REVIEW-FIX.md, commits 8d1fad0..491050d), full suite 589/589. WR-02 fix (replay also requires matching on_behalf_of/purpose; rotated key still replays 200) flagged for human confirmation of the semantics.

## Summary

total: 2
passed: 1
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps
