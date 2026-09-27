# BOLT 12 test vectors

These files are verbatim copies of the official BOLT 12 JSON test vectors from
[lightning/bolts](https://github.com/lightning/bolts/tree/master/bolt12).

- Upstream commit: `1aadb719b4007c4cea0ba6e36b08c4fb53788dee` (master, fetched 2026-09-27)
- Source: `https://raw.githubusercontent.com/lightning/bolts/1aadb719b4007c4cea0ba6e36b08c4fb53788dee/bolt12/<file>`

| File | Contents | Tests |
|---|---|---|
| `format-string-test.json` | 12 strings: case, `+` continuation and whitespace rules | `Bolt12VectorTests` (format strings) |
| `offers-test.json` | 53 offers: 20 valid with their expected `fields`, 33 invalid | `Bolt12VectorTests` (offers) |
| `signature-test.json` | 4 Merkle trees (leaves, nonce leaves, branches, root) incl. a full `invoice_request` with its signature | `Bolt12VectorTests` (Merkle); the signature is lane B12-B's |

`payer-proof-test.json` is not copied: payer proofs are out of scope (BOLT 12 plan §1.9).

No official vector exists for an `invoice` or `invoice_error` round trip. Plan task B0-T4 adds CLN-captured
bytes to `test/NLightning.Tests.Utils/Vectors/Bolt12Vectors.cs` (TODO: captured by the B12-E Docker proof).

Do not edit these files. To refresh them, re-download from a newer upstream commit and update the SHA above.
The typed loader lives in `test/NLightning.Tests.Utils/Vectors/Bolt12Vectors.cs`.
