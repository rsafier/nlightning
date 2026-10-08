# Native signer authority boundary

This increment supplies transactional authority and wallet-validation foundations
for milestone 2. It does not complete independent channel validation or the goal's
live hosted-node acceptance gates.

## Authoritative persistence and publication integration

Signer transaction fencing does not fence node database writes or network
publication. The remaining strong deployment path requires database-enforced
writer authority in the same PostgreSQL database as each node's private state.
Authenticated worker principals must be bound to an installed writer epoch;
caller-selected session variables cannot confer authority. Database enforcement
must cover all mutable tables, including bulk repository and accounting writes,
and hold the ownership lock through the actual state transaction commit.

A trusted publication daemon must own peer sockets and Bitcoin submission
credentials. Workers must be unable to bypass that network boundary. Publication
requests identify exact durable intents committed with the associated node
transition; the daemon reads those intents itself. All transaction, package and
raw-package submissions require this boundary. Peer-send refusals must propagate
as failed outcomes rather than being swallowed by connection-error handling.

Ownership transfer must stop admission, classify in-flight submissions and close
old peer sessions before installing the next epoch and reopening admission.
Unknown transfer or publication outcomes stop admission until reconciled.
Initially one trusted publication instance avoids an unfenced predecessor holding
live sockets. Submitted bytes can arrive later, and previously released Bitcoin
signatures cannot be revoked. These are integration requirements, not implemented
guarantees of the current prototype.

## Trusted administration and storage

`NativeSignerAuthority` accepts a signer-installed connection factory. Node
credentials must have no database access. Enrollment, exact intent approval and
writer transfer belong to a separately authenticated owner administration plane;
none may be exposed through the node signing RPC.

Production authority storage must sit outside the node host's rollback and
administrative domain. Local SQLite is useful for transactional tests, but cannot
establish resistance to a compromised host restoring all local files. Database
transport authentication, credential separation, durable commit and backup
freshness are deployment requirements. The signer fails closed when this
independent authority is unavailable.

Enrollment binds node, owner, signer, Bitcoin network and public identity.
Approvals bind that enrollment to an immutable request ID, operation and exact
payload, with an expiry. Changing a destination, amount, fee or embedded snapshot
changes the approval fingerprint. Node credentials cannot approve an operation.

## Writer and recovery ordering

A supervisor acquires the next writer epoch through compare-and-swap. Epoch and
writer identity are checked before receipt lookup. Authority-enabled RPCs also
require a signer-installed credential bound to the exact writer and epoch; the
node token and caller-selected writer headers alone cannot acquire authority. Execution obtains a database
write fence before private-key use and retains it through authority receipt
commit. Ownership transfer is ordered before or after that transaction, so an old
writer cannot commit a signing result after a successful transfer.

Request identity is independent of execution epoch. A new authorized writer can
recover the original completed request; changing its payload cannot replay it.
Node-local request identifiers are scoped by immutable enrollment.

The independent authority records both its receipt checkpoint and an aggregate
digest of the signer's actual durable safety stores, bound to enrollment and
unique source names. This includes the main journal, swap session history and
nonce allocation/consumption history. A signer with older or different journal history
cannot start a new operation. This must be wired to signer-owned journal bytes,
never to a node-supplied checkpoint or claim that a transition was saved.

The local journal and authority database are distinct resources. If signing
commits locally but the authority commit fails, the signer must enter a sticky
fault and reconcile the original local receipt before further operations. `NativeAuthorizedSignerExecutor` enters a sticky fault when local history
changes before a failed authority transaction. Reconstruction still requires
explicit reconciliation of the original operation; the primitive does not reset
history or implement publication fencing. Those integration gates remain open.

Receipt replay checks current execution authority, local invalidation and exact
local/authority response bytes before committing the replay transaction or
returning a cached result. Retirement or data loss cannot be bypassed by a second
receipt cache. Reconciliation reads run under the same writer fence without
creating spending permission. Transaction commit ordering does not prevent a
network reply delayed until after transfer; publication needs its own current
writer check at its authoritative boundary.

## Independent wallet validation

`NativeWalletTransactionValidator` requires an authenticated, independently
configured chain-evidence source and signer-derived ownership/change checks.
It verifies fresh evidence, exact input outpoints, unspent status, ownership,
input amounts, approved output scripts/amounts, signer-owned change, duplicate
inputs and the resulting fee limit before any key operation. Caller wallet
snapshots do not establish funding authority.

Exact request approval does not substitute for this validation. A deployment
must supply the authenticated evidence adapter and verify its freshness against
the expected Bitcoin network. The initial tests use a controlled evidence source
to exercise rejection paths; they do not constitute a live Bitcoin proof.

### Typed reserved-wallet authorization

`ApproveWalletIntent` installs the exact operation-29 request, its independently
approved reservation, input derivations, destinations and fee limit. Typed
metadata, unique input claims and the exact approval commit in one authority
transaction. A generic exact-payload approval alone cannot authorize this wallet
purpose. Claims remain retained after completion; no claim-reset API exists.

`NativeWalletSignerRequestValidator` rejects other operations. It validates the
complete unsigned transaction and snapshot before wallet mutation or key use:
input and reservation sets, account-zero P2WPKH/P2TR metadata, signer-derived
scripts, independently verified amounts, outputs and fees. Snapshot-supplied
derivation overrides, silent-payment keys, channel locks, prior spending and
external inputs are rejected by this initial purpose validator. The node cannot
install its own ownership scripts or replace the evidence source. Approval expiry
is checked both before and after independent evidence validation, before key use.

Completed replay rechecks immutable approval and snapshot metadata, then matches
the local and authority receipts byte for byte. It returns the original response
without another private-key operation. Spent inputs or approval expiry do not
invalidate an already completed response. An invalidated, missing or different
local receipt blocks replay. During fenced execution, immutable approval reads
reuse the active authority connection and transaction, including SQLite shared
cache. Synchronous read scopes unwind after success or failure; external approval
reads use their own connection.

### Authenticated Bitcoin Core evidence

`AuthenticatedNativeCoreChainEvidence` fixes enrollment, network/genesis,
endpoint, authentication and freshness limits through signer administration.
HTTPS uses certificate validation and disables redirects. Plain HTTP is limited
to loopback or an explicitly trusted administrative transport. Requests and
responses are bounded; RPC errors do not expose credentials.

The adapter rejects wrong network/genesis, initial synchronization, header lag,
old/future tips, malformed output evidence and immature coinbase inputs.
`gettxout` includes mempool spending, and amounts must be exact integer satoshis.
A validation batch captures one tip and rejects changes or expiry before key use.
Ownership labels come from the installed signer-derived script registry.

This boundary trusts the independently administered Core instance, authenticated
transport and signer clock. It is not a separate consensus verifier or a durable
chain rollback watermark. The adapter and typed wallet validator are library
foundations; production executable composition remains an open gate.

## Remaining milestone 2 gates

Independent channel reconstruction must still validate funding, exact
millisatoshi balances, fee changes, commitment outputs, holder signatures,
revocation eligibility and nonce use. Protocol forwarding and protective
on-chain actions need explicit bounded owner standing policies. Generic node-key
hash signing is not an independent authorization boundary. These requirements
must remain visible until implemented and tested through the actual signer RPC.

## Bootstrap foundation

`SignerAuthorityBootstrap` reads a private administrator-owned configuration and
requires preinstalled enrollment, writer history, independent evidence and a
validator before creating an executor. It cannot enroll, approve spending or
transfer writers through node RPC credentials. Tests cover missing authority,
mismatched owner/history, stale writer and unavailable evidence. Production
executable composition and divergent-history reconciliation remain open; this primitive
alone is not an enabled deployment mode.

Normal bootstrap now loads the independent authority's current checkpoints under
the installed writer fence and requires the local aggregate digest to match.
An old configuration snapshot cannot reset receipt history. Allocation journals
and encrypted key files now expose committed digest sources; production must
include the appropriate source in its aggregate checkpoint composition.

## Persistence and publication integration still required

The signer fence does not currently cover node unit-of-work commits or final
peer and Bitcoin sends. Acceptance must exercise `TransportService` writes and
`BitcoinChainService` transaction/package submissions, including direct sends
and monitor rebroadcasts. Queue admission alone leaves a gap before submission.

The writer epoch must be enforced in the authoritative transaction committing
node state, receipt consumption and publication intent. An external authority
transaction wrapped around a separate local database commit can lose its fence
before that local commit finishes. A trusted persistence adapter must enforce
ownership transfer against those mutations in the same authority domain.

Managed egress must serialize transfer with actual submission and quiesce the
former writer before transfer completes. Already submitted bytes can arrive
later. Writer epochs cannot revoke a valid Bitcoin signature already released
to a host; independent signing validation must establish that the signature was
safe when issued. These boundaries remain open acceptance gates.
