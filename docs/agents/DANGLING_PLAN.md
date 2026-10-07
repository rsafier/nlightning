# Integrated follow-ups

Branch: `wip/dangling`. Base: `wip/fafo` at `e47ae080`.

The authorized scope is the remaining implementation list after silent-payment integration, excluding silent-payment performance evidence. Optional light scanning and mainnet activation are separate work.

## Work lanes

| Lane | Scope |
|---|---|
| Silent payments | Exact named-input withdrawals to silent destinations, including send-all; operator instructions for `--utxo`, reserves, expanded `spstatus` and logs. |
| NL-1289 | Bounded, durable wallet-history backfill with restart/reorg/pruned-history handling and database paging. |
| NL-1283 | Interceptor compatibility, actual-versus-overridden amounts, on-chain/downstream settlement races and upstream-client proofs. |
| NL-1186 | Remaining WalletKit template funding, unconfirmed operations, leases, transaction labels and account/key-scope support. |
| NL-758 / NL-759 | Durable price-replacement audit and consistent immutable closed-period valuation/export behavior. |
| Proofs | Focused accounting/recovery/signing tests and one successful appropriate normal Bitcoin Core/LND cluster run. |

All provider migrations and compiled models are generated together after the model changes converge. No live-node configuration, funds or historical ledger corrections are part of this branch.

## Evidence

### Implemented behavior

- **NL-1298:** `withdraw --utxo` accepts silent-payment destinations for fixed amounts and send-all. Only selected eligible coins are spent; wallet reserves still apply. Received silent-payment coins participate in explicit selection. See [SILENT_PAYMENTS_OPERATOR.md](SILENT_PAYMENTS_OPERATOR.md).
- **NL-1289:** `wallethistory` starts an opt-in, bounded, durable history job. Each block's raw transactions, ownership projections and cursor commit together. Restart resumes the checkpoint; reorg repairs history under the same writer gate as the live monitor. Core undo data supplies historical input ownership without `txindex`. Pruned birthdays fail before a job is created unless partial recovery is requested. GetTransactions pages lightweight records before loading selected raw transactions. History recovery does not replay wallet custody or accounting events. See [WALLET_HISTORY.md](WALLET_HISTORY.md).
- **NL-1283:** forwarding history records interpreted overrides separately from the actual incoming value used for accounting. Expiry checks apply without a connected interceptor; overridden dust and unsupported mandatory wire records are refused. An upstream on-chain claim persists its preimage with the circuit, leaving an in-flight outgoing leg to settle or fail exactly once even after restart or removal of the incoming HTLC row. A failed claimed circuit reconstructs its original income key during accounting cutover; event-staging errors abort the live failure save.
- **NL-1186, partial:** named owned BIP84/BIP86 accounts, bounded watch-only account imports, inputless Core PSBT v0/v2 and existing-input/change templates, durable labels, explicitly requested unconfirmed funding, confirmation-depth leases, wallet CPFP and repeated child RBF. Core-proven unconfirmed coins stay outside confirmed custody. Initial and replacement fee-bump intents commit before broadcast. Package fees include all unconfirmed ancestors and replacements preserve the selected parent output and account change script. See [WALLET_ACCOUNTS_OPERATOR.md](WALLET_ACCOUNTS_OPERATOR.md).
- **NL-758 / NL-759:** price replacements retain old/new values, sources, operator and note in an atomic audit. New signed period closes include immutable prices; closed exports and cost-basis rebuilding use those prices. Legacy signatures remain byte-compatible; missing legacy valuations are reported conservatively.

### Runtime checks

All runtime results below are net10.0. The final Release solution build covers both net10.0 and net11.0. Checks cover the production fixes, generated migrations and compiled models; the last narrow recovery patch also has its own boundary regressions:

| Check | Result |
|---|---|
| Release solution build, net10.0 and net11.0 | Zero warnings and errors |
| Full formatting verification; solution configuration; whitespace | Passed |
| Application accounting/history/interceptor tests | 388 passed |
| Integration, excluding Docker/cluster/SQL Server runtime | 1,251 passed |
| LND gRPC tests | 312 passed |
| Bitcoin wallet/account/lease/CPFP and monitor affected tests | 480 passed |
| Daemon history/price/withdraw tests | 90 passed |
| Domain forwarding-circuit tests | 22 passed |
| Final strict claimed-failure staging regressions | 2 passed |
| Inputless and existing coin-select templates | 9 passed, including actual signature verification |
| Final SQLite accounting backfill/recovery | 15 passed |
| Projected-history lease cleanup and retained-spend proof | 6 passed |
| CPFP whole-vbyte package and relay rounding/replacement/intent | 9 passed |
| Final SQLite transient confirmation/rollback, UTXO and monitor accounting | 56 passed |
| All-provider migration/model guards; SQLite fresh/legacy runtime | Passed in Integration |

The final lease cleanup also hydrates only a bounded history page before checking canonical transaction hashes and confirmation depth; missing or corrupt retained history cannot prove a spend. The changed Bitcoin project builds cleanly for both targets. Core package and replacement relay pricing round each child up to whole virtual bytes, including partial-byte signed weights. Confirmations promote transient coins into durable custody and prioritize staged confirmed outputs for same-block spends; injected commit failures leave memory, custody and raw history unchanged. The final Bitcoin and Repositories builds cover both targets with zero warnings/errors.

The earlier full Bitcoin suite passed 2,434 tests with three platform skips; subsequent wallet changes were checked by the affected suite above. Broader runs are not repeated once a relevant final run passes. The real Core/LND combined acceptance run is still pending; successful fixture startup, isolated steps or PostgreSQL-only passes do not satisfy the combined acceptance gate. The additional Release.Native `PublishAot=true` analyzer build completed with zero errors and four inherited warnings: IL2026/IL3050 at the existing Newtonsoft RPC request serializer in BlockPrevoutSource, and two IL2075 reflection warnings in LndUnknownMethods. No NativeAOT publish or runtime proof is claimed.

### Supported limits and remaining work

NL-1186 stays open. Arbitrary/nested/hybrid key scopes, random coin selection and automatic LND deadline fee escalation are not implemented. Wallet CPFP handles a requested fee target; channel anchor/HTLC fee management retains its existing resolver path. Watch-only historical discovery has explicit script/block bounds and needs available Core history. A named-account wallet backup requires the database and key-origin/index metadata as well as the seed.

Wallet-history recovery cannot discover unknown silent-payment outputs: run `sprescan` first. Partial recovery on a pruned node explicitly records its missing lower range. SQL Server has migration/compiled-model guards here, but no SQL Server runtime proof is claimed. Optional remote scanning, mainnet activation, silent-payment performance evidence and changes to live-node funds/configuration remain outside this branch.
