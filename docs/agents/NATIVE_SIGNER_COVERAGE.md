# Native remote signer coverage inventory

Delivery scope: [the four-milestone goal](NATIVE_SIGNER_HOSTED_NODES_GOAL.md).
Status: implementation and acceptance work in progress. Native process and live proofs have executed; remaining lifecycle and authority gates are open.

The native RPC surface forwards the current Lightning signer methods, but not
every subsystem has a safe remote key-operation boundary, an independent policy
validator, or an application lifecycle recovery proof. Keep these claims separate.

| Subsystem | Current remote boundary | Durable state / existing proof | Required work |
| --- | --- | --- | --- |
| Node ECDH and transport/onion use | `ComputeNodeSharedSecret`; node private key export refused | Actual signer process ECDH and identity tests | Bind caller, identity and permitted purpose; full subsystem regression |
| Normal channel commitments and revocations | Typed native signer dispatch | Node workflows, exact requests/receipts, signer journal; actual node crash tests | Independent exact commitment/balance validation and writer fencing |
| Opening and funding | Channel allocation, registration and funding RPCs | Original key-allocation intent/receipt and frozen inbound acceptance context; native SegWit/taproot opening proof; funded-initial-signing guard; exact funding request recovery | Four funding process-kill boundaries pass; complete funded-opening restart/retransmission and remaining negotiation workflows |
| Reconnect and taproot commitments | Verification nonce and partial-signature RPCs | Captured reconnect workflows and durable consumed-nonce outcomes | Complete creation/restart lifecycle and independent transition validation |
| Cooperative and force close | Close nonce/signature and broadcast RPCs | Consumed-nonce/broadcast guards; native live peer proof exists | Complete node close workflows and crash/retransmission proofs |
| Dual funding and splicing | Funding registration, shared-input signing, funding-lock RPCs | Native signing guards | Complete application request recovery for all negotiation/RBF/funding boundaries |
| On-chain claims and sweeps | Sweep, anchor and wallet-input RPCs | Initial delayed and replacement sweeps capture exact prerequisites/receipts; restored child watches and reorg retirement; native restart and script verification | Other claim/anchor lifecycles; initial-sweep node-crash proofs, independent evidence and fee/destination authorization |
| Wallet accounts and withdrawals | Public account metadata, wallet signing and wallet messages | Named-account reserved-spend checks; native withdrawal intent/request/receipt recovery and atomic broadcast persistence; seven live process-kill/rejection cases and 71 wallet tests pass | PSBT and bump/reorg recovery; production input/destination authorization |
| Silent payments | Output construction/spend operations plus typed public receiver metadata, label and scan ECDH operations | Real-process remote receive and restart tests pass | Complete scan persistence/reorg and spend recovery |
| Swap/keyring metadata | `GetKeyRingPublicKey` (operation 110); public records saved before issuance | `RemoteKeyRingPublicTests`: actual RPC, invalid locators, restart and save failure; `LoopPersistenceTests`: durable public records | Signer-side family/owner authorization and independent key-purpose enrollment |
| Swap signing and MuSig sessions | Typed remote `ISwapSigner`; private ring keys stay inside the daemon | Encrypted persistent MuSig sessions; actual-process restart, consumed-nonce and exact receipt tests pass | Independently authorize swap destinations and purposes; complete application lifecycle proof |
| BOLT 11/12, gossip, payment-related signatures | Invoice, offer, gossip and node signature RPCs | Actual native invoice/signature and auxiliary surface tests | Purpose-bound authorization; remove generic signing bypasses from untrusted node credentials |
| Backups, peer storage and offer identifiers | Purpose-scoped seal/open and identifier RPCs | Authenticated encryption round-trip/refusal tests | Owner/context binding and complete subsystem recovery proof |
| Accounting and administration | Native node/message signing operations | Existing application callers | Explicit authorized statement types and roles; no arbitrary financial-signing bypass |

All operations ultimately require node/owner/signer context binding. Financial
operations require owner intent or bounded standing policy. Protocol operations
require independently validated signer transitions, with scoped authorization.

## First coverage increment

Public keyring allocation no longer calls a private derivation API to obtain
metadata. The node's `KeyRingService` uses `ISecureKeyManager.GetKeyRingPublicKey`;
the native remote implementation sends family/index to the authenticated signer
and receives only a compressed public key. Private keyring export still throws.
Reserved families and negative indexes are rejected inside the actual signer.
Public derivation is deterministic and does not allocate signer safety state;
the node's append-only public key record is saved before it is returned.

The swap and receiver increments extend this boundary through typed operations.
Their actual RPC and process-restart tests now pass. Independent owner policy
and the remaining lifecycle gates are still open. Failed or skipped acceptance
cases remain visible below.

## Validated foundation checks

The first focused checks passed: five immutable-context cases, two contextual
payment-event cases, 57 native close/splice/gossip/silent-payment crypto cases,
and 20 local swap-signing cases including the new journal recovery checks.
These do not replace the remote process, crash, live-chain or hosted-node gates.

## Current acceptance evidence

The combined net10 Release solution builds with zero warnings and errors.
The full solution formatting verification and 45-project configuration check pass.
Six replacement-sweep scheduler recovery and retirement cases also pass.
Current focused checks pass: 71 wallet cases, 54 daemon enrollment/context/hosting
cases and 22 compiled-model cases across the three generated providers.
The expanded native RPC regression passes all 325 cases without skips in a
sequential run without competing acceptance jobs. This includes allocation
checkpoint rollback, authority restart loading, writer credentials, receiver and
swap RPCs, obsolete-sweep retirement and swap timer/shutdown checks. The current
run also covers saved opening allocations, frozen inbound decisions, initial
delayed sweeps, persisted context collisions, Core evidence and typed wallet
authorization. Its 45 wallet-authority cases include shared-cache execution,
exact replay, receipt mismatch rollback and expiry during evidence validation.

The complete native node workflow suite passes all 21 cases without skips.
Twelve kill actual node processes at signing/recovery boundaries, including
all four funding boundaries and incoming-before-commit. Nine cases cover
indeterminate outcomes and rejection of altered or lost recovery history.
Original request IDs, envelopes, receipts and subsequent convergence are checked.

Live regtest proofs pass for two independent nodes sending/receiving fractional
payments and closing cooperatively, both nodes force-closing and recovering
confirmed delayed outputs, and LND forwarding 10,000,123 millisatoshis through
both native nodes with exact persisted balances and circuits. The independent
daemon-process wallet/isolation proof also passes on the verified runner image:
authenticated signer readiness, separate IPC credentials and wallets, A outage
with B continuing to sign, and both daemon identities retained after restart.
All four live proofs execute without skips. These are prototype-native proofs;
independent authority composition and hosted writer failover remain open.

These proofs do not establish production independent authorization or stale
writer publication fencing. The executable still uses prototype signing
authority until the independent authority is fully composed and validated.

## Native lifecycle and context increment

Saved v1 allocation requests retain their original key index and exact public
receipt across restart. Invalid requests fail before allocation. Inbound retries
reuse the saved decision, negotiated features, opening defaults and fee quote;
changed live policy does not silently produce a different acceptance. Native
SegWit and taproot production-handler exchanges both consume allocation and
initial-signing receipts with the first channel save. An interrupted funded
opening still blocks safely rather than rebuilding funding; automatic recovery
and exact reply retransmission remain open.

Initial delayed sweeps retain the unsigned transaction, witness prerequisites,
output/close snapshot and confirmed HTLC-parent identity. Recovery restores the
child spend watch and consumes its original receipt with the broadcast/output
decision. Changed close or HTLC-parent confirmation retires the obsolete intent
without another signature. Real signer restart checks verify SegWit/taproot
witnesses and byte-identical signed transactions. The application recovery and
executor checks pass 48 cases; 67 affected resolver/scheduler regressions pass.

Service composition compares the full immutable context before registration.
The persisted collision proof uses two enrolled databases and signer processes,
then compares every private table's raw values across A-only mutation and both
restarts. LND credential storage is enrolled and effective macaroon keys are
context-derived, including secondary roots. Hosted preflight rejects overlapping
administrative resources, shared credential authorities and authentication
bypasses across owners. Actual TLS/interceptor checks reject opposite-owner
credentials and retain enrollment through restart/rotation. Current checks pass
136 LND cases, 46 hosted-admin/Cashu cases, one two-listener hold-backend case and
192 single-node configuration/composition/IPC cases, without skips. Two wallet
factory cases confirm that native recovery runs before reservation cleanup and
that local mode still runs without a coordinator.

The live withdrawal proof passes all five process-kill boundaries: saved intent,
prepared request, signer completion before reply, saved receipt and consumed
transition/broadcast before publication. Recovery retains original reservations,
envelopes and receipts; Core accepts the byte-identical transaction, repeated
submission converges, and confirmation followed by node restart releases its
reservation. The two rejection cases retain inputs and publish nothing: an
explicitly injected indeterminate outcome and a genuinely removed signer
receipt. All seven cases execute without skips using real signer/Core/SQLite
processes. The test node uses production services and mirrors daemon startup;
it does not launch the daemon executable for these withdrawal cases.

The typed wallet-purpose validator and authenticated Core evidence adapter are
independent authorization foundations described in
[the authority boundary](NATIVE_SIGNER_AUTHORITY.md). They are not wired as a
production signing mode. The remaining inventory and writer-failover gates stay
open.

## Verified increment record

The final net10 Release solution build passes with zero warnings and errors.
Full solution formatting passes with the normal Blazor-project exclusion; the
final test-only allocation deadline adjustment also passes its whitespace check.
The clean native regression runs 325 cases, the node workflow suite runs 21,
and the live withdrawal suite runs seven, all without failures or skips. All
four hosted-node live proofs pass again on the same runner image, including
confirmed delayed sweeps through the new initial-sweep workflow.

Runner image: `nltg-spike-runner:native-hosted`, configuration digest
`b95b8673e6f95f92252a3ef39ab80d15282c065cf7aad198f4494521235d2b09`.
It was staged from the verified Release binaries without rebuilding inside the
image. These results retain the prototype authority limitation above. Funded
opening replay, PSBT/remaining claim lifecycles, production purpose coverage and
independent writer/publication enforcement remain delivery gates.
