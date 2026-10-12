# Native signer and isolated hosted nodes: delivery goal

Status: in progress. Working branch: `wip/remotesigner`.

The [native coverage inventory](NATIVE_SIGNER_COVERAGE.md) tracks implemented
boundaries and remaining acceptance gaps. No milestone is complete yet.

## Outcome

Run the full supported node through the native C# remote signer, with independent
signer validation and owner authorization, safe lifecycle recovery, and one
authoritative writer. Demonstrate two hosted node instances with distinct owners,
identities, state and signers, without either instance gaining authority over the
other's funds or secrets.

Fresh identities, wallets and databases are acceptable. Prior-version migration
is not required. Active channels must retain their keys and safety history.
Public documentation and PR descriptions/comments for this work must remain
within the four milestones below.

## 1. Complete native signer coverage and lifecycle recovery

Create an inventory of every enabled subsystem that consumes private material or
changes signing safety state. Record its typed operation, capability, durable
state, authorization requirement and proof. Interface forwarding alone is not
evidence of coverage.

| Subsystem | Required coverage |
| --- | --- |
| Transport and payment cryptography | Node ECDH, onion processing and related node-key operations |
| Channels | Allocation, opening/funding, commitments, HTLCs, revocation, reconnect, cooperative/force close, dual funding and splicing |
| On-chain resolution | Claims, timeouts, penalties, delayed sweeps, anchors, fee bumping and reorg recovery |
| Wallet | Supported accounts and address types, deposits, funding, withdrawals, input reservations and silent payments |
| Payments and discovery | BOLT 11, BOLT 12, blinded paths, keysend, forwarding, liquidity-related signatures and gossip signatures |
| Auxiliary consumers | Swap/keyring operations, backups, peer storage and accounting/administrative signatures |

Move remaining private-key consumers behind purpose-specific operations. Root,
wallet and channel private keys never leave the signer. Legitimate protocol
outputs, such as safely released per-commitment secrets, remain supported.
Preserve exact millisatoshi accounting and existing supported protocol behavior.

Every safety-changing lifecycle must persist its original signing intent and
request ID before dispatch, retain the exact receipt, and atomically consume that
receipt with the associated node transition. Persist publication intent before
wire or transaction broadcast; replay must reconcile the original operation.
Unknown outcomes block unsafe continuation. Never replace an uncertain request
with a new ID or fall back to local keys.

Completion gates:

- Every inventory row has implemented remote coverage and meaningful proof;
  silently disabling a currently supported subsystem does not count as completion.
- Crash tests cover intent saved, signer commit before reply, receipt saved,
  node transition consumed, and publication/retransmission boundaries for each
  distinct lifecycle. Nonce allocation/consumption and key-index allocation have
  explicit restart rules.
- Retry, retirement, data-loss and journal-capacity behavior fail safely. Receipt
  retention/compaction preserves reconciliation and cannot resurrect old state.
- Independent peer verification and Bitcoin transaction acceptance cover the
  relevant channel, wallet and on-chain paths. Explicit tests execute without
  skips; missing prerequisites fail the acceptance job.

## 2. Add independent native validation, authorization and writer fencing

Treat the node process as untrusted for financial decisions. The signer must
derive allowable transitions from its own durable history and verify relevant
transaction inputs, outputs, fees, signatures, balances, funding identities,
revocations and nonce use. Node-provided channel registration, wallet snapshots
and claims that a transition was saved are inputs to verify, not authority.

Bind owner-approved spending intent to the node, network, destination, amount,
fee limits, expiry and operation identity. Distinguish payments and withdrawals
from protocol-required forwarding and safety actions; bounded standing policy
may authorize the latter. Node credentials cannot grant spending permission,
change owner policy, switch owners or obtain a generic signing bypass.

Define an authenticated chain-evidence source and a durable freshness/fencing
authority. Document their trust assumptions. Local process locks and local files
alone do not establish protection against a compromised host restoring old state.
Check the current writer epoch at authoritative persistence and signer commits;
reject stale writers after failover, including requests already in flight.
Keep execution fencing separate from immutable logical request identity so
legitimate recovery retains the original signing intent and receipts.

Completion gates:

- Adversarial tests reject unauthorized spend, altered destination/amount/fee,
  fabricated funding or wallet state, conflicting commitments, premature secret
  release and nonce reuse without mutating signer safety state.
- A valid authenticated node request still fails when owner authorization or
  independent state validation fails. Authorized forwarding and protective
  on-chain actions continue under their explicit policy.
- Two competing writers cannot both advance one node/channel authority. A paused
  stale writer cannot commit or publish after ownership transfers.
- Old or mismatched node/signer snapshots cannot resume signing. Loss of required
  freshness evidence blocks signing; recovery does not reset safety history.

## 3. Introduce explicit node/owner/signer contexts

Define distinct identities for:

- `NodeId`: the logical runtime instance and its Lightning public identity/network.
- `OwnerId`: the principal governing funds, policy and administrative authority.
- `SignerId`: the assigned signing authority and verified public identity.

Bind these through immutable enrollment and authenticated execution context.
Resolve signer assignment from authorized configuration, not arbitrary
caller-supplied selectors. Bind channel, wallet and key-purpose authority to that
context; selecting a signer must never silently select another owner's keys.

Propagate context through service composition, persistence, signing workflows,
credentials, events and public APIs. Scope wallets, key indexes, invoices,
payments, circuits, reservations and receipts to the appropriate node/owner.
Public graph and chain data may be shared deliberately; private state and
mutation authority remain isolated. Provide separately advertised peer endpoints
for distinct Lightning identities.

Completion gates:

- Requests cannot change node, owner, signer, network or key purpose across an
  authorization boundary. A mismatch fails before signing or persistence.
- Deliberate collisions of request IDs, payment hashes, wallet indexes and
  node-local identifiers across two contexts do not alias state or receipts.
- Existing single-node mode runs through an explicit default context with the
  same supported behavior and recovery guarantees.

## 4. Prove two isolated hosted nodes with separate signers

Run two logical node instances under one hosting supervisor, initially with
separate daemon processes/service compositions. Give each a distinct owner,
Lightning identity, wallet, private database, credential and native signer
process. Both may use the same Bitcoin Core. No shared in-process runtime is
required for this acceptance proof.

Each node must independently open and operate channels with an external peer,
send and receive payments, forward a payment, restart and reestablish, close
cooperatively, and force close with the appropriate confirmed output recovery.
Use separate channels where mutually exclusive close paths require them.

Completion gates:

- Both identities operate concurrently; their public keys, funding ownership,
  wallet outputs and recovery histories remain distinct.
- Node A's credentials and requests cannot access Node B's state or signing
  authority, including deliberately relabeled and replayed requests.
- Killing or disconnecting signer A blocks A safely while B continues paying and
  receiving. Restarting A reconciles its original operations without affecting B.
- Writer failover and stale-state tests retain the milestone 2 guarantees in the
  hosted configuration. There is no cross-node or local-signer fallback.
- Actual peer, settlement, signature and Bitcoin confirmation assertions pass;
  process startup or mocked RPC success is insufficient evidence.

## Final acceptance

All four milestone gates pass on the same reviewed implementation. Record the
exact build, inventory coverage, adversarial/crash proofs and two-node live
results. Keep any unresolved gaps visible; do not label the complete goal done
based on a subset of the signer RPC surface or a successful direct payment.
