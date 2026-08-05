# olive

Status: **scaffold — simulated executor only; no real write path.**

Olive validates and routes externally proposed actions and hosts the shared review queue.
It does not generate proposals, call models, or connect to a target system.  See
[`docs/STATUS.md`](docs/STATUS.md) for entry criteria and open decisions.

The shared vocabulary specification remains a read-only reference at
`~/_agent-inputs/olive/PROGRAM_SHARED_VOCABULARY_v0.4.md`; its machine-readable
counterpart is vendored under `contracts/vendored/`.
