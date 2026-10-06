# LOOP_GRPC_PLAN — running Lightning Labs Loop (loopd) on NLightning's LND gRPC surface

## Implementation record (2026-10-06, NL-1190)

Baseline: PR #14 at `525347f6c0d6ed8bf0ce4d451e11e49265ccbed5`, which already includes the gRPC waves 2 and 3.
The sections below this record retain the attached research/design; their original checkout paths, “missing”
labels and merge instructions describe the earlier snapshot, not the current implementation.

L0–L4 now have implementations: State/Versioner; historical, reorg-aware ChainNotifier streams; durable isolated
swap keys; ECDH, witness-v0/taproot raw signatures and MuSig2 session RPCs; tapscript imports and confirmed watch
history; supplied invoice preimages; multiple outputs and outgoing-channel sets. The baseline already fixed
PublishTransaction RPC errors, dynamic relay fee floors, and raw external wallet transactions.

Operator configuration (normal LndGrpc TLS/macaroon options still apply):

```json
"LndGrpc": {
  "Enabled": true,
  "ListenAddress": "127.0.0.1",
  "EnableSigner": true,
  "Signer": {
    "AllowedKeyFamilies": [21, 99, 42060, 42068, 42069],
    "MaxSessions": 1000,
    "SessionLifetime": "01:00:00"
  },
  "MaxChainNotifierRegistrations": 128,
  "MaxChainNotifierScanBlocks": 1000000
}
```

Signer exposure defaults off; mainnet also requires `AllowMainnet` for the listener and
`AllowSignerOnMainnet` for the signer. A remote signer listener requires `ClientCaPath` (mutual TLS).
Use `BakeMacaroon` with only the RPCs Loop needs: signer `generate`/`read`, address `read`/`write`,
onchain `read`/`write`, offchain `read`/`write`, invoices `read`/`write`, info `read`. State startup RPCs
are unauthenticated as in LND; Versioner and ChainNotifier are authenticated. An admin macaroon includes signer
rights, but cannot bypass the family policy. Node/channel families 0–9 are always refused. A pubkey-only
signing request must resolve to a public key already stored in the ring; missing ECDH locators never select
the node identity.

The frozen path is `m/1017'/0'/family'/0/index` on all networks. Versions 1 and 2 retain the existing
network-genesis chain code, while version 3 uses its saved BIP32 master chain code. SQLite restart tests and
independently computed public-key vectors pin this distinction. Public keys are saved before issuance;
explicit DeriveKey advances the next index past it. Back up the database with the key file: restoring an old
allocation database can reuse an index. MuSig secret nonces are memory-only, consumed once, wiped after signing,
cleanup, expiry or disposal, and never supplied by callers. Restarting loses sessions and requires re-registration.
LND SignOutputRaw returns raw DER or **64-byte Schnorr** signatures; the caller supplies the witness sighash byte
(the earlier draft's non-default-sighash suffix statement is corrected here).

Imported outputs are confirmed-only and kept outside wallet UTXOs and the accounting feed. They appear in
WalletKit.ListUnspent and GetTransactions with raw transactions and spent outpoints, but never in ordinary
coin selection, FundPsbt, wallet signing, anchor reserves or spendable balances. Imports persist the exact
request and script; rebuilding from active blocks handles restart, disconnected deposits and disconnected
spends without txindex. This first implementation rebuilds from the earliest import when the processed tip or script set changes
(maximum one million blocks, 1000 scripts). A memory-only cache validates the active tip hash and persisted
script set on each query and returns defensive copies. Imports and same-height reorgs invalidate it; missing
or pruned data fails explicitly during rebuild. It has no mempool tracking or persistent history index. ChainNotifier reconciles the committed monitor tip every 500 ms, with a 100-block epoch reorg
window, bounded registrations/scans, cancellation cleanup and halt errors. Taproot spend registration requires
an outpoint; legacy/v0 scripts can be registered alone.

SendOutputs uses the existing funded-PSBT lease, signing, publication and rebroadcast path. A signing failure
releases its leases; an ambiguous publication failure keeps them. Unconfirmed selection and custom strategies
remain UNIMPLEMENTED. Outgoing-channel sets restrict every route-planning round; blinded payments with such a
restriction are refused explicitly. L5 external PSBT channel funding and Taproot Assets remain optional/out of scope.

Verification: focused gRPC tests (185), real SQLite restart/import/cache recovery and all-provider compiled-model checks
(25), and key-file derivation vectors (3) pass. Both Release and Release.Native compile on net10/net11; the
185 gRPC tests, 42 wallet spend tests and 25 persistence/compiled-model checks also pass with native crypto on both frameworks. Wallet spend tests cover production dependency injection, fee
quotes for Loop's zero-x taproot witness program, and safe lease handling after signing/publication failures.
The exact PR #14 baseline reproduces the two native Sphinx allocation-budget failures; these are NL-1201.
Timing-sensitive baseline tests are recorded separately in NL-1198, NL-1199 and NL-1202. The full non-Docker
Release suite passed on both frameworks (34,164 passed tests); later reorg/cache changes have focused checks.
The `loop` cluster suite launches the pinned Loop PR #1222 (`3d10930491713c1ee2f9957316747da9a3813072`),
Aperture v0.4.0, LND 0.21.4 and Bitcoin Core 29.0. Build with:

```bash
LOOP_SOURCE=/path/to/pinned/loop NLTG_LOOP_RUNNER_TAG=dev \
  test/NLightning.Integration.Tests/Cluster/Loop/image/build.sh
NLTG_LOOP_RUNNER_IMAGE=nltg-loop-runner:dev scripts/run-cluster.sh --suite loop -n 1
```

Images must be loaded into the selected local cluster; pull policy is Never. The suite owns one harness namespace,
runs our node and the Go processes in its in-cluster runner, and tests L402 plus real classic out/in and static in.
It fails on a terminal swap failure or a bounded timeout; it cannot pass by skipping missing binaries.
The final real Loop/Aperture/LND run (`loop-proof25`) passed in 636 seconds: classic out, classic in,
static in, recovery of an outstanding Loop Out after restarting both NLightning and loopd, historical
confirmation/spend/epoch parity against LND, six raw-signature descriptor shapes, and four mixed MuSig2
rounds with either side combining signatures. It also removed and reinstated a deposit through a live
reorg and confirmed its unilateral CSV timeout sweep after 4,321 expiry blocks. Logs are retained under
`TestResults/cluster/loop-proof25/loop-proof25-1/output.log.gz`. Remaining proof cases are tracked in NL-1196.
The pinned source-built regtest server does not implement cooperative withdrawal. The `wip/lnd-p2`
follow-up adds a test-owned `Cluster/Loop/server/withdraw.go` in a staged copy, leaving the pinned Loop
checkout and real Loop client unchanged. The extension validates confirmed Core prevouts and uses real
LND MuSig2 partial signatures; its image is labeled `nltg.loop-fixture=withdraw-v1`. This is regtest
fixture support, not a claim about a public Loop server.


## Recovery proof follow-up (wip/lnd-p2, NL-1190 / NL-1196)

The final owned run `lnd-p2-proof6` passed **1/1** (756.866 seconds in the runner; 764 seconds
including harness orchestration). All regtest cases below passed together. Logs are retained at
`TestResults/cluster/lnd-p2-proof6/lnd-p2-proof6-1/output.log.gz`; the runner image is
`nltg-loop-runner:lnd-p2-proof6`, with Loop pinned to `3d10930491713c1ee2f9957316747da9a3813072`
and the staged `withdraw-v1` server fixture. Mutinynet has not been run.

Verification on net10/net11: Release solution build has zero warnings/errors; all 191 gRPC tests and
1,201 integration tests pass per framework. The full non-Docker run completed with 34,264 passed and
four failures under concurrent load. Both unchanged graph timing failures (NL-1198) passed in isolation;
the onion metric race is repaired (NL-1205), and all 18 focused onion/accounting checks pass on each
framework. The unchanged accounting adjustment test's loaded net11 failure remains recorded as NL-1206.
All 4,370 Application tests passed on net10. Full formatting and the final edited-file checks pass;
solution mappings and all-provider compiled-model checks pass. No database schema changed.

The owned suite proved:

| Case | Required proof |
|---|---|
| LND client payment | Caller keysend hash/preimage reaches real LND; TrackPayments reports committed IN_FLIGHT then SUCCEEDED. |
| Classic Loop Out restart | Restart NLightning and loopd with an outstanding swap; its retained state reaches SUCCESS. |
| Classic Loop In restart | Wait for the HTLC funding broadcast, restart both daemons, then confirm the swap and SUCCESS. |
| Server loss and signer refusal | Stop the swap server after funding, restart both local daemons, refuse the first refund signature, restore opt-in, restart loopd, and confirm the timeout refund. |
| Terminal failure restart | Restart both daemons after the refund confirms; FAILED/TIMEOUT stays terminal. |
| Static deposit restart | Retain the confirmed imported deposit across both restarts, then complete static Loop In and confirm its spend. |
| Cooperative withdrawal restart | Retain another imported deposit across both restarts and a loopd-only restart, then use real two-party signing to confirm a withdrawal. |
| Spend reorg | Disconnect the withdrawal block; imported input becomes unspent and the withdrawal loses confirmation. Reconfirm and observe it spent again. A wallet destination can retain an unconfirmed transaction-history entry. |
| Deposit reorg | Remove and reinstate the confirmed deposit in both imported UTXOs and transaction history; Loop reconciles its replacement height. |
| CSV timeout | Mine the 4,320-block static lifetime plus expiry, then confirm Loop's unilateral signed sweep. |
| Notifier and signer parity | Historical conf/spend/epoch events versus LND, six raw-signature descriptor shapes, four mixed MuSig2 rounds with either combiner. |
| Mutinynet one-node trial | Pending node access, a confirmed Mutinynet-compatible server and explicit owner approval; see `LOOP_MUTINYNET_TRIAL.md`. |

The restart checkpoints matter. Loop's pre-signing static FSM deliberately aborts/relinquishes deposits
on recovery instead of reusing memory-only MuSig nonces. The static proof restarts with a confirmed
available deposit and initiates a fresh signing round; it does not claim that an interrupted pre-signing
attempt resumes. Pinned loopd also exits when its LND chain streams close, so node-restart recovery
includes a supervised restart of loopd from its retained database. Existing streams do not survive
process death.

---

Research draft, 2026-10-06. Not in the repo. Sources read:

- Loop `v0.35.0-beta` (latest release tag), clone at `/Users/ms/.claude/jobs/c064ce2b/tmp/loop`.
- lndclient `v0.21.0-2` (the version Loop's `go.mod` pins), clone at `/Users/ms/.claude/jobs/c064ce2b/tmp/lndclient`.
- lnd `v0.21.0-beta` (Loop's `go.mod`), btcwallet `v0.16.18`, aperture `v0.4.0`, all in `~/go/pkg/mod/...`.
- Loop PR #1222 "regtest: add source-built Loop server" (open, last updated 2026-09-29), fetched as branch `pr1222`.
- NLightning: wave 1 on `wip/fafo` (`/Users/ms/nlightning`), wave 2 on `wip/lnd-grpc-compat`
  (`/Users/ms/nlightning-lndgrpc`), wave 3 on `wip/lnd-grpc-wave3` (`/Users/ms/nlightning-lndgrpc3`).
  Wave 3 was branched from wave 1 and does not contain wave 2.

Below, `loop:` paths are under the Loop clone, `lndclient:` paths under the lndclient clone, `lnd:` paths
under `~/go/pkg/mod/github.com/lightningnetwork/lnd@v0.21.0-beta`.

---

## 0. Summary

- loopd talks to LND **only through lndclient**, with **one macaroon** (`--lnd.macaroonpath`, the admin
  macaroon or a custom macaroon that has every permission) and the LND TLS certificate pinned. lndclient
  builds **all** sub-server clients (Lightning, WalletKit, Signer, ChainNotifier, ChainKit, Invoices,
  Router, Versioner, State, WtClient) at startup. Before loopd does anything, it needs **State, Versioner,
  GetInfo, ChainNotifier block epochs and Signer.DeriveSharedKey**.
- Static address loop-in is **not** experimental-gated: its managers start on every loopd
  (`loop:loopd/daemon.go` ~640-760). Instant out and reservations start only with `--experimental`
  (`loop:loopd/daemon.go` ~754).
- Static loop-in needs four things NLightning does not have today: **(1) an LND-style key ring**
  (`DeriveNextKey`/`DeriveKey` by KeyLocator, sign by locator *or by public key*), **(2) a signrpc
  service** (SignOutputRaw for tapscript and P2WSH, MuSig2 session API, DeriveSharedKey), **(3) a
  chainrpc ChainNotifier** (block epochs, conf and spend notifications by txid/outpoint *or script*,
  historical dispatch from a height hint, reorg events), and **(4) watch-only taproot imports**
  (`ImportTapscript`) whose outputs show in `ListUnspent` and `GetTransactions` (with the raw tx).
  Plus Versioner and State services and a few fixes to existing waves (AddInvoice `r_preimage`,
  PublishTransaction error semantics, GetTransactions raw tx, MinRelayFee).
- **End-to-end proof is possible**: PR #1222 adds `cmd/loopserver-regtest`, a source-built regtest Loop
  server that runs real loop-out, loop-in and static-address loop-in, behind Aperture (L402). The server
  side keeps its own real LND; only the client's LND is replaced by NLightning. The PR is open, so we
  would pin its commit (`3d109304`) or wait for the merge.
- Biggest risks: exposing raw signing (`SignOutputRaw` signs any sighash the caller hands in), keeping
  key-ring derivation stable forever (loopd's macaroon DB and every swap key depend on it), and building a
  ChainNotifier with LND's exact historical/reorg semantics on top of our channel-centric chain monitor.

---

## 1. Startup and requirements

### 1.1 lndclient connection (loopd)

`loop:loopd/run.go:41-48` sets the floor:

```go
LoopMinRequiredLndVersion = &verrpc.Version{AppMajor: 0, AppMinor: 18, AppPatch: 4,
    BuildTags: []string{"signrpc", "walletrpc", "chainrpc", "invoicesrpc"}}
```

`loop:loopd/run.go:104-116` builds `lndclient.LndServicesConfig` with `CustomMacaroonPath`, `TLSPath`,
`CheckVersion`, and `BlockUntilChainSynced`, `BlockUntilUnlocked`, `BlockUntilChainNotifier` all true.

`lndclient:lnd_services.go` `NewLndServices` then, in order:

1. `lnrpc.State/SubscribeState` with the read-only macaroon and waits until the state is
   `RPC_ACTIVE`/`SERVER_ACTIVE` or the stream closes (`getLndInfo`, lines 646-760).
2. `lnrpc.Lightning/GetInfo` (lines 686-695). `newInfo` (`lndclient:lightning_client.go:1446-1484`)
   **fails** unless `block_hash` parses as a hash and `color` parses as `#rrggbb`; it reads
   `chains[0].network` (index 0 must exist), `identity_pubkey`, `block_height`, `synced_to_chain`, `uris`, etc.
3. Network check: `chains[0].network` must equal loopd's `--network` string (`regtest`, `testnet`,
   `signet`, `mainnet`...) (lines 835-840). Mutinynet must answer `signet`.
4. `verrpc.Versioner/GetVersion`: `UNIMPLEMENTED` is an error; `app_major/minor/patch` must be
   >= 0.18.4 and `build_tags` must contain the four tags (lines 866-944).
5. Builds every sub-server client with that one macaroon (lines 366-405; with `CustomMacaroonPath` the
   macaroon pouch uses the same file for every service).
6. `BlockUntilChainSynced`: polls `GetInfo` until `synced_to_chain` (`waitForChainSync`).
7. `BlockUntilChainNotifier`: retries `chainrpc.ChainNotifier/RegisterBlockEpochNtfn` until it does not
   fail with LND's "chain notifier server not active" error (lines 570-640, 785-800).

### 1.2 loopd's own macaroon DB needs `Signer.DeriveSharedKey` at every start

`loop:loopd/daemon.go:524-555` starts lndclient's macaroon service with `DBPassword: macDbDefaultPw`
(`loop:loopd/macaroons.go:20`, empty) and `EphemeralKey: lndclient.SharedKeyNUMS`,
`KeyLocator: lndclient.SharedKeyLocator` (family **21**, index **0**, `lndclient:macaroon_service.go:41-50`).
With an empty password `MacaroonService.Start` calls `Signer.DeriveSharedKey(NUMS, {21,0})` and encrypts
loopd's `macaroons.db` with the result (`lndclient:macaroon_service.go:194-235`). Consequence: **the key
at family 21 index 0 must never change** for a given node, or loopd cannot open its macaroon DB after
the first start (it only falls back to the empty password once, to migrate).

### 1.3 Other unconditional startup calls

- `GetInfo` for the current height (`loop:loopd/daemon.go:465-473`).
- `ListPayments` paged with `index_offset`/`max_payments` once, for the loop-out cost migration
  (`loop:cost_migration.go:141-160`; runs until a DB flag says it is done).
- `RegisterBlockEpochNtfn` by the swap executor (`loop:executor.go:79`), the static address manager
  (`loop:staticaddr/address/manager.go:84-85`), the deposit manager (`loop:staticaddr/deposit/manager.go:106`),
  the withdrawal manager (`loop:staticaddr/withdraw/manager.go:161`) and the sweep batcher
  (`loop:sweepbatcher/sweep_batch.go:842`). The deposit manager **reads the first epoch as the current
  tip** (lines 113-124), so LND's rule "no best block in the request = the current tip is sent at once"
  (`lnd:chainntnfs/bitcoindnotify/bitcoind.go:982-986`) is load-bearing.
- The liquidity manager (autoloop) calls `ListChannels(activeOnly=false, publicOnly=false)` on its ticks
  (`loop:liquidity/liquidity.go:420,662,824,1129`).
- The L402 interceptor pays the server's L402 invoice with `PayInvoice` → `routerrpc.SendPaymentV2` +
  `TrackPaymentV2` (`aperture:l402/client_interceptor.go:394-430`, `lndclient:router_client.go:599-640`).
  A static address requires an L402 token (`loop:staticaddr/address/manager.go:144`).

### 1.4 Macaroons and TLS

- One macaroon for everything (`loop:loopd/config.go:125-130`: "A custom macaroon must contain ALL
  permissions required for all subservers"). Our admin macaroon already has `signer:generate/read`
  (`nlightning-lndgrpc3:src/NLightning.LndGrpc/Macaroons/LndPermissions.cs:23,31`), so it works once the
  permission table knows the new methods. For least privilege the operator can bake a Loop macaroon
  (`BakeMacaroon`, wave 2) with exactly: `info:read`, `offchain:read/write`, `onchain:read/write`,
  `address:read/write`, `invoices:read/write`, `signer:read/generate`, `macaroon:...` not needed.
- The interceptor must have entries for `/signrpc.Signer/*`, `/chainrpc.ChainNotifier/*`,
  `/verrpc.Versioner/GetVersion` and `/lnrpc.State/*` (otherwise our interceptor answers
  `PERMISSION_DENIED`). LND's table: SignOutputRaw, ComputeInputScript, SignMessage, DeriveSharedKey,
  MuSig2CreateSession/RegisterNonces/RegisterCombinedNonce/Sign/CombineSig/Cleanup = `signer:generate`;
  VerifyMessage, MuSig2CombineKeys, MuSig2GetCombinedNonce = `signer:read`
  (`lnd:lnrpc/signrpc/signer_server.go:57-105`); the three ChainNotifier streams = `onchain:read`
  (`lnd:lnrpc/chainrpc/chain_server.go:62-70`); GetVersion = `info:read` (`lnd:lnrpc/verrpc/server.go:16`);
  `lnrpc.State` needs no macaroon in LND (lndclient sends the read-only one anyway). WalletKit:
  DeriveNextKey/DeriveKey = `address:read`, SendOutputs = `onchain:write`, ImportTapscript = `onchain:write`
  (`lnd:lnrpc/walletrpc/walletkit_server.go:76-180`) — wave 3's table already has these rows.
- TLS: lndclient pins the certificate file; our wave 1 TLS (self-signed ECDSA P-256, SANs) is what it
  expects. The host loopd dials must be in the SANs (`LndGrpc:TlsExtraDomains`).

---

## 2. Required RPCs per feature

Legend: L = `lnrpc.Lightning`, WK = `walletrpc.WalletKit`, S = `signrpc.Signer`, CN = `chainrpc.ChainNotifier`,
I = `invoicesrpc.Invoices`, R = `routerrpc.Router`, V = `verrpc.Versioner`, ST = `lnrpc.State`.

### 2.1 Startup (every loopd)

| RPC | Where | Semantics that matter |
|---|---|---|
| ST SubscribeState | `lndclient:lnd_services.go:667` | one `SERVER_ACTIVE` (or `RPC_ACTIVE`) event is enough |
| L GetInfo | `lndclient:lnd_services.go:686`, `loop:loopd/daemon.go:465` | `block_hash`, `color`, `chains[0].network`, `synced_to_chain`, `block_height > 0` |
| V GetVersion | `lndclient:lnd_services.go:870` | >= 0.18.4, tags signrpc/walletrpc/chainrpc/invoicesrpc |
| CN RegisterBlockEpochNtfn | `lndclient:lnd_services.go:575` and every manager | current tip sent at once; then every new tip |
| S DeriveSharedKey (NUMS, {21,0}) | `lndclient:macaroon_service.go:200` | `SHA256(compressed(k·P))`, stable forever |
| L ListPayments | `loop:cost_migration.go:141` | `index_offset`, `max_payments`, `include_incomplete`, `last_index_offset`; once |
| L ListChannels | `loop:liquidity/liquidity.go:420` | autoloop ticks |
| R SendPaymentV2 / TrackPaymentV2 | `aperture:l402/client_interceptor.go:394,425` | L402 token payment (`fee_limit_sat`, `timeout_seconds`) |

### 2.2 Static address: create address

`loop:staticaddr/address/manager.go:112-238`.

1. WK **DeriveNextKey(family 42060)** (`swap.StaticAddressKeyFamily`, `loop:swap/keychain.go:10`) →
   client key; Loop stores the returned KeyLocator (lines 149-151, 203-206).
2. Server returns its key and a CSV expiry. Address = P2TR, internal key
   `MuSig2 KeyAgg(sorted{client, server})` (BIP 327 "1.0.0-rc2" = final BIP 327), one tapleaf
   `<x(client)> OP_CHECKSIGVERIFY <expiry> OP_CHECKSEQUENCEVERIFY`, output key tweaked with the leaf root
   (`loop:staticaddr/script/script.go:62-127`).
3. WK **ImportTapscript** (full tree: internal key + the one leaf) so LND's wallet tracks the address
   (lines 217-225, `lndclient:walletkit_client.go:1026-1100`). This is the only deposit-detection mechanism.

### 2.3 Static address: deposits

`loop:staticaddr/deposit/manager.go`.

- WK **ListUnspent(min_confs 0, max_confs 0)** every block and every 10 s (`PollInterval`, line 35;
  `reconcileDeposits` 300-347), filtered by the static pkScript (`address/manager.go:313-327`). It
  **requires that LND's ListUnspent returns the imported watch-only taproot outputs**, with
  `address_type = TAPROOT_PUBKEY` (lndclient fails the whole call on any type other than P2WKH, NP2WKH,
  P2TR: `lndclient:walletkit_client.go:279-290`), `confirmations` (0 for mempool) and `pk_script`.
  An output that disappears from ListUnspent is treated as spent/replaced/reorged out (`syncActiveDeposits`).
  `max_confs 0` means "no maximum" (LND `ParseConfs`; wave 3's `ParseConfs` matches).
- WK **NextAddr(account "default", TAPROOT_PUBKEY, change=false)** per new deposit: the timeout sweep
  destination (lines 362-370).
- Expiry (CSV) sweep of a deposit: `loop:staticaddr/deposit/actions.go:30-127`:
  WK **EstimateFee(conf 3)** for `sat_per_kw` and `min_relay_fee_sat_per_kw` (lndclient's `MinRelayFee`
  is `EstimateFee(6).min_relay_fee_sat_per_kw`, `lndclient:walletkit_client.go:553-590`);
  S **SignOutputRaw** with a descriptor that has **only the public key, no KeyLocator**
  (`loop:staticaddr/deposit/fsm.go:538-557`: `KeyDesc{PubKey: ClientPubkey}`, `SignMethod:
  TaprootScriptSpendSignMethod`, `SIGHASH_DEFAULT`, witness script = the timeout leaf), prev outputs
  given; WK **PublishTransaction**, where an error containing "output already spent" is tolerated.
  Then CN **RegisterConfirmationsNtfn(txid or nil, script = sweep pkScript, 3 confs, heightHint)**
  (lines 140-143) — script-only when the txid is unknown.

### 2.4 Static address: loop-in

`loop:staticaddr/loopin/` (FSM in `fsm.md`).

- `InitHtlcAction` (`actions.go:62-350`): WK **DeriveNextKey(42060)** for the HTLC client key
  (lines 124-133; the KeyLocator is stored); L **AddInvoice** with a **client-chosen preimage**
  (`r_preimage`), `value_msat`, memo, `expiry` 1 year, optional `route_hints`, `private: true`
  (lines 136-148); WK **NextAddr(TAPROOT)** for the HTLC timeout sweep (line 319). On failure
  I **CancelInvoice** (line 367).
- `SignHtlcTxAction` (`actions.go:558-712`): L **GetTransactions(start 0, end -1)** through
  `lndTxOutChecker` (`txout_checker.go:24-77`) to prove every deposit outpoint is still unspent: the
  deposit's funding tx must be in the list **with its raw tx** (`raw_tx_hex`), and no listed tx may have
  the deposit in `previous_outpoints`; otherwise "deposit ... is no longer available" and the swap fails.
  Then three sets of MuSig2 sessions (standard / high / extreme fee HTLC txs), one per deposit:
  S **MuSig2CreateSession(version V100RC2, key_loc = the static address KeyLocator, all_signer_pubkeys =
  [client, server] 33-byte, taproot_tweak{script_root = timeout-leaf hash, key_spend_only=false})**
  (`staticutil/utils.go:105-121`), S **MuSig2RegisterNonces(server nonce)** → must answer
  `have_all_nonces`, S **MuSig2Sign(digest = BIP 341 sighash SIGHASH_DEFAULT computed by Loop, cleanup=false)**
  → 32-byte partial signature (`loopin.go:202-265`), S **MuSig2Cleanup** (`actions.go:776-790`).
  Loop sends our public nonces and partial sigs to the server; the server holds the combined signature.
- `MonitorInvoiceAndHtlcTxAction` (`actions.go:796-1360`): L **LookupInvoice** (806, 904),
  I **SubscribeSingleInvoice** (833; states OPEN/ACCEPTED/SETTLED/CANCELED), CN
  **RegisterConfirmationsNtfn(nil txid, HTLC pkScript, ..., WithReOrgChan)** (861-864: reorg events used),
  L **GetInfo** for height (1129).
- HTLC timeout sweep (`actions.go:1369-1560`, `loopin.go:368-470`): WK EstimateFee, L GetInfo, S
  **SignOutputRaw** with **KeyLocator only** (no pubkey), `WitnessV0SignMethod`, P2WSH HTLC v2 timeout
  script, SIGHASH_ALL, prev outputs nil (segwit v0); WK PublishTransaction; CN RegisterConfirmationsNtfn
  (1413).
- Sweepless sweep after success (`manager.go:271-455`): the server sends a PSBT; per deposit
  S MuSig2CreateSession (as above) + RegisterNonces + Sign + Cleanup.

### 2.5 Static address: withdrawal

`loop:staticaddr/withdraw/manager.go`: WK NextAddr(TAPROOT) unless a destination is given (410),
WK EstimateFee (421), per deposit S MuSig2CreateSession/RegisterNonces/**Sign**/**CombineSig** (Loop
combines our partial sig with the server's into the final 64-byte Schnorr witness, 790-880),
WK PublishTransaction (644; tolerated errors: "output already spent", `chain.ErrSameNonWitnessData`
"txn same nonwitness data in mempool", `chain.ErrInsufficientFee` "insufficient fee"),
CN **RegisterSpendNtfn(outpoint + static pkScript, heightHint)** then RegisterConfirmationsNtfn on the
spender (681-697).

### 2.6 Static address: open a channel from deposits (optional)

`loop:staticaddr/openchannel/manager.go:430-750`: L **OpenChannel** stream with a **PSBT funding shim**
(`OpenStatusUpdate_PsbtFund`) and L **FundingStateStep** (PsbtVerify, PsbtFinalize, ShimCancel), funded by
a MuSig2-signed withdrawal tx. NLightning has no external-PSBT channel funding; defer.

### 2.7 Loop out (classic)

- WK DeriveNextKey(**99**) (`loop:loopout.go:136`), L DecodePayReq (1684), R **SendPaymentV2** with
  `fee_limit_sat`, `timeout_seconds`, `max_parts`, **several `outgoing_chan_ids`** (autoloop and
  `--channel` give a set) (`loop:loopout.go:827-833`), R TrackPaymentV2 (977, 1188, 1633), prepay payment.
- Optional routing plugin: L QueryRoutes and R **QueryMissionControl** (`loop:routing_plugin.go:291,512,588`),
  L GetNodeInfo, GetChanInfo.
- Sweep batcher (`loop:sweepbatcher/`): CN RegisterSpendNtfn (with reorg chan), RegisterConfirmationsNtfn
  (with reorg chan), RegisterBlockEpochNtfn; WK EstimateFee(2), MinRelayFee, NextAddr, PublishTransaction;
  S MuSig2CreateSession(family-99 key, taproot tweak with the HTLC root)/RegisterNonces/Sign/CombineSig
  (cooperative), S SignOutputRaw (non-cooperative success path).
- Recovery tool `loopd/sweep_htlc.go`: S **SignOutputRawKeyLocator**, WK **DeriveKey** scanning up to
  20,000 indices of family 99 (stateless recovery) — this one needs derivation that is deterministic
  from the seed, not just consistent.

### 2.8 Loop in (classic)

`loop:loopin.go`: I **AddHoldInvoice** (probe invoice, 216), L AddInvoice with preimage (195),
WK DeriveNextKey(99) (185), S **DeriveSharedKey(swap-hash pubkey, {99,0})** for the HTLC internal key
(1241-1252), WK **SendOutputs** (funds the HTLC from the wallet, 828), WK EstimateFee, CN conf/spend
notifications, I SubscribeSingleInvoice and CancelInvoice, WK NextAddr + S SignOutputRaw (timeout sweep).

### 2.9 Reservations and instant out (`--experimental` only)

Reservations (`loop:instantout/reservation/`): WK DeriveNextKey(**42068**), S MuSig2CreateSession,
CN conf/spend/block. Instant out (`loop:instantout/`): WK DeriveNextKey(**42069**), L DecodePayReq,
R SendPaymentV2 (`max_parts`), S MuSig2 RegisterNonces/Sign/CombineSig/Cleanup, S SignOutputRaw,
WK NextAddr/EstimateFee(3, 6)/MinRelayFee/PublishTransaction, L GetInfo, CN conf. Same primitive set as
static addresses, no extra subsystem.

### 2.10 Liquidity manager / autoloop

L ListChannels (and the static-address autoloop in `loop:staticaddr/loopin/autoloop*.go`, which reuses
2.3/2.4). Nothing new.

---

## 3. Key derivation: what Loop assumes

- Families used: **21** (shared key for loopd's macaroon DB, index 0), **99** (classic swaps; index 0 also
  for DeriveSharedKey), **42060** (static address and its HTLC keys), **42068** (reservations), **42069**
  (instant out).
- Loop **persists KeyLocators** returned by `DeriveNextKey` and later signs with them (MuSig2CreateSession
  requires `key_loc`, `lnd:lnrpc/signrpc/signer_server.go:966-972`; SignOutputRaw with a locator-only
  descriptor). It also signs **by public key alone**: lndclient's `SignOutputRaw` sends *either* the pubkey
  *or* the locator (`lndclient:signer_client.go:247-266`), and the static deposit timeout sweep passes only
  the pubkey (`loop:staticaddr/deposit/fsm.go:546-550`). LND resolves a bare pubkey through its wallet's
  address→key map (`lnd:lnwallet/btcwallet/signer.go:203-237`).
- So Loop needs **consistency** (a locator returned once always yields the same key; a pubkey handed out
  can be signed with; `DeriveNextKey` never returns an index twice) for normal operation, and
  **determinism from the seed** for the `sweep_htlc` stateless recovery tool and for the restore case
  (loopd's DB survives but the node is restored from seed).
- LND's own layout is `m/1017'/coin'/family'/0/index`. NLightning's v3 node key is already
  `m/1017'/0'/6'/0/0` (`src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs:33-36`),
  channel keys are `m/6425'/...` and wallet keys `m/84'`/`m/86'` (`src/NLightning.Domain/Bitcoin/Constants/KeyConstants.cs`).
  A key ring at `m/1017'/0'/family'/0/index` is therefore disjoint from every key we use except family 6
  index 0 (the node key) — **refuse families 0-9** (LND's internal families) unless explicitly allowed.
  For v1/v2 key files (non-BIP-32 master) the ring needs the same "file version decides derivation" rule.
- Not required: byte-identical keys with a real LND (our seed is not an aezeed seed).

---

## 4. Static address protocol specifics

- **Address**: P2TR. Internal key = MuSig2 (BIP 327, `MuSig2Version100RC2`, keys sorted) of client
  (us, family 42060) and server; script tree = one leaf `<x(client)> CHECKSIGVERIFY <csv> CSV`; output key
  tweaked with that root (`script.go:62-100`). Key path = 2-of-2 MuSig2 (cooperative: loop-in HTLC tx,
  sweepless sweep, withdrawal); script path = our unilateral CSV timeout sweep.
- **Who signs what**: we produce MuSig2 partial signatures over Loop-computed BIP 341 sighashes (we do
  not see the transaction in signrpc, only the digest); we combine for withdrawals; the server combines
  for HTLC txs and sweepless sweeps. LND's MuSig2CreateSession sorts keys (`musig2.NewContext(privKey,
  true, ...)`, `lnd:input/musig2.go:456`), generates the secret nonce internally, returns the 66-byte public
  nonce and a session id `SHA256(combinedKey || pubNonce)` (`lnd:input/musig2.go:579-592`); sessions live
  in memory only (lost at restart; Loop recreates them per signing round).
- **Deposit detection**: only through LND's wallet: ImportTapscript once, then ListUnspent polling, and
  GetTransactions to check deposits before signing. No ChainNotifier for deposits.
- **HTLC funding**: the loop-in HTLC tx spends the deposits by key path (server publishes it); change goes
  back to the static address (it shows up as a new deposit through ListUnspent).
- **Timeouts**: deposit CSV sweep via SignOutputRaw tapscript (pubkey-only descriptor); HTLC CLTV sweep
  via SignOutputRaw segwit v0 (locator-only descriptor). Both published with WK PublishTransaction.
- **Wallet assumptions**: imported static-address outputs are watch-only (LND cannot spend them alone) and
  are never picked by LND's coin selection; our wallet must exclude them from coin selection, `withdraw`,
  anchor reserve, `FundPsbt`, `walletbalance` "confirmed spendable", etc.

---

## 5. Mapping against NLightning

Status per required RPC. W1 = `wip/fafo`, W2 = `wip/lnd-grpc-compat`, W3 = `wip/lnd-grpc-wave3`.

### 5.1 Startup set

| RPC | Status | Notes / backing |
|---|---|---|
| ST SubscribeState / GetState | **missing** | trivial: one `SERVER_ACTIVE` then hold the stream; needs `stateservice.proto` and permission rows |
| V GetVersion | **missing** | `verrpc/verrpc.proto`; report the LND API level we emulate (W1 `GetInfo` already prints `LndApiVersion`) and the build tags we serve; owner decision (O1) |
| L GetInfo | W1 | `LightningService.Info.cs`: color `#rrggbb`, `chains[0].network` (`regtest`/`testnet`/`testnet4`/`mainnet`; check mutinynet → `signet`), `BlockHash` only set when the monitor has a hash (line 60) — must always be a valid hash for lndclient |
| CN RegisterBlockEpochNtfn | **missing** | new ChainNotifier service over `BlockchainMonitorService.OnNewBlockDetected`/`OnBlockDisconnected` |
| S DeriveSharedKey | **missing** | `ISecureKeyManager.ComputeNodeSharedSecret` exists for the node key only; needs key-ring keys |
| L ListPayments | W1 | check `index_offset`/`last_index_offset` paging (NL-1163) |
| L ListChannels | W1 | |
| R SendPaymentV2 / TrackPaymentV2 | W2 | L402 payment works (one or no outgoing channel) |

### 5.2 Static loop-in set

| RPC | Status | Notes / backing |
|---|---|---|
| WK DeriveNextKey / DeriveKey | **missing** (permission rows exist in W3) | new key ring (§6.1) |
| WK ImportTapscript | **missing** (permission row in W3) | new watch-only taproot import (§6.3) |
| WK ListUnspent | W3, **partial** | `WalletPsbtService.ListUnspentAsync` lists only wallet-address UTXOs (`utxo.WalletAddress is null` skipped, `nlightning-lndgrpc3:src/NLightning.Infrastructure.Bitcoin/Wallet/WalletPsbtService.cs:93-97`); must add imported outputs (TAPROOT_PUBKEY); unconfirmed outputs only if the monitor tracks mempool deposits |
| WK NextAddr (TAPROOT, "default") | W3 | OK |
| WK EstimateFee | W3, **partial** | `MinRelayFeeSatPerKw` hard-coded 253 (`WalletKitService.cs:106`); use `IBitcoinChainService.GetMempoolMinFeeRatePerKwAsync` / bitcoind `relayfee` |
| WK PublishTransaction | W3, **wrong for lndclient** | W3 reports failures in `publish_error` and answers OK (`WalletKitService.cs:235-258`); LND returns an RPC error (`lnd:lnrpc/walletrpc/walletkit_server.go:690-695`) and lndclient **ignores the response body** (`lndclient:walletkit_client.go:508-526`), so Loop would believe a refused sweep was published. Return an error with LND's strings: "transaction rejected: output already spent" (`lnd:lnwallet/interface.go:71`), "insufficient fee", "txn same nonwitness data in mempool" (`btcwallet:chain/errors.go:284,389`) |
| L GetTransactions | W3, **partial** | built from accounting events; `raw_tx_hex` only when a `BroadcastTransactions` row exists (`LightningService.Transactions.cs:192-197`), so an externally funded deposit has no raw tx → Loop's `TxOutChecker` drops it and **every static loop-in fails at signing**. Must include imported-address transactions with raw tx (fetch by block hash from bitcoind) and previous outpoints of spends |
| S MuSig2CreateSession / RegisterNonces / Sign / CombineSig / Cleanup | **missing** | over our BIP 327 port (`IMusig2Service`, `src/NLightning.Domain/Crypto/Interfaces/IMusig2Service.cs`; `AggregatePubKeys` with tweaks, `GenerateNonce`, `Sign`, `AggregatePartialSignatures`); needs a session store (§6.2) and V100RC2 only (V040 refused) |
| S SignOutputRaw (taproot script path, segwit v0) | **missing** | NBitcoin sighash + key ring key; by locator or by pubkey (§6.1, §6.2) |
| L AddInvoice with `r_preimage`, `route_hints`, 1-year expiry, `private` | W1, **refuses `r_preimage`** (`nlightning-lndgrpc:src/NLightning.LndGrpc/Services/LightningService.Invoices.cs:34-35`) | static and classic loop-in both supply the preimage; `InvoiceService` must accept a caller preimage (and caller route hints, or ignore them as LND's `private` already adds ours) |
| L LookupInvoice | W1 | |
| I SubscribeSingleInvoice / CancelInvoice (plain invoice) | W2 | `CancelInvoice` cancels open invoices too (`InvoicesService.cs:112-121`) |
| CN RegisterConfirmationsNtfn (txid or script-only, heightHint, reorg) | **missing** | §6.4 |
| CN RegisterSpendNtfn (outpoint + script, heightHint, reorg) | **missing** | §6.4 |

### 5.3 Classic loop out / loop in, instant out, reservations

| RPC | Status | Notes |
|---|---|---|
| R SendPaymentV2 with several `outgoing_chan_ids` | W2 **partial** ("one outgoing channel at most", `RouterService.Payments.cs:37-38`) | `PaymentService` takes one `--out` channel; a set needs the route planner to accept a channel set |
| R QueryMissionControl, L QueryRoutes | missing | optional (routing plugin off by default) |
| I AddHoldInvoice | W2 | |
| WK SendOutputs | **missing** | back with `IWalletSpendService` (`WalletSpendService`, the `withdraw` path) generalised to several outputs and a sat/kw rate |
| L DecodePayReq | W1 | |
| L OpenChannel PSBT shim + FundingStateStep | missing | static "open channel from deposits" only; defer |

---

## 6. New subsystems

### 6.1 Key ring (Domain port `IKeyRing`, Infrastructure.Bitcoin implementation)

- `DeriveNextKey(family)`: next index for that family, **persisted before the key is returned**
  (new table `KeyRingKeys(Family, Index, PubKey, CreatedAt)` + per-family counter; migration on all three
  providers). `DeriveKey(family, index)`: pure derivation, also recorded so the pubkey is findable.
- Derivation `m/1017'/0'/family'/0/index` from the key file's master (new `ISecureKeyManager` method, private
  keys never leave Infrastructure.Bitcoin). Deterministic from the seed, so `sweep_htlc` recovery and a seed
  restore work; the counters can be re-learned by scanning.
- `FindByPubKey(pubkey)`: only keys of the ring (never wallet `m/84'`/`m/86'` keys, never channel keys,
  never the node key) — stricter than LND, which signs with any wallet key by pubkey.
- Family policy (`LndGrpc:Signer:AllowedKeyFamilies`, default `[21, 99, 42060, 42068, 42069]`; 0-9 always
  refused, NUMS shared key on 21 only). Unknown families fail with `PERMISSION_DENIED`.

### 6.2 signrpc `Signer` service (`NLightning.LndGrpc`)

- `SignOutputRaw` / `ComputeInputScript` (latter optional for Loop): parse the tx, check `prev_outputs`
  count/order for taproot, compute the sighash with NBitcoin per `sign_method` (WITNESS_V0, TAPROOT_KEY_SPEND_BIP0086,
  TAPROOT_KEY_SPEND, TAPROOT_SCRIPT_SPEND), apply `single_tweak`/`double_tweak`/`tap_tweak`, sign with the key
  ring key resolved from locator or pubkey. Return raw sigs exactly like LND (DER without sighash byte for v0;
  64-byte Schnorr, plus the sighash byte when not DEFAULT, for taproot).
- MuSig2: in-memory session table keyed by `SHA256(combinedKey || pubNonce)`, bounded (e.g. 1,000 sessions,
  TTL 1 h), secret nonces generated by `IMusig2Service.GenerateNonce` with fresh randomness and the signer key,
  deleted after `Sign` (nonce reuse is impossible by construction), `Cleanup` idempotent. Version V100RC2
  only. Taproot tweak: `script_root` → x-only tweak `TapTweak(P || root)`; `key_spend_only` → BIP 86 tweak.
- `DeriveSharedKey`: `SHA256(compressed(k·P))` with a key-ring key; **a missing `key_loc`/`key_desc`
  (LND's default = node key, family 6) is refused**: node-key ECDH is our Sphinx/BOLT 8 secret.
- `SignMessage`/`VerifyMessage` (signrpc variants): not needed by Loop; leave UNIMPLEMENTED.

### 6.3 Watch-only taproot imports (`ImportTapscript`)

- Compute the output key from internal key + full tree / partial reveal / root hash / full key (BIP 341
  TapBranch ordering), store `ImportedTapscripts(OutputKey, InternalKey, Leaves, CreatedHeight)`.
- The chain monitor watches those scripts like wallet addresses (deposits, spends, reorg rollback) but
  their UTXOs carry an "imported, not spendable" flag: excluded from `IFeeInputSelector`, `IWalletSpendService`,
  `AnchorReserveService`, `FundPsbt`, `walletbalance` spendable totals, accounting as **our** funds
  (Loop deposits are 2-of-2 until swept; the accounting treatment is an owner question).
- `ListUnspent` and `GetTransactions` include them (with raw tx and block hash; for unconfirmed ones only if
  we follow the mempool — optional).
- A rescan from `CreatedHeight` on import (LND does none for fresh addresses; ours can skip it too).

### 6.4 chainrpc `ChainNotifier` service

- In-memory registrations (LND's are memory-only; Loop re-registers after any restart).
- Block epochs: send the tip at once (or catch up from a client `BlockEpoch` hash/height), then each block from
  `OnNewBlockDetected`; reorgs → new tip epochs from the fork (LND sends the new chain's blocks).
- Confirmations: by txid (bitcoind `getrawtransaction` with block hash / our watch) or **script-only** (zero
  txid, scan block outputs). Historical dispatch: scan blocks `heightHint..tip` with `GetBlockAsync(height)`
  (no txindex needed; bounded scan; pruned nodes limited), then live. Emit `Conf` once `num_confs` reached
  (`raw_tx`, `block_hash`, `block_height`, `tx_index`, `raw_block` when asked); on a disconnect below the
  confirmation emit `Reorg` and re-arm.
- Spends: by outpoint (+script) from `OnBlockInputs`, historical scan from `heightHint`; `Spend` with spending
  tx, input index, height; `Reorg` when the spend is disconnected. Confirmed spends only (LND's chainrpc
  semantics); mempool spends not needed.
- Error string before the monitor runs: LND's "chain notifier RPC is still in the process of starting"
  family so lndclient's retry loop works (`lndclient:lnd_services.go:785-800`).
- Halt gate: while `chainstatus` reports a halt, streams error out (Loop retries) rather than going silent.

### 6.5 Small fixes to existing waves

- AddInvoice: accept `r_preimage` (and `route_hints`), 1-year expiry.
- PublishTransaction: RPC error with LND strings (above).
- EstimateFee: real `min_relay_fee_sat_per_kw`.
- GetTransactions: raw tx for every listed tx, imported addresses included.
- GetInfo: always a block hash; mutinynet/signet network name.
- SendPaymentV2: `outgoing_chan_ids` sets (classic loop out/autoloop only).

---

## 7. Security design

- **Off by default**: a separate switch `LndGrpc:EnableSigner` (signrpc + key-ring WalletKit methods +
  ImportTapscript), refused on mainnet unless `LndGrpc:AllowSignerOnMainnet`, in addition to
  `LndGrpc:Enabled`/`AllowMainnet`.
- **What raw signing exposes**: `SignOutputRaw` signs any digest the caller derives from any transaction
  with any key the ring can resolve; LND's only gate is `signer:generate`. Mitigations here: the ring
  contains only `m/1017'/0'/family'/...` keys of allow-listed families, so wallet funds, channel keys and the
  node key are unreachable through signrpc; DeriveSharedKey never uses the node key; MuSig2 sessions bounded
  and memory-only; per-call audit log (family, index, sighash type, input index, txid); the default admin
  macaroon keeps signer rights (LND compatible) but the docs recommend a baked Loop macaroon, and a
  `LndGrpc:Signer:RequireDedicatedMacaroon` option could refuse signer calls from the admin macaroon.
- Funds at risk through signrpc are therefore only funds locked to ring keys (Loop's swaps and static
  addresses), which is exactly what a compromised Loop macaroon could steal on LND too.
- Loopback/Unix-socket listener recommended (`ListenAddress 127.0.0.1`); remote use requires mutual TLS
  (`ClientCaPath`).
- Key-file version: the ring for v1/v2 key files must be derived the way that file version derives (the
  NL-159 rule); changing it later strands Loop's static address and macaroon DB. Pin with vectors.
- Update `docs/agents/SECURITY_REVIEW.md` with an SR entry for signer exposure.

---

## 8. Waves (rough sizing; S ≈ 1-2 days, M ≈ 3-5, L ≈ 1-2 weeks of agent work)

**L0 – prerequisites (S-M).** Merge waves 2 and 3 into one branch (they diverge from wave 1). Fix §6.5 items
for static loop-in (AddInvoice `r_preimage`, PublishTransaction errors, EstimateFee min relay, GetInfo
block hash). Add `stateservice.proto`, `verrpc.proto`, `signrpc`/`chainrpc` protos (vendored, manifest),
State and Versioner services, permission rows. Outcome: lndclient's `NewLndServices` gets past the version
check (then stops at the ChainNotifier wait).

**L1 – ChainNotifier (M-L).** §6.4 over `BlockchainMonitorService` (`OnNewBlockDetected`,
`OnBlockInputs`, `OnBlockDisconnected`, `IBitcoinChainService.GetBlockAsync`). Unit tests against a fake
chain with reorgs; parity tests against LND on the cluster (same registrations to LND alice and to us, same
events). Outcome: loopd starts up to the macaroon DB.

**L2 – key ring + signrpc (L).** §6.1, §6.2: `IKeyRing`, migration `AddKeyRing`, `ISecureKeyManager`
derivation, Signer service (SignOutputRaw, DeriveSharedKey, MuSig2 x5), WalletKit DeriveNextKey/DeriveKey.
Vectors: BIP 327 (already), LND signrpc behaviour reproduced with LND alice on the cluster (sign the same
digest/descriptor shapes; MuSig2 2-of-2 between LND and us both ways). Outcome: loopd starts fully; classic
loop out works (needs no ImportTapscript).

**L3 – static address (M-L).** §6.3 ImportTapscript, watch-only UTXOs in the chain monitor and wallet
(coin-selection exclusions, accounting decision), ListUnspent/GetTransactions with imported outputs and raw
txs. Outcome: `loop static new`, deposit, `loop static in`, `loop static withdraw`, CSV timeout sweep.

**L4 – classic loop in, instant out, reservations (M).** WK SendOutputs over `IWalletSpendService`,
SendPaymentV2 outgoing channel sets, optional QueryMissionControl/QueryRoutes.

**L5 – (optional) open channel from deposits (L).** OpenChannel PSBT shim + FundingStateStep: external
PSBT funding of our v1/v2 opens. Separate decision.

## 9. Test strategy

- **Unit / in-process**: the new services against fakes (ChainNotifier reorg matrix, key ring persistence
  and family policy, MuSig2 session lifecycle, SignOutputRaw descriptor shapes incl. pubkey-only and
  locator-only, PublishTransaction error mapping). Drive them with **lndclient itself** is not possible from
  .NET; instead use our in-tree LND client (`test/NLightning.Testing.Lnd`) after regenerating it with the
  signrpc/chainrpc/verrpc/state protos.
- **Parity on the cluster**: the `lnd` suite already has LND 0.21.4; for each new RPC issue the same call
  to LND alice and to our node and compare shapes (block epochs, conf/spend events on the same tx, MuSig2
  2-of-2 signing between LND and NLightning that verifies on chain).
- **End to end with real Loop**: PR #1222's source-built `cmd/loopserver-regtest` (regtest-only, in-memory,
  "real protocol" for loop out, loop in and static loop-in, Aperture with L402). Topology: bitcoind, server
  LND (real, `lightninglabs/lnd`), `loopserver-regtest`, Aperture, `loopd` pointed at **NLightning**
  (`--lnd.host`, our `admin.macaroon`, our `tls.cert`), channels both ways between NLightning and the server
  LND. Script = PR's `regtest/e2e.sh` with `lndclient` commands replaced by `nltg` (deposit funded by
  `nltg withdraw <static address>` or bitcoind). As a new cluster suite `loop` (images built locally like
  `custom_lnd`, pull policy Never): `loopd` and `loopserver-regtest` built from Loop at a pinned commit
  (PR head `3d109304` until merged), Aperture `v0.4.0`. The old `lightninglabs/loopserver` image is
  binary-only and its static-address support is unknown; do not depend on it.
- Proofs per wave: L0 `loopd` connects (log "lnd version ..."), L1/L2 `loop getinfo` + classic loop out,
  L3 static deposit → `loop static in` SUCCEEDED → sweepless sweep confirmed; withdrawal; CSV timeout
  sweep after `mine <expiry>`; restart of NLightning and of loopd mid-swap; reorg of the deposit.

## 10. Risks and open questions for the owner

1. **Versioner answer (O1)**: we must claim LND >= 0.18.4 and the four build tags for lndclient to connect.
   Report "0.21.4-beta" (the API level we emulate) or the minimum that works? Either way it is a claim about
   LND compatibility; LndGrpc `GetInfo.version` already says `0.21.4-beta nlightning-...`.
2. **Raw signer exposure (O2)**: accept LND's model (signer macaroon = can sign anything in the ring) with
   the family allow-list and node-key exclusion above? Mainnet default off?
3. **Key-ring derivation (O3)**: freeze `m/1017'/0'/family'/0/index` (coin type 0 on every network, like our
   node key) and the v1/v2 file rule now; it can never change after a user creates a static address.
4. **Accounting of static-address deposits (O4)**: they are 2-of-2 with the Loop server until swept; book
   them as ours (pending), as external, or not at all until a timeout sweep pays our wallet?
5. **Chain notifier scope (O5)**: historical scans need blocks from `heightHint`; on a pruned bitcoind old
   hints fail. Acceptable (error) or require unpruned?
6. **Mempool deposits (O6)**: Loop shows deposits at 0 conf if LND does; we would only report confirmed ones
   unless the monitor tracks mempool outputs to imported scripts. Fine for a first version?
7. **PR #1222 not merged**: the e2e suite pins an unmerged commit; if Lightning Labs changes it, the suite
   needs updates. The server image cannot be used for mainnet tests: mainnet proof means the real Loop
   service (paid, L402) with small amounts — the canary question again.
8. **Merge order**: waves 2 and 3 are separate branches off wave 1; Loop needs both plus this plan's work.
9. **Open-channel-from-deposits** (L5) needs PSBT-funded channel opens; out of scope unless wanted.
10. Loop also has optional Taproot Assets (tapd) paths (`loop:assets/`); out of scope.
