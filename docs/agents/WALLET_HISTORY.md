# Durable wallet history recovery

`wallethistory` indexes a bounded range of retained Bitcoin blocks into `WalletTransactions` for LND-compatible `GetTransactions`. It is idle until explicitly requested. It does not change balances, UTXOs, reservations, accounting facts, the accounting cutover, or the live chain cursor.

```sh
nltg-cli wallethistory
nltg-cli wallethistory --from-height 101 --to-height 250 --address-count 30
nltg-cli wallethistory --cancel
```

Use the wallet's actual birthday as `--from-height`. Both bounds are inclusive. Omit `--to-height` to freeze the upper bound at the current chain tip when the request is accepted. `--address-count` bounds public ordinary-address discovery to that many indices on each of the P2WPKH/BIP86 receive/change chains; the default is 30 and the maximum is 100,000. Addresses already recorded by the wallet are included even above that discovery bound. This command can stage public address catalogue records, but never reserves an address or creates spendable custody.

The status reports the requested birthday, actual available lower bound, fixed target, durable cursor, active flag, partial flag and last error. Each block's raw relevant transactions, ownership description and cursor commit in the same save. A restart resumes an active job from its last committed block. Cancel preserves completed history and its checkpoint. A second request while a job is active is refused; cancel before changing the bounds.

A pruned birthday fails before any job checkpoint is written. An explicitly accepted partial scan is available:

```sh
nltg-cli wallethistory --from-height 101 --allow-partial
```

Its actual lower bound is the node's `pruneheight`, and its status remains marked partial. Blocks before that bound are not recovered. Missing blocks or undo data during a job pause it with an error and leave its cursor unchanged; they are never silently skipped. Complete history requires an unpruned source retaining the requested blocks and their input prevouts. The streamed Core undo source proves input amounts without `txindex`, including transactions without taproot outputs and ordinary inputs whose creating transaction is before the requested lower bound.

Ownership includes ordinary wallet addresses and the public derived catalogue, all registered imported tapscripts, and accepted silent-payment output metadata already retained by this wallet. Imported scripts remain watch-only: indexing their transactions does not create custody or accounting income. Each owned output/input identity counts once when an imported script overlaps another history source. The default wallet's derivation catalogue is not a discovery mechanism for separately configured accounts: their already registered scripts are included, while account creation and LND account filtering retain their own rules.

For an empty database restored from a silent-payment key file, run `sprescan` from the true birthday first to discover historical silent-payment outputs and their labels. `wallethistory` consumes those ownership records; it does not perform silent-payment discovery or restore spendable coins. An incomplete address, import or silent-payment catalogue cannot prove ownership outside its explicit scope.

The live monitor serializes block confirmations and rewinds with historical writes. Its rewind unconfirms disconnected history and adjusts the history job's checkpoint in the same save. Rescanning does not replay accounting events or rewind other cursors. Operator transaction labels are stored independently of confirmation and survive reconfirmation or delayed history discovery.

`GetTransactions` reads bounded database pages of lightweight ownership projections and loads raw transactions for the selected response page. Older rows without a complete projection retain the raw fallback until a backfill supplies their full ownership; a partial replay never discards older known inputs to manufacture a positive deposit.
