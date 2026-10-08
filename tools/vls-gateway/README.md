# Experimental VLS gateway

A runnable **local regtest signer prototype** used by the NLightning VLS adapter.
It uses pinned VLS `cb8a64c71d3b214951e752281f05b9090e77f074`, stock Native
(CLN) derivation, real `SimpleValidatorFactory` with balance enforcement, and
durable transactional Redb persistence. FAFO starts with fresh wallets and
channels; importing prior NLightning keys/channels is out of scope.

The `NLightning.Infrastructure.VlsSigning` adapter uses newline-delimited JSON over
private Unix sockets. This gateway is not a vlsd wire proxy. No Nitro/vsock
implementation is included. `Signing:Mode=Vls` selects the fresh-key regtest
prototype; unsupported cryptographic and channel operations fail explicitly.
See [the node MVP runbook](../../docs/agents/VLS_NODE_MVP.md) for configuration
and the separately recorded node/peer acceptance scope.

## Build and prove recovery

Install Rust through rustup, then:

```bash
rustup toolchain install 1.94.0 --profile minimal
./tools/vls-gateway/run.sh test
```

The runner caches the canonical VLS checkout, verifies its revision and tracked
cleanliness, and stages this crate beside it. Every build/test uses the committed
Cargo.lock and `--locked`. An existing pinned checkout can be reused:

```bash
mkdir -p /tmp/nltg-vls-gateway
ln -s /absolute/path/to/pinned/vls /tmp/nltg-vls-gateway/source
VLS_GATEWAY_CACHE=/tmp/nltg-vls-gateway ./tools/vls-gateway/run.sh test
```

The process test starts actual daemon children and exercises:

- Correct authentication, denial of node-side payment authorization, malformed
  request handling, and refusal of a competing writer on the same database.
- Channel allocation/setup, independently peer-signed holder commitment
  validation, activation, and independently verified remote funding signature.
- An accepted remote-signing request whose original client reply is deliberately
  **unread**. A separate authenticated reconciliation confirms its durable receipt;
  the child is then killed. Restart recovers/replays the exact response. This
  proves application-level response loss, not a write-blocked transport or a kill
  inside Redb's fsync implementation.
- Rejection of changed same-ID payloads and skipped commitments. The latter leaves
  the durable Redb file byte-for-byte unchanged.
- Rejection of an incorrect peer revocation secret, valid peer revocation, and
  restored revocation progression after another process restart.
- Rejection of an unauthorized outgoing HTLC, separate credentialed keysend
  approval, persisted approval across restart, and admission with one HTLC
  signature. It does not prove settlement/preimages or independently verify that
  HTLC signature.
- Independently verified holder force-close funding signature and refusal of a
  different injected seed against the existing store.
- Durable broadcast/data-loss invalidation before receipt replay and reconciliation,
  mixed-case channel-ID alias rejection, data-loss persistence across process restart,
  and malformed upstream setup-txid rejection without terminating the owner.

Tests use deterministic fixture seeds/peer keys and an artificial funding
outpoint. No live Bitcoin chain, peer or NLightning node is involved.

## Start the daemon

Create an operator-owned mode-0700 directory and distinct random ASCII
credentials. Keep the approval credential outside the node's configuration.
For example, provision credentials locally:

```bash
install -d -m 700 /private/vls-gateway
python3 - <<'PY'
import os, secrets
for name in ('node-token', 'approval-token'):
    fd = os.open('/private/vls-gateway/' + name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'wb') as f:
        f.write(secrets.token_hex(32).encode('ascii'))
PY
```

Supply **exactly 32 raw seed bytes** on stdin and close the pipe. The same seed
must be supplied again on every restart. Its external custody is the caller's
responsibility; this daemon does not create/store a seed file. An externally
managed seed file is one prototype provisioning option:

```bash
cat /secure/operator-managed-seed.bin | ./tools/vls-gateway/run.sh \
  /private/vls-gateway /private/vls-gateway/node-token \
  /private/vls-gateway/approval-token regtest
```

The seed injection buffer is zeroized after initialization; VLS necessarily
retains its signing keys in memory. Only regtest is accepted. The configuration
binds the injected seed's node identity, network, derivation and policy version
to the store. Mismatches fail startup. Redb acquires its database lock before
stale socket removal, preventing a competing daemon from removing the owner's
sockets. `node.sock` and `approval.sock` have mode 0600.

## Request contract

One request and one reply per connection, with a trailing newline. Frames are
limited to 1 MiB and reads/writes have five-second deadlines. Dispatch is
serialized. Example request, using the exact credential file contents as token:

```json
{"token":"YOUR_NODE_TOKEN","id":"identity-1","command":{"op":"identity"}}
```

Replies are `{"ok":true,"result":...}` or
`{"ok":false,"error":"..."}`. No request body, credential or seed is logged.
Malformed/unsupported requests fail closed. This prototype uses textual errors;
a stable typed error/status contract is a remaining adapter requirement.

The allowlisted commands and fields are defined by `Command` in `main.rs`:

| Socket | Commands |
| --- | --- |
| Node | `identity`, `ecdh`, `sign_invoice`, `public_account`, `wallet_public_key`, `wallet_sign`, `allocate`, `basepoints`, `setup`, `point`, `sign_remote`, `validate_holder`, `activate`, `revoke_holder`, `validate_revocation`, `payment_preimages`, `sign_channel_update`, `sign_node_announcement`, `force_close`, `mutual_close`, `mark_data_loss`, `broadcast_status`, `verify_broadcast_mark`, `reconcile` for these commands |
| Anchors | `sign_holder_anchor` (our anchor input), `wallet_sign_fee_inputs` (P2WPKH wallet inputs beside foreign inputs with path `m`) |
| Approval | `authorize_keysend`, `authorize_invoice`, `reconcile` for approval |

Channel IDs are 41 decoded bytes: compressed peer key plus little-endian dbid,
represented as hex. Positive dbids are allocated/managed by the caller and must
be persisted alongside BOLT channel IDs. Setup accepts only StaticRemoteKey and
AnchorsZeroFeeHtlc; it uses VLS's serde `ChannelSetup` representation. Commitments
use increasing numbers (do not reverse them). HTLC offered/received directions
follow the broadcaster of the transaction. For remote commitments, our outgoing
HTLC is `received`. Values are satoshis; authorization amounts are millisatoshis.
Signatures are 64-byte compact ECDSA hex. The adapter must apply correct funding
and per-channel-type HTLC sighash bytes/DER conversion.

`validate_holder` and `revoke_holder` are separate. The node must durably save
its commitment transition **before** asking to release a revocation secret.
No permissive approver or arbitrary-hash signing tunnel exists. The separate
approval socket accepts signed positive-amount regtest BOLT11 invoices through
VLS `add_invoice`, or explicit keysend authorization. The node credential cannot
authorize payments. The operator can use `tools/vls-approve/approve.py`; the node
never receives its approval credential. `payment_preimages` records channel-bound
settlement information and persists NodeState in the receipt transaction.

## Durable receipts and boundaries

Every successful command's typed, canonical serialized command digest and exact
JSON result are written in the **same Redb transaction** as VLS policy mutations.
The transaction commits before sending the response. The gateway does not mutate
through a plain per-key Redb persister. A policy failure must leave no buffered
mutations; otherwise the process fails immediately. Persistence/receipt failures
also fail-stop before returning success.

Persist the request ID and command in the caller **before dispatch**. Repeat the
same ID and command to get the exact stored result; changed payloads are refused.
For read-only reconciliation:

```json
{"token":"YOUR_NODE_TOKEN","id":"probe-1","command":{"op":"reconcile","id":"sign-1","command":{"op":"sign_remote","channel":"...","point":"...","number":1,"feerate":253,"holder_sat":2999000,"peer_sat":0,"offered":[],"received":[]}}}
```

A matching committed receipt returns `status=completed` plus its result. Sticky
data-loss or channel-close policy invalidates unsafe normal-operation receipts
before replay/reconciliation and returns `status=invalidated`. The same selected
force-close result can be replayed until data loss is marked. Hex identifiers are
canonicalized before every custom safety-metadata lookup. An
absent receipt returns `status=not_found`; the transaction containing both policy
state and receipt did not commit. Reconciliation does not execute the operation
or allocate a receipt. Receipt IDs are immutable and never evicted. Limits are
65,536 receipts and 64 MiB of receipt values, reserving 4 MiB before dispatch;
capacity exhaustion fails closed. No safe compaction/maintenance is implemented.

This prototype establishes local crash persistence, not rollback protection,
cloned-store fencing or an adversarial operating-system boundary. No chain
monitoring, SPV/oracle proof or mainnet behavior is established by the gateway
process proof. The adapter now supports VLS-owned node ECDH, BOLT11 invoice
signing/tracking, typed gossip signing, stock Native wallet public derivation,
and policy-checked P2WPKH funding witnesses. Native wallet children interleave
receive/change indexes as `2*index + change`, beneath the stock account `m/0/0`.
Shutdown and cooperative-close destinations must match a VLS wallet path.
The node VLS profile refuses dust HTLC exposure before commitment acceptance.
Pinned VLS phase2 consumes nondust outputs; supplying logical trimmed HTLCs
would violate its transaction policy, while omitting settled dust payments
causes a balance-policy shortfall. Balance enforcement stays enabled.
HTLC claims/sweeps/penalties, taproot, splicing, dual funding, auxiliary
node encryption and BOLT12 remain outside this adapter slice. Some allowlisted
operations are not exercised by the standalone process test; node/peer acceptance
results are documented separately. There is no native-signer fallback.
