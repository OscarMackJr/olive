# Dev reviewer identity fixtures

Source-controlled stand-ins for an Entra-authenticated reviewer (D-07). Non-production only.

- The `X-Olive-Dev-Reviewer` request header carries only a `reviewer_id` (the fixture file stem). Identity, `oid`, `tid` and groups always come from these files, never from the request.
- `upn` is display-only and is never compared. Self-approval is decided on `oid` plus `tid`.
- `alpha-lookalike` shares a UPN with `alpha-self` but has a different `oid`, to prove that.
- Phase 7 replaces this scheme with Entra OIDC emitting the same claims.
