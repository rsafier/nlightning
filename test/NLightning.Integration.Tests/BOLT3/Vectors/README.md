# BOLT 3 test vectors

Copies of the test vectors in [lightning/bolts `03-transactions.md`](https://github.com/lightning/bolts/blob/master/03-transactions.md).
The spec wins: when a vector here and the spec disagree, re-copy it from the spec.

| File | Spec section | Upstream commit | Loaded by |
|---|---|---|---|
| `appendix-c.txt` | Appendix C: Commitment and HTLC Transaction Test Vectors (verbatim) | `1aadb719b4007c4cea0ba6e36b08c4fb53788dee` | `Bolt3SpecVectors` |
| `appendix-f.json` | Appendix F: Commitment and HTLC Transaction Test Vectors (anchors) (verbatim) | `1aadb719b4007c4cea0ba6e36b08c4fb53788dee` | `Bolt3SpecVectors` |
| `appendix-g.json` | Appendix G: Dual Funded Transaction Test Vectors (values transcribed, see below) | `1aadb719b4007c4cea0ba6e36b08c4fb53788dee` (master on 2026-09-27; the last commit touching `03-transactions.md` is `444805d12ab98c30006173bb190cd9d6fce9e405`) | `AppendixGVectorTests` |

## Appendix G transcription

Appendix G is prose with inline blocks, so `appendix-g.json` holds its values as JSON, each copied as written:

- `parent_tx`: "Parent transaction (spends coinbase of block 1)"; `parent_txid` is the txid both inputs name.
- `locktime` 120, `feerate_per_kw` 253, both `funding_satoshi` 200,000,000.
- `inputs`: the opener's `tx_add_input` (serial_id 20, prev_vout 0, the P2WSH hash-lock input) and the accepter's
  (serial_id 11, prev_vout 2, the P2WPKH input with its private key); `witness_data` is the one each side's
  `tx_signatures` carries.
- `outputs`: the opener's `tx_add_output`s (serial_id 30, change; serial_id 44, the 2-of-2 funding output) and the
  accepter's (serial_id 33, change). The spec prints `script` with its length byte in front (`16...`, `22...`);
  the JSON holds the script without it.
- `expected_*_fee_satoshis`: "Expected Fee Calculation" (the spec prints `contributor_weight = 395` then multiplies
  398; the 100 sat result is what the change output pays).
- `unsigned_tx`, `txid` and `signed_tx`: "Unsigned Funding Transaction", the `tx_signatures` txid and "Signed Funding
  Transaction".