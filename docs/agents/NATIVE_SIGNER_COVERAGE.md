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
| Opening and funding | Channel allocation, registration and funding RPCs | Original key-allocation intent/receipt and frozen acceptance context; funded inbound intent/channel capture, startup recovery and exact retained replies are implemented; exact funding request recovery | Prior four funding process-kill cases pass; new inbound source checks pass; current live acceptance and outbound durable reservations/retransmission and remaining negotiations stay open |
| Reconnect and taproot commitments | Verification nonce and partial-signature RPCs | Captured reconnect workflows and durable consumed-nonce outcomes | Complete creation/restart lifecycle and independent transition validation |
| Cooperative and force close | Close nonce/signature and broadcast RPCs | Consumed-nonce/broadcast guards; native live peer proof exists | Complete node close workflows and crash/retransmission proofs |
| Dual funding and splicing | Funding registration, shared-input signing, funding-lock RPCs | Native signing guards | Complete application request recovery for all negotiation/RBF/funding boundaries |
| On-chain claims and sweeps | Sweep, anchor and wallet-input RPCs | Initial delayed and replacement sweeps capture exact prerequisites/receipts; restored child watches and reorg retirement; native restart and script verification | Other claim/anchor lifecycles; initial-sweep node-crash proofs, independent evidence and fee/destination authorization |
| Wallet accounts and withdrawals | Public account metadata, wallet signing and wallet messages | Named-account checks; withdrawal recovery has prior seven live cases and 71 wallet checks; captured send-outputs PSBT publication/recovery is implemented | PSBT source checks pass; validate current live cases; standalone finalization/publication, bump/reorg lifecycles and wider input/destination authorization remain open |
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

## Historical acceptance evidence before the current increment

Before the PSBT, funded inbound and restricted authority changes below, the combined net10 Release solution built with zero warnings and errors.
The full solution formatting verification and 45-project configuration check pass.
Six replacement-sweep scheduler recovery and retirement cases also pass.
Those focused checks passed: 71 wallet cases, 54 daemon enrollment/context/hosting
cases and 22 compiled-model cases across the three generated providers.
That verified native RPC regression passed all 325 cases without skips in a
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

These historical proofs used prototype authority. They do not validate the new
restricted executable profile or establish full-node independent authorization
and stale-writer publication fencing.

## Native lifecycle and context increment

Saved v1 allocation requests retain their original key index and exact public
receipt across restart. Invalid requests fail before allocation. Inbound retries
reuse the saved decision, negotiated features, opening defaults and fee quote;
changed live policy does not silently produce a different acceptance. Native
SegWit and taproot production-handler exchanges both consume allocation and
initial-signing receipts with the channel save. The current funded inbound
increment saves the complete negotiated channel and immutable funding-created
message before registration/signing, including original key index, features,
effective parameters, creation height and peer verification nonce. Startup
replays the original requests, then consumes allocation and signing receipts
with the final channel and funding watch before returning funding-signed.
Duplicates read and reconcile consumed receipts; changed input/peer, missing
receipts and channels beyond the funding-negotiation state are refused. New
SegWit/taproot save-failure and signer-restart source checks pass below; current
live funded-opening acceptance remains pending. Funded outbound recovery, durable pre-sign input reservations and
reconnect retransmission until the peer acknowledgment remain open.

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
[the authority boundary](NATIVE_SIGNER_AUTHORITY.md). The current executable
increment composes them in the restricted profile described below; this is not
full-node independent authorization. Wider purpose coverage and writer-failover
and publication-fencing gates remain open.

## Historical verified increment record

The prior increment's final net10 Release solution build passed with zero warnings and errors.
Full solution formatting passes with the normal Blazor-project exclusion; the
final test-only allocation deadline adjustment also passes its whitespace check.
The clean native regression runs 325 cases, the node workflow suite runs 21,
and the live withdrawal suite runs seven, all without failures or skips. All
four hosted-node live proofs pass again on the same runner image, including
confirmed delayed sweeps through the new initial-sweep workflow.

Runner image: `nltg-spike-runner:native-hosted`, configuration digest
`b95b8673e6f95f92252a3ef39ab80d15282c065cf7aad198f4494521235d2b09`.
It was staged from the verified Release binaries without rebuilding inside the
image. This image and its 325-case native run precede the current implementation
increment; they do not certify its new source or acceptance tests. Wider opening,
wallet/claim lifecycles, purpose coverage and independent writer/publication
enforcement remain delivery gates.


## Current implementation and acceptance scope

Captured native send-outputs now saves a PSBT publication intent before wallet
signing: funded PSBT, exact unsigned transaction, reservation IDs, fee/rate,
height and label. Recovery reuses original signing envelopes and receipts and
validates the exact inputs, outputs, fee and signed transaction before saving
the consumed workflow with its broadcast row. Publication occurs after that
save. Startup recovery precedes withdrawal reservation cleanup; lease/release
and competing-publication checks preserve inputs held by an unfinished or
persisted publication decision. Indeterminate or missing receipts block recovery
rather than select new inputs or issue replacement signing requests. This
boundary covers captured send-outputs publication, not every separately exposed
PSBT finalization/publication operation. Regression checks and the 12-case
real PSBT process-kill/receipt-rejection proof pass as recorded below.

The executable accepts an explicit administrator-installed
`--authority-config` profile with mode `NativeWalletAuthorityV1`. It binds node,
owner, signer, network and node key to a pre-enrolled PostgreSQL authority,
installed writer credential, bounded wallet derivations and authenticated Core
evidence. PostgreSQL requires verified TLS and bounded connection/command
timeouts; secrets and the installed profile use separate private files.
Runtime authority/evidence activation occurs before the listener, and a
persistent profile marker prevents omission or replacement on restart.

This profile authorizes only reserved account-zero wallet withdrawal operation 29
(`SignWalletTransaction3`) through its installed validator. It does not enroll
owner authority, approve its own withdrawals, authorize PSBT operation 28 or
provide a usable independently authorized whole-node signer. Other financial
and protocol signing purposes remain rejected or outside this profile. The executable
PostgreSQL/Core proof below establishes restricted withdrawal authorization
and signer writer rotation. Whole-node authority and fencing of node writes
and publication remain open.
The earlier prototype-native proofs remain separate evidence.

## Verified current source checks

The integrated net10 Release solution build passes with zero warnings and
errors. The net11 daemon and native signer builds also pass with zero warnings
and errors. The focused wallet suite passes all 150 cases, the node workflow
suite passes 21, PSBT recovery passes 13, funded inbound recovery passes ten,
and corrected authority composition checks pass 13. The broad native RPC
regression passes all 361 cases (`native-psbt-full-rpc-tests.log`). All these
suites pass without failures or skips.

The real PSBT SIGKILL/process-restart and receipt-rejection proof passes all
12 cases without failures or skips (`native-psbt-live-kill-tests.log`). This
adds live acceptance evidence for captured PSBT publication alongside the
source checks for saved funded-opening inputs and exact native reply recovery.

The actual restricted signer executable passes both P2WPKH and P2TR cases
against verified-TLS PostgreSQL and authenticated Bitcoin Core
(`native-psbt-live-authority-tests6.log`): node-only credentials, altered intent,
unsupported purpose, Core outage and stale writers leave safety history unchanged.
Externally authorized writer rotation survives signer restart; exact replay
retains history, and Core confirms the exact signed transaction bytes.
The restored Core endpoint must pass the same independent freshness checks and
retain the original chain tip and UTXO before rotation proceeds.

The seven existing withdrawal killpoint/receipt-rejection cases also pass on
the final runner image (`native-psbt-final-withdrawal-live.log`). The hosted
channel/payment/restart/cooperative-close proof and the separate daemon-process
IPC/wallet/signer-isolation proof pass without failures or skips
(`native-psbt-final-hosted-nodes-live.log`,
`native-psbt-final-hosted-daemons-live.log`). The two-node force-close and
confirmed delayed-sweep proof also passes without failures or skips
(`native-psbt-final-hosted-onchain-live.log`). At the owner's request this
checkpoint is committed for transfer to a faster machine. The final hosted
forwarding rerun and full solution formatting verification remain pending;
resume with [the checkpoint handoff](NATIVE_SIGNER_HANDOFF_2026_10_08.md). These
checks do not establish the complete independent authority deployment. Funded outbound recovery, other
signing lifecycles and independent writer/publication enforcement remain open.