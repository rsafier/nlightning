# Experimental VLS gateway

A runnable **local regtest signer prototype**, separate from the NLightning node.
It uses pinned VLS `cb8a64c71d3b214951e752281f05b9090e77f074`, stock Native
(CLN) derivation, real `SimpleValidatorFactory` with balance enforcement, and
durable transactional Redb persistence. FAFO starts with fresh wallets and
channels; importing prior NLightning keys/channels is out of scope.

This is newline-delimited JSON over private Unix sockets, **not gRPC, a vlsd wire
proxy, or `Signing:Mode=Vls`**. The NLightning semantic adapter remains to be built.
No Nitro/vsock implementation is included.

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
| Node | `identity`, `allocate`, `setup`, `point`, `sign_remote`, `validate_holder`, `activate`, `revoke_holder`, `validate_revocation`, `force_close`, `mutual_close`, `reconcile` for these commands |
| Approval | `authorize_keysend`, `reconcile` for keysend approval |

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
No permissive approver or arbitrary-message tunnel exists. Only explicit
keysend admission authorization is provided here; invoice authorization,
payee validation and trusted user-facing approval flows are still required.

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

A matching committed receipt returns `status=completed` plus its result. An
absent receipt returns `status=not_found`; the transaction containing both policy
state and receipt did not commit. Reconciliation does not execute the operation
or allocate a receipt. Receipt IDs are immutable and never evicted. Limits are
65,536 receipts and 64 MiB of receipt values, reserving 4 MiB before dispatch;
capacity exhaustion fails closed. No safe compaction/maintenance is implemented.

This prototype establishes local crash persistence, not rollback protection,
cloned-store fencing or an adversarial operating-system boundary. No chain
monitoring, SPV/oracle proof or mainnet behavior is established. Ordinary invoices,
ECDH/node message signing, wallet funding, HTLC claims/sweeps/penalties, forwarding,
cooperative-close validation vectors and holder-revocation transport vectors are
remaining integration work. Some allowlisted operations are not yet exercised
by the process test. No native-signer fallback should be used for VLS channels.
