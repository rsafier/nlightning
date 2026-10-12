# Simple taproot channel test vectors (BOLTs #995)

`simple-taproot-vectors.json` is a verbatim copy of the JSON block under "Test Vectors" in
[`bolt-simple-taproot.md`](https://github.com/lightning/bolts/blob/master/bolt-simple-taproot.md), the extension BOLT
merged with [BOLTs #995](https://github.com/lightning/bolts/pull/995) (master, fetched 2026-10-03).

| Section | Contents | Tests |
|---|---|---|
| `params` | seed, funding 10,000,000 sat, dust 354, csv 144, commit height 42, the NUMS point, every key and derived key | `SimpleTaprootScriptVectorTests` (derived keys) |
| `scripts` | funding, to_local, to_remote, both anchors, offered/accepted HTLCs, second-level outputs: leaf scripts, leaf hashes, tapscript root, internal and output keys, pkScript | `SimpleTaprootScriptVectorTests` |
| `transactions` | three commitment transactions (no HTLC; five HTLCs; two trimmed at dust 2,500) with their HTLC resolution transactions | `SimpleTaprootCommitmentVectorTests` |

Notes on reading the vectors (checked against the spec text, 2026-10-03):

- `scripts.funding.combined_key` is the BIP 86 **output** key (the pkScript's key), not the untweaked MuSig2 aggregate
  the spec calls `combined_funding_key`.
- `scripts.accepted_htlc_*` are built with the two HTLC keys swapped relative to their names
  (`local_htlcpubkey` = `derived_remote_htlc_pubkey`); the commitment vectors' accepted HTLC outputs use the spec's
  roles. Both `*_local_commit` and `*_remote_commit` entries are identical (same keys).
- §To Remote Outputs names a second NUMS point `0245b181...` and a control block with `combined_funding_key`; the
  vectors use `02dca094...` (the `nums_point` param) as the internal key of both to_local and to_remote.
- `remote_partial_sig_hex` of an HTLC resolution is a 64-byte BIP 340 signature (zero aux randomness); in the
  witness it carries the sighash byte 0x83 (`SIGHASH_SINGLE|SIGHASH_ANYONECANPAY`), the local signature none
  (`SIGHASH_DEFAULT`).

Do not edit the JSON file. To refresh it, re-extract the block from a newer spec revision.
