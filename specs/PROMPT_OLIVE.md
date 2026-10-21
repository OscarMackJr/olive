# Work Prompt — Olive Owner / Implementer

You own the action-broker boundary. Read the cross-component execution
specification, Olive Charter v0.4, Popeye v0.4.2, the agent-key scope decision,
and Liam design/test specifications v0.4 before implementation.

## Objective

Approve Olive's component boundary and replace S-OL-1 with an owner-approved
proposal API that accepts at most one Liam proposal per run, authorizes it
before queueing, and never allows Liam or Hometown to execute an action.

## First deliverable: charter and wire-contract decision

The charter is a draft. Obtain the required approval for Olive's existence and
boundary before code that holds a write credential. Then publish a versioned
proposal contract that defines:

- endpoint, authentication, status/error taxonomy, versioning, and replay or
  idempotency semantics;
- request fields for action type, party reference, parameters, justification,
  Hometown trace reference, Liam run ID, and Nono session identifier;
- response fields for proposal ID and lifecycle state;
- catalog/version discovery or pinning and parameter-schema validation;
- the correlation shape required to join a proposal to Hometown's trace and
  the agent-key evidence.

The proposal submitter remains Liam unless the approved contract changes that
ratified decision. A difference from S-OL-1 requires a compatibility table and
joint Liam-owner review.

## Required implementation work after approval

1. Accept the session agent key only as injected by Nono's proxy. Validate it
   with Popeye introspection and authorize on the intersection of the agent and
   `on_behalf_of` entitlements.
2. Validate catalog membership, version, and parameters before the proposal
   reaches a review queue. Refused-policy proposals never appear in the queue.
3. Implement the proposal lifecycle, per-proposal approval, expiry, and
   idempotent execution keyed by proposal ID.
4. Keep the narrow target-system write credential solely in Olive. Never
   forward the session agent key to a target system.
5. Store queue payloads by reference, not inline bodies; make decisions
   auditable by actor, time, and rationale.

## Evidence required

- Tests for all action and queue requirements, including policy refusal before
  queueing, duplicate execution, expired proposal refusal, and inline-payload
  rejection.
- Tests that unregistered/user/agent callers cannot use the Popeye
  introspection path as a service caller.
- A capture proving the incoming session key is not forwarded to the target
  system, plus a credential audit showing only Olive holds its write
  credential.
- Contract fixtures that Liam can use only after the owner approves the final
  wire shape.

## Stop for review if

The implementation would give Liam a write credential, let Hometown execute a
proposal, queue a policy-refused action, accept an unvalidated key, or require
the session key to reach a target system.
