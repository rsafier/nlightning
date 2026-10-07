# Silent payments validation

Branch: `wip/silent-payments`, based on `wip/fafo` at `a608db64`.

This record distinguishes completed runtime checks from pending acceptance gates. All cluster proofs use the repository's `scripts/run-cluster.sh` wrapper and owned namespaces. The owner requested normal Bitcoin Core for proofs; rbitcoin is not required or used.

## Completed Core preflight

Run `sp-core-preflight1` used Bitcoin Core 31.1 and the independent, pinned BIP 352 v1.1.1 Python reference implementation. Both ZMQ and polling modes demonstrated payments from the reference wallet to NLightning and from NLightning to the reference wallet, verified on chain.

All three prevout routes agreed on actual P2PKH, P2SH-P2WPKH, P2WPKH, P2TR key-path, P2TR script-path and P2WSH inputs, including an in-block child. REST and `getblock` also passed without `txindex`. Pruned history was refused with the actual prune floor. The authentic response is retained in `test/NLightning.Infrastructure.Bitcoin.Tests/Wallet/Fixtures/core31-prevouts.json`, with wire hashes and image identity.

The full preflight failed: both node modes timed out after invalidation while awaiting a smaller tip. The revised proof mines empty replacement blocks before asserting rollback and then explicitly reconfirms the disconnected withdrawal. A successful preflight interoperability check does not count as a green full proof.

## Unit checks and scanner measurements

The final Bitcoin build had zero warnings/errors. The full suite executed 2,370 passing tests, with three platform skips and two explicit tests not run. The sender-inclusive targeted selection passed 323 tests with the same three platform skips. This includes official BIP 352 vectors, both key parities, signing, reservations, change recovery and the captured Core responses.

The broad Application suite passed 4,473 tests before the final historical-settlement fixes. Six optional tool/benchmark/real-node checks reported unavailable; that run does not validate later source changes. The initial CLI/IPC/withdraw/Cashu selection passed 65 tests; final startup-gate and shuffled-output-index cases still need the refreshed build.

The optimized synthetic scanner measurement used Debian 13 x64, .NET 10.0.12, four logical CPUs, 4,000 transactions, 2,000 eligible transactions and 100 recovery labels, with two warmups and ten samples. Normal median/p95 were 1,666.899/1,815.703 ms. Adversarial K_max (2,323 discovered outputs per sample) median/p95 were 2,505.377/2,591.922 ms. Preparation reduced normal p95 by 60.5% against the initial measurement. The adversarial initial run had only three samples, so its percentage comparison is weaker.

The measurements include parsing and scanner mathematics with preloaded prevouts, excluding RPC/database. The adversarial cloud result meets the 60 s budget. The normal cloud result is above 1 s; the Mac reference-machine goal remains unverified. Raw results are in [scanner-normal-cloud.json](proofs/silent-payments/scanner-normal-cloud.json) and [scanner-adversarial-cloud.json](proofs/silent-payments/scanner-adversarial-cloud.json).

## Pending final validation

- Rebuilt unit and integration suites after the final fixes.
- Three consecutive green full Core regtest runs, including encrypted-key-file restore into an empty database, spend/reorg/reconfirmation, accounting and independent interoperability.
- PostgreSQL runtime migration and metadata round trip; SQLite runtime coverage and all three provider model guards.
- Final optimized scanner measurements, with workload and hardware limitations reported.
- Release net10/net11, native backend compilation, formatting and solution configuration checks.

Optional remote/light scanning (NL-1268) and mainnet activation/canary (NL-1270) remain separate work. No live-node configuration is changed by this branch.
