# Native signer checkpoint handoff — 2026-10-08

Status: checkpoint committed for transfer to a faster machine at the owner's
request. The delivery goal is still in progress; no milestone is complete.
Continue on `wip/remotesigner`. The previous checkpoint was
`68cb9798c83265afd8d508f9d80db5231fc70c5d`; the branch includes the critical
`wip/fafo` fixes at `dd598216`.

Read `CLAUDE.md`, the relevant scoped instructions, the
[four-milestone goal](NATIVE_SIGNER_HOSTED_NODES_GOAL.md),
[coverage inventory](NATIVE_SIGNER_COVERAGE.md),
[authority design](NATIVE_SIGNER_AUTHORITY.md), and issue entries NL-1310/NL-1311
before continuing. Native C# signer coverage is the implementation focus.
Prior-version compatibility/migration is not required; active-channel recovery
and safety are required. Public work stays within the four goal milestones.
The owner authorizes swarms, commits and branch pushes. No PR is requested.

## Implemented in this checkpoint

- Native `SendOutputs` PSBT publication: workflow kind 12 freezes the funded
  packet, transaction, wallet inputs, original reservations and fee terms before
  operation 28. Recovery retains the original envelopes and receipts. Witness
  validation, receipt consumption and durable broadcast save precede Core
  submission. Pending/blocked intents hold their inputs against lease expiry,
  release and competing signing/publication. Exact already-saved publication
  can be resubmitted.
- Funded inbound v1 opening: `NativeV1FundedInboundOpening` saves negotiated
  channel state, exact `funding_created`, nonce material and signing inputs
  before native registration/signing. Startup resumes before early-state repair.
  Receipt consumption, channel transition, push accounting and funding watch
  save atomically before `funding_signed`. Duplicate replies reconcile retained
  history; changed inputs and advanced/closed channels are refused.
- Executable restricted authority: administrator-installed
  `--authority-config` / `NativeWalletAuthorityV1` wires verified-TLS PostgreSQL,
  writer-bound credentials, installed wallet derivations and authenticated Core
  evidence before the signer listener opens. It supports **only operation 29**,
  reserved account-zero withdrawals. It never enrolls owner authority, approves
  spending or acquires a writer at startup/RPC. A persistent profile marker
  prevents downgrade/replacement while allowing externally authorized writer
  rotation. `--authority-manifest` exports public enrollment/checkpoint data
  without changing safety history or starting a listener.
- The live authority harness verifies history through a separate process so
  production exclusive history locks remain intact. Its namespaced runner Role
  adds only `get`/`patch` on `statefulsets/scale` for the real Core outage. After
  restart, the harness waits for the actual independent freshness adapter and
  checks that the original tip and funded UTXO survive before writer rotation.

No persistence schema, migration or generated-model changes in this checkpoint.

## Verified evidence

All successful test rows below executed with zero failures and zero skips.
Counts overlap: focused suites are also included in broader suites.

| Check | Result |
| --- | --- |
| Integrated net10 Release solution | Zero warnings/errors |
| Final integration test assembly rebuild | Zero warnings/errors |
| net11 daemon dependency graph and native signer | Zero warnings/errors |
| Solution configuration mappings | 45 projects pass |
| Wallet signing/spending | 150 cases |
| Broad native RPC regression | 361 cases |
| Native node workflow recovery | 21 cases, including 12 actual node-process kills |
| PSBT publication recovery | 13 focused cases |
| Funded inbound recovery | 10 cases across two channel formats |
| Restricted authority composition | 13 cases |
| Runner manifest/RBAC | 11 cases |
| Live PSBT process-kill/receipt rejection | 12 cases across P2WPKH/P2TR |
| Live restricted executable PostgreSQL/Core authority | Both P2WPKH/P2TR cases |
| Existing live withdrawal crash/receipt rejection | Seven cases |
| Two hosted nodes: payment/restart/cooperative close | One complete live proof |
| Separate hosted daemon processes: public API isolation | One complete live proof |
| Two hosted nodes: force close and confirmed delayed sweeps | One complete live proof |

The authority proof rejects node-only credentials, altered intent, unsupported
purpose, Core outage and stale writers without journal mutation. Externally
authorized rotation survives signer restart, completed replay retains exact
bytes/history, and Core confirms the exact signed transaction.

The inbound focused proof injects save failures and restarts the signer and the
actual startup channel manager; it is **not** an actual funded-inbound node-kill
and Core-settlement proof. The hosted proofs still use prototype authority;
they do not prove full independently authorized node operation or writer
failover/publication fencing.

Final live runner configuration ID:
`9b409bc177a6529683aca9026ed35944d09487f83120d4bea4db065906721e2e`.
Manifest:
`0e8a15467317376efa2530a5b388c80f2a889f950c9a45d630bceaffd705eaba`.
The PSBT 12-case proof used the earlier checkpoint image
`ee661d350aa4087b916433b092db916ab2f7971300f7feafe90c1deab7b8ef40`;
subsequent changes were authority harness/manifest fixes. All 22 production
assemblies in the final runner were checked byte-identical against the passing
361-case native regression assemblies.

Raw logs are local to the original machine under `/workspace/scratch/` and are
not committed. Relevant names: `native-psbt-full-rpc-tests.log`,
`native-psbt-node-workflow-tests.log`, `native-psbt-wallet-tests.log`,
`native-psbt-composition-tests2.log`, `native-psbt-live-kill-tests.log`,
`native-psbt-live-authority-tests6.log`, `native-psbt-final-withdrawal-live.log`,
`native-psbt-final-hosted-nodes-live.log`,
`native-psbt-final-hosted-daemons-live.log`, and
`native-psbt-final-hosted-onchain-live.log`.

Earlier authority attempts failed on a history-observer lock, missing scale
RBAC, restored Core readiness and disk exhaustion. The final two-case run passes
with the production locks and freshness checks intact. The final frozen-source
review reported no new concrete safety findings.

## First actions on the new machine

This is a transfer checkpoint, not final acceptance. Two checks were deliberately
left for the faster machine: **the final hosted forwarding rerun and the full
solution formatting verification**. Changed C# whitespace was formatted;
UTF-8/no-BOM/LF/no-final-newline and `git diff --check` pass. The final full
formatting gate has not run after the last changes. Do not infer that it passed.

1. Fetch/check out `wip/remotesigner` and inspect the checkpoint. Restore/build
   net10 Release with the repository SDK. Run the format/configuration gates.
2. Build/import a fresh integration runner image from that build. Run
   `HostedNativeNodesForwardingInClusterRunnerTests` explicitly; require its
   inner proof and wrapper to report no failures/skips. It forwards
   10,000,123 msat through both native nodes and checks persisted accounting.
3. Record results in the coverage inventory/issue ledger. Then begin the next
   bounded implementation slice below. Keep the complete goal open.

Typical commands after provisioning prerequisites:

```bash
dotnet build NLightning.sln -c Release -p:NltgTargetNet11=false -p:MSBuildWarningsAsMessages=MSB4121
dotnet format NLightning.sln --no-restore --verify-no-changes --exclude test/BlazorTests
python3 scripts/check-sln-configs.py
NLTG_RUNNER_PROJECT=integration NLTG_RUNNER_TAG=native-hosted test/NLightning.Testing.Cluster/Runner/image/build.sh Release
NLTG_RUNNER_IMAGE=nltg-spike-runner:native-hosted dotnet test/NLightning.Integration.Tests/bin/Release/net10.0/NLightning.Integration.Tests.dll -explicit only -class NLightning.Integration.Tests.Cluster.Live.HostedNativeNodesForwardingInClusterRunnerTests -parallel none -showLiveOutput -noColor
```

The original machine used activation scripts in `/workspace/nlightning-setup/`,
SDK 11 RC with net10 runtime, Docker and Kubernetes context `kind-nltg-cloud`.
Those scripts/images are machine-local; provision equivalents on the new host.
Core 31.1, custom LND 0.21.4 and PostgreSQL 16.2-alpine are pinned by the harness.
Four CPUs and a 32 GB disk caused very slow cold builds and disk pressure.
Avoid concurrent builds/formatter/live proofs. Keep enough disk for image staging,
imports and pod snapshots; reclaim only unused owned test images/build cache.
Do not repeatedly restore unchanged assets. No acceptance job remains running.

## Next bounded native coverage slice

Swarm funded **outbound** v1 opening recovery and standalone PSBT return recovery,
with a separate review/test owner. Coordinate shared workflow/wallet files.

Outbound opening:

- Before registering/signing at `accept_channel`, atomically freeze the full
  channel, exact message/features/nonces, funding template, fees, change, ordered
  inputs and original reservation ID. Keep reservation identity separate from
  workflow identity.
- Ambiguous saves/signing must retain inputs, temporary channel and signer
  history. Current outbound cleanup and block-only startup are insufficient.
- Restore consumed `V1FundingCreated` openings before stale-state repair; peer
  reconnect retransmits exact retained `funding_created` while awaiting ACK.
- Persist exact `funding_signed` and the unsigned funding transaction before
  wallet signing. Reuse original held inputs. Consume receipt and save broadcast,
  watch and reservation changes atomically before Core submission.
- Prove both formats, every crash boundary, changed inputs/policy, reconnect and
  concurrent admission with actual peer/chain acceptance.

Standalone `SignPsbt`/`FinalizePsbt` returns:

- They still lack a durable returned-result lifecycle. Do not treat the captured
  `SendOutputs` publication workflow as completing these APIs.
- Freeze exact original/enriched PSBT, unsigned transaction, owned input indexes,
  all previous outputs, wallet snapshot and lease exposure before operation 28.
  Enrich key metadata first: current `AddKeyInfo` invokes remote public wallet
  derivation and cannot run inside an operation-28-only capture scope.
- Derive stable lookup identity from immutable node context, return mode and
  exact input packet bytes, excluding writer epoch. Preserve the exact request
  and receipt. Startup may reconcile completed results but must never automatically
  sign an intent-only request or publish a standalone return.
- Atomically persist the exact signed packet/input indexes/final transaction and
  consume the workflow before API return. A new result entity will need all
  provider migrations/generated models. Return consumed bytes without rebuilding
  from current wallet state.
- Track irreversible signature exposure by outpoint and approved transaction
  digest; lease expiry/release cannot revoke a signature. Permit compatible
  Sign-to-Finalize reuse. Mixed packets require frozen foreign prevouts/witnesses
  and appropriate partial versus final verification.

## Remaining goal gates

Claims, anchors/CPFP, penalties, HTLC parents, fee bump/reorg, silent deposits and
remaining channel lifecycles still need complete capture/recovery and independent
purpose validation. The restricted withdrawal profile is not a whole-node mode.

Writer fencing must reach authoritative node persistence and trusted publication,
not just signer RPCs. Use the same independently managed authority row for writer
transfer and database commits, authenticated database session principals and
transaction-held fences on every mutable path, including bulk/accounting writes.
Never hold a node transaction/fence lock across a signer RPC. Trusted egress must
own Core/peer publication credentials and release only exact committed intents;
old workers must be unable to bypass it. Transfer must fence in-flight work and
peer sockets. Lost evidence fails closed. Already released Bitcoin signatures
cannot be revoked by an epoch change.

Finally prove hosted writer failover, paused stale writers, mismatched snapshots
and ambiguous outcomes with two isolated contexts. The existing separate-process
proof is a foundation, not completion of the four-milestone goal.
