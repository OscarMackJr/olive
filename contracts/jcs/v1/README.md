# JCS golden vectors (v1)

Language-neutral vectors for the RFC 8785 JSON Canonicalization Scheme as Olive uses it to compute
`proposal_hash` (SHA-256 over the JCS of exactly `action_type`, `party_ref`, `parameters`, `justification`)
and the idempotency body hash (SHA-256 over the JCS of the whole request body, including `nono_session_id`).
Hometown must reproduce these bytes and hashes exactly so both sides agree on one canonical form.

## Layout

- `<name>.input.json` is the raw request-body text, exactly as sent.
- `<name>.canonical.json` is the expected JCS output, exact bytes, UTF-8, no BOM, no trailing newline.
  Reject-only cases have no canonical file.
- `vectors.json` is the manifest (checked by `vectors.schema.json`).
- Files are byte-stable: `.gitattributes` marks `contracts/jcs/v1/*` as `-text`, so no line-ending conversion applies.

## Two verdicts per case

1. `canonicalize`: pure RFC 8785 output for any finite JSON number. This is what `canonical` and `sha256` record.
   For `kind: proposal_hash` the canonical file is the JCS of the four hashed fields only.
2. `gate`: Olive's I-JSON policy, applied before any hashing so that neither side hashes a body the other refuses.
   Values are `accept`, `reject_lossy_number`, `reject_duplicate_key`, `reject_lone_surrogate`.
   A number is lossless when its significant decimal digits and exponent equal the shortest round-trip text of the
   double it parses to (so `0.1`, `1.0`, `1e0` pass; `9007199254740993` does not).
   Cases with `expect: "reject"` are pure negatives and carry no canonical form.

Example: `cyberphone-values` canonicalizes `333333333.33333329` to `333333333.3333333`, but its gate verdict is
`reject_lossy_number` because two different tokens would otherwise share one hash.

## Assumption recorded

The two-verdict design and the lossless-number reading are the adopted recommendations from phase research
(assumption A1); they have not yet been confirmed by hometown.

## Hometown manual verification

Hand this folder to the hometown owner. They run their canonicalizer over every `*.input.json`, compare bytes with
`*.canonical.json`, compare SHA-256 with the manifest, and confirm they refuse every case whose `gate` is not `accept`.
Record the pass result (date, owner, implementation) in the phase verification notes.

## Deliberate divergence

The gate verdicts are stricter than RFC 8785, which accepts any finite number and does not address duplicate keys
or unpaired surrogates. The strictness is intentional (D-05, D-18) and is not a defect in the canonicalizer.

## Provenance and licence

`cyberphone-*` files are taken unmodified from https://github.com/cyberphone/json-canonicalization (`testdata/input`
and `testdata/output`), licensed under the Apache License 2.0, copyright 2018 Anders Rundgren.
`rfc8785-sorting` follows the property-sorting example in RFC 8785 section 3.2.3. `olive-*` and the negative cases
are Olive's own.
