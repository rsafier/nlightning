# Silent payments validation

Branch: `wip/silent-payments`, based on `wip/fafo` at `a608db64`.

This record distinguishes completed runtime checks from pending acceptance gates. All cluster proofs use the repository's `scripts/run-cluster.sh` wrapper and owned namespaces. The owner requested normal Bitcoin Core for proofs; rbitcoin is not required or used.

## Completed Core preflight

Run `sp-core-preflight1` used Bitcoin Core 31.1 and the independent, pinned BIP 352 v1.1.1 Python reference implementation. Both ZMQ and polling modes demonstrated payments from the reference wallet to NLightning and from NLightning to the reference wallet, verified on chain.

All three prevout routes agreed on actual P2PKH, P2SH-P2WPKH, P2WPKH, P2TR key-path, P2TR script-path and P2WSH inputs, including an in-block child. REST and `getblock` also passed without `txindex`. Pruned history was refused with the actual prune floor. The authentic response is retained in `test/NLightning.Infrastructure.Bitcoin.Tests/Wallet/Fixtures/core31-prevouts.json`, with wire hashes and image identity.

The full preflight failed: both node modes timed out after invalidation while awaiting a smaller tip. The revised proof mines empty replacement blocks before asserting rollback and then explicitly reconfirms the disconnected withdrawal. A successful preflight interoperability check does not count as a green full proof.

## Pending final validation

- Rebuilt unit and integration suites after the final fixes.
- Three consecutive green full Core regtest runs, including encrypted-key-file restore into an empty database, spend/reorg/reconfirmation, accounting and independent interoperability.
- PostgreSQL runtime migration and metadata round trip; SQLite runtime coverage and all three provider model guards.
- Final optimized scanner measurements, with workload and hardware limitations reported.
- Release net10/net11, native backend compilation, formatting and solution configuration checks.

Optional remote/light scanning (NL-1268) and mainnet activation/canary (NL-1270) remain separate work. No live-node configuration is changed by this branch.
