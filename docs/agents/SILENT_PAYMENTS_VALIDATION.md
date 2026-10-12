# Silent payments validation

Branch: `wip/silent-payments`, merged with `wip/fafo` at `27c4ebc2`. Core proof source: `f92c75a2`; the subsequent collaborative accounting correction has the focused verification below.

## Completed Core proof

The owner specified normal Bitcoin Core and one complete successful pass. `sp-core-merged1-2` passed on the merged source: two outer wrapper tests, four inner Core tests and one inner PostgreSQL test, zero errors or failures, 164 s wall time. Further queued attempts were stopped and owned namespaces cleaned. The proof uses `scripts/run-cluster.sh`, Bitcoin Core 31.1 and the pinned upstream BIP 352 v1.1.1 Python reference wallet.

Both ZMQ and polling modes proved labeled/unlabeled receipts, independent interoperability in both directions, withdrawal, two-block reorg, exact custody rollback with pending-input reservations retained, reconfirmation, restart and encrypted-key-file recovery into an empty database. Recovery matched every silent and ordinary wallet outpoint and the complete balance: **2,398,697 sat** in each mode. A real channel became usable after funding it with a confirmed silent-payment input; operational and financial reconciliation stayed clean.

All three prevout routes agreed on P2PKH, P2SH-P2WPKH, P2WPKH, P2TR key-path, P2TR script-path and P2WSH inputs, including an in-block child. REST and `getblock` also passed without `txindex`. Pruned history was refused before recovery writes, with the actual prune floor. The captured Core response remains in `test/NLightning.Infrastructure.Bitcoin.Tests/Wallet/Fixtures/core31-prevouts.json`.

PostgreSQL migration and durable SP metadata tests passed in the runner pod. SQLite migration, atomicity and accounting tests also passed. SQL Server compiled-model and migration-history guards passed; SQL Server runtime execution is not claimed.

[core-merged-proof.json](proofs/silent-payments/core-merged-proof.json) records source/image identity, result counts, custody balance, proof markers and the full output-log hash. Host artifacts remain under `TestResults/cluster/sp-core-merged1/sp-core-merged1-2/`.

Earlier preflights exposed test setup errors in reorg notification, reservation-aware custody comparison and channel funding amount; the successful trial retains the strengthened assertions. The first merged attempt failed two miner startup/readiness checks during scratch-disk pressure, before their source RPC proofs; that attempt is excluded from acceptance.

## Fresh merged-source tests

| Suite | Passed | Scope |
|---|---:|---|
| Infrastructure.Bitcoin | 2,391 | Full suite; three platform skips. Includes official vectors, both key parities, reservations and mixed SP/foreign PSBT validation with an extra-TapTweak negative control. |
| Integration | 1,194 | Excludes Docker, SQL Server runtime and Cluster tests. Includes all three compiled models, final model guards, chronological migration-designer guards, accounting, failed saves, restarts and reorgs. |
| LND gRPC | 311 | Full suite, including durable SP history, canonical/imported ownership and upstream wallet/route changes. |
| Application | 115 | SP recovery/history, splice-out refusal, PayRoute/Attach, splice RBF and channel-acceptor compatibility. |
| Daemon | 89 | SP CLI/IPC, withdraw, Cashu and PayRouteAttach IPC. |
| Domain | 42 | Focused SP suite. |

Recovery history tests prove raw transactions and complete owned input amounts survive failed commits, mixed ordinary/SP inputs, final spend audits and reorg replay. The broad Application suite previously passed 4,473 tests before the final changes; that earlier run is not presented as final-source verification.

Full solution Release net10 passed with zero warnings/errors in 3 min 51 s; full solution Release net11 compile check passed with zero warnings/errors in 6 min 42 s. Production Native compilation on `f92c75a2` passed with zero warnings/errors in 4 min; full formatter verification passed. Solution configuration check passed for all 40 projects; `git diff --check` passed.

## Final collaborative accounting verification

A narrow follow-up to the accepted `f92c75a2` Core proof corrects collaborative-spend accounting: a transaction with our 75,000 sat input and 44,000 sat change records the wallet's **−31,000 sat** delta, including silent-payment ownership discovered from durable metadata only. Recovery promotion into live custody must preserve that delta and the 44,000 sat change without duplicate settlement or custody.

Final correction checks passed: 24 SQLite integration tests (including the three new collaborative cases), 25 Application recovery tests and 212 Bitcoin signer/PSBT tests, zero failures or skips. The changed production graph compiled for net11 and Release.Native with zero warnings/errors; the updated integration project compiled for net10 with zero warnings/errors. Full solution formatting verification passed again after the correction. The completed Core proof above retains its exact `f92c75a2` identity. Historical promotion uses the recovery writers explicitly; changing receive policy alone does not replay already processed live blocks.

## Scanner measurements and remaining limits

The unchanged optimized scanner was measured on Debian 13 x64, .NET 10.0.12, four logical CPUs, 4,000 transactions, 2,000 eligible transactions and 100 recovery labels, with two warmups and ten samples. Normal median/p95 were 1,666.899/1,815.703 ms. Adversarial K_max (2,323 discovered outputs per sample) median/p95 were 2,505.377/2,591.922 ms. Preparation reduced normal p95 by 60.5%. The adversarial baseline had only three samples, so its percentage comparison is weaker.

Measurements cover parsing and scanner mathematics with preloaded prevouts, excluding RPC/database. The adversarial cloud result meets the 60 s budget. The normal cloud result is above 1 s; the Mac reference-machine target remains unverified. Raw results: [normal](proofs/silent-payments/scanner-normal-cloud.json) and [adversarial](proofs/silent-payments/scanner-adversarial-cloud.json).

Receiving stays opt-in. Optional remote/light scanning (NL-1268) and mainnet activation/canary (NL-1270) remain separate work; this branch changes no live-node configuration. Key-only recovery restores funds within the documented address gap, while original accounting basis, imported scripts and operator metadata require a database backup. See [operator instructions](SILENT_PAYMENTS_OPERATOR.md).
