# Silent payments validation

Branch: `wip/silent-payments`, merged with `wip/fafo` at `27c4ebc2`.

The previous-base batch `sp-core-final2` completed three consecutive full green Core runs. Fresh validation on the merged base is pending; previous-base results do not certify the new durable-history integration.

This record distinguishes completed runtime checks from pending acceptance gates. All cluster proofs use the repository's `scripts/run-cluster.sh` wrapper and owned namespaces. The owner requested normal Bitcoin Core for proofs; rbitcoin is not required or used.

## Completed Core preflight

Run `sp-core-preflight1` used Bitcoin Core 31.1 and the independent, pinned BIP 352 v1.1.1 Python reference implementation. Both ZMQ and polling modes demonstrated payments from the reference wallet to NLightning and from NLightning to the reference wallet, verified on chain.

All three prevout routes agreed on actual P2PKH, P2SH-P2WPKH, P2WPKH, P2TR key-path, P2TR script-path and P2WSH inputs, including an in-block child. REST and `getblock` also passed without `txindex`. Pruned history was refused with the actual prune floor. The authentic response is retained in `test/NLightning.Infrastructure.Bitcoin.Tests/Wallet/Fixtures/core31-prevouts.json`, with wire hashes and image identity.

The full preflight failed: both node modes timed out after invalidation while awaiting a smaller tip. The revised proof mines empty replacement blocks before asserting rollback and then explicitly reconfirms the disconnected withdrawal. A successful preflight interoperability check does not count as a green full proof.

## First final Core run: partial success, not an acceptance pass

The first final Core run passed the PostgreSQL migration and durable metadata checks. Both ZMQ and polling modes passed independent interoperability, spend/reorg/reconfirmation and encrypted-key-file recovery into an empty database. The restored wallet matched all ordinary and silent-payment custody exactly: 2,398,697 sat in each mode.

The run failed only the channel-funding assertion: its 500,000 sat request was fully covered by ordinary coins, so the selected inputs did not include a silent-payment coin. The fixture correction in `aa897ba0` requests the total ordinary balance plus 500,000 sat, requiring a silent-payment input. That correction and the three consecutive full green Core runs remain unverified here. Partial successes do not count toward the three-run acceptance gate.

## Unit checks and scanner measurements

The fresh full Release net10 build passed with zero warnings/errors in 4 min 2 s. Final executed suites passed: Bitcoin 2,375 (three platform skips), focused Domain 42, Daemon 84, LND gRPC 259 and Integration 1,185 (5.01 min). Bitcoin coverage includes official BIP 352 vectors, both key parities, signing, reservations, change recovery and captured Core responses. PostgreSQL runtime migration and metadata verification passed in the first final Core run; SQL Server runtime migration execution is not claimed.

The final Application targeted run is pending its fixture-only correction and must not be reported green yet. Earlier, the historical/helper service-and-splice selection passed 22 cases; the broad Application suite passed 4,473 tests before the final historical-settlement fixes. Six optional tool/benchmark/real-node checks reported unavailable in that broad run. Those earlier results do not validate later source changes.

The optimized synthetic scanner measurement used Debian 13 x64, .NET 10.0.12, four logical CPUs, 4,000 transactions, 2,000 eligible transactions and 100 recovery labels, with two warmups and ten samples. Normal median/p95 were 1,666.899/1,815.703 ms. Adversarial K_max (2,323 discovered outputs per sample) median/p95 were 2,505.377/2,591.922 ms. Preparation reduced normal p95 by 60.5% against the initial measurement. The adversarial initial run had only three samples, so its percentage comparison is weaker.

The measurements include parsing and scanner mathematics with preloaded prevouts, excluding RPC/database. The adversarial cloud result meets the 60 s budget. The normal cloud result is above 1 s; the Mac reference-machine goal remains unverified. Raw results are in [scanner-normal-cloud.json](proofs/silent-payments/scanner-normal-cloud.json) and [scanner-adversarial-cloud.json](proofs/silent-payments/scanner-adversarial-cloud.json).

## Pending final validation

- Refreshed Application targeted tests after the fixture-only correction.
- Three consecutive green full Core regtest runs after the channel-funding fixture correction, including encrypted-key-file restore into an empty database, actual silent-payment channel inputs, spend/reorg/reconfirmation, accounting and independent interoperability.
- Release net11, native backend compilation, formatting and solution configuration checks.

Optional remote/light scanning (NL-1268) and mainnet activation/canary (NL-1270) remain separate work. No live-node configuration is changed by this branch.
