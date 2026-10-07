# Silent payments operator guide

Silent payments are opt-in. The node sends and receives BIP 352 version 0 addresses using its own Bitcoin node; no scanning service receives the scan key. This implementation follows BIP 352 v1.1.1 and the pinned official vectors described in [the plan](SILENT_PAYMENTS_PLAN.md).

## Enable on regtest or a signet

Set `SilentPayments:Enabled` to `true` in the node configuration. `Send` and `Receive` default to `true`; `PrevoutSource` defaults to `Auto`. Restart the daemon after changing configuration. `nltg --check-config` validates the same options as startup. Mainnet remains disabled by default and requires the separate `AllowMainnet` configuration gate.

The receive source tries `getblock` verbosity 3, then Core 31.1's REST `spenttxouts` endpoint, then indexed `getrawtransaction`. REST requires `-rest`; raw transaction fallback may require `-txindex`. A missing or pruned block pauses recovery or halts live chain processing instead of skipping possible receipts. `spstatus` reports the selected source and recovery errors.

## Receive and send

- `nltg getspaddress` prints the static, unlabeled receiving address.
- `nltg getspaddress --label sales` creates or reuses a durable operator label.
- `nltg splabels` lists labeled addresses, including reserved change label 0.
- `nltg withdraw <silent-payment-address> <amount_sat|all>` sends through the wallet's normal reservation, signing and publication path. Existing fee and accounting label options remain available.
- `nltg spstatus` reports receiving/sending gates, birthday, live/recovery checkpoints, output counts, the latest scan duration/error and unspent silent-payment outpoints with their amount, block height and label. Use the displayed transaction ID and output index for `--utxo`.

The HRP is `sp` on mainnet, `tsp` on test networks/signets and `sprt` on regtest. Receipts become selectable only after block confirmation. A silent payment is a normal P2TR output on chain, with its derived spending metadata stored locally. LND-compatible wallet history, balances and unspent outputs show the actual P2TR script/address. Channel accept/funding delivery, cooperative close and splice-out destinations require normal Bitcoin addresses; receive into the wallet and then withdraw to a silent-payment recipient.

`AvoidMixing` defaults to true: selection first tries ordinary coins, then a single silent-payment coin, then coins with the same silent-payment label. If the amount needs mixed ownership, the wallet falls back to a mixed selection and logs it. Sending uses the exact reserved input set for derivation and signature verification. Reusing a transaction with a changed input set requires fresh derivation.

## Choose exact inputs

Use repeated `--utxo <txid>:<vout>` to spend exactly the named confirmed wallet outputs, including received silent-payment coins, to either an ordinary or silent-payment address:

```
nltg spstatus
nltg withdraw <silent-payment-address> 20000 --utxo <txid>:<vout>
nltg withdraw <silent-payment-address> all --utxo <first-txid>:<vout> --utxo <second-txid>:<vout>
```

Transaction IDs use explorer/Bitcoin Core display order. A normal amount returns change; `all` sends the sum of only those named outputs minus the fee, with no change. The sender derives BIP 352 outputs from the exact reserved inputs before signing. It never adds an unchosen coin to pay the amount or fee. Unknown, duplicate, unconfirmed, channel-locked, reserved or pending-spent inputs are refused. Existing fee limits and network/feature gates still apply.

Explicit choice opts into linking the named coins and bypasses `AvoidMixing`'s automatic preferences. It does not bypass the anchors reserve: the remaining eligible confirmed coins plus this spend's change must back the reserve. For `all`, other confirmed coins must back it. A coin can appear in `spstatus` while a reservation or pending spend makes it unavailable; `spstatus` lists custody, and the withdrawal validates spend availability. Send logs report the amount, transaction ID, fee, change, input count, explicit-choice flag and required anchors reserve.

Silent-payment sends use BIP86 P2TR change by default. When `ChangeToSilentPayment` is enabled, receiving must also be enabled; change large enough for the configured receive minimum uses the wallet's reserved silent-payment change label 0. Smaller change falls back to ordinary BIP86 change so the scanner does not ignore it.

## Restore or recover a receiving gap

Keep the encrypted node key file and the original receiving birthday. A version 3 key file derives scan/spend keys from the BIP39/BIP32 seed using `m/352'/coin'/0'/{1',0'}/0`. Legacy key files remain recoverable by this node's key file, but the same keys are not recoverable by standard external silent-payment wallets; `info` and `getspaddress` report `recoverable_elsewhere: false`.

The birthday defaults to the tip at first enable. `SilentPayments:BirthdayHeight` supplies a known earlier birthday. To discover past payments explicitly:

```
nltg sprescan --from-height <birthday> --labels 100
nltg spstatus
nltg sprescan --cancel
```

Recovery labels are a bounded count of unnamed label indices, persisted with the recovery job across restart. The change label is always included. Operator labels are allocated monotonically and capped by `MaxLabels`; recovery defaults to `RecoveryLabelCount` (100). If labels were previously issued beyond that range, supply a larger count up to 100,000.

Recovery advances its own durable checkpoint. It never rewinds the global wallet/channel cursor. Historical receipts, spends and accounting facts commit with that checkpoint; currently unspent outputs are materialized only after a current-chain proof. Live scanning and recovery share a lease. Reorgs reverse disconnected facts and restore surviving spent outputs. With receiving disabled, already discovered silent-payment outputs retain spend/reorg tracking.

## Accounting and local metadata

Accounting retains the existing `external`, `wallet` and `broadcast` source classifications so the normal financial rules keep their meaning. Silent-payment receipts additionally carry `receiptSource=silent_payment`, ownership and label metadata. Label 0 alone cannot prove self-transfer: an outside sender can pay that label too. Change is classified from actual transaction ownership/publication evidence.

A seed rescan recovers silent-payment custody and ordinary P2WPKH/BIP86 wallet custody within a key-derived address catalogue, including default BIP86 withdrawal change. The catalogue begins with 30 indices on both receive/change chains, preserves a restored catalogue's existing width, and extends 30 indices beyond actually found ordinary addresses, up to 100,000 indices. Large unused address gaps require the original address catalogue or database backup. Found ordinary addresses are reserved atomically so spending their recovered coins cannot cause address reuse. Historical ordinary receipts/spends retain journal evidence; current coins become selectable only after confirmed current-chain and mempool checks. Live spending of recovery-owned coins also records custody and settlement while historical materialization is pending.

A seed rescan does not restore the original accounting database. Complete historical wallet spends receive an exact transfer/fee settlement with unknown original purpose recorded explicitly. Recovery pauses if a shared input or ordinary wallet change lacks the custody journal needed to settle it honestly; restore the original ledger before continuing. Replaying a lower threshold promotes eligible ignored outputs without making historical spent coins selectable, and corrects any affected recovered settlement through immutable reversal and replacement.

Keep an accounting/database backup to retain original cost basis, transaction purpose, labels and financial rules. A fresh restore can lack the provenance of already spent ordinary inputs; a found receipt may therefore be classified as an external equity transfer. The default financial profile books external receipts to equity transfers in, not income, and opens lots at block-time market value. That is not proof of original tax basis; custom income rules need review against the restored provenance.

Output keys, tweaks, labels, scan checkpoints and spent markers persist in the node database. Treat database backups as private wallet metadata. Scan private keys stay in the secure key owner; they are not returned through CLI, IPC, gRPC or the source interface.

## Proof status

See the PR's validation record for completed runtime and regtest evidence. A test's presence in the repository is not evidence that it passed. Optional remote/light scanning (NL-1268) and the mainnet canary (NL-1270) remain separate work.
