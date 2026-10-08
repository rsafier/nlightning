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
| Opening and funding | Channel allocation, registration and funding RPCs | Key-index journal and signer guards; native funding workflow captures the original request and exact receipt | Four funding process-kill boundaries pass; complete allocation and remaining opening workflows |
| Reconnect and taproot commitments | Verification nonce and partial-signature RPCs | Captured reconnect workflows and durable consumed-nonce outcomes | Complete creation/restart lifecycle and independent transition validation |
| Cooperative and force close | Close nonce/signature and broadcast RPCs | Consumed-nonce/broadcast guards; native live peer proof exists | Complete node close workflows and crash/retransmission proofs |
| Dual funding and splicing | Funding registration, shared-input signing, funding-lock RPCs | Native signing guards | Complete application request recovery for all negotiation/RBF/funding boundaries |
| On-chain claims and sweeps | Sweep, anchor and wallet-input RPCs | Native guards and node broadcast machinery; replacement-sweep capture and obsolete-intent retirement pass native RPC and scheduler recovery checks | Initial resolver/anchor lifecycle; end-to-end signer workflow recovery, independent evidence and fee/destination authorization |
| Wallet accounts and withdrawals | Public account metadata, wallet signing and wallet messages | Named-account reserved-spend checks; native withdrawal intent/request/receipt recovery and atomic broadcast persistence; 71 wallet tests pass | Actual withdrawal process-kill/chain acceptance, PSBT and bump/reorg recovery; production input/destination authorization |
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
The expanded native RPC regression passes all 219 cases without skips in a
sequential run without competing acceptance jobs. This includes allocation
checkpoint rollback, authority restart loading, writer credentials, receiver and
swap RPCs, obsolete-sweep retirement and swap timer/shutdown checks.

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
