# Olive agent constraints

- This repository is a scaffold. There is no real target-system connector, SDK,
  HTTP client, credential, or model call.
- Real executors require a signed divisional write agreement and a scoped credential
  (charter §3.3). Do not add one until both exist.
- Proposals originate outside Olive; Olive only validates, queues, approves and
  simulates execution.
- Do not edit `contracts/vendored/vocabulary.yaml`; update catalog entries instead.
- Keep queue payloads as references, never inline bodies. Keep tenant predicates in
  application queries even though PostgreSQL RLS is also enforced.
- Catalog action types are source-controlled. Do not add a runtime registration API.
