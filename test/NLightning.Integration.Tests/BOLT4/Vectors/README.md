# BOLT 4 test vectors

These files are verbatim copies of the official BOLT 4 JSON test vectors from
[lightning/bolts](https://github.com/lightning/bolts/tree/master/bolt04).

- Upstream commit: `1aadb719b4007c4cea0ba6e36b08c4fb53788dee` (master at 2026-09-21T17:24:56Z, fetched 2026-09-25)
- Source: `https://raw.githubusercontent.com/lightning/bolts/1aadb719b4007c4cea0ba6e36b08c4fb53788dee/bolt04/<file>`

| File | Contents |
|---|---|
| `onion-test.json` | 5-hop onion construction (`generate`), expected 1366-byte `onion`, per-hop `decode` private keys |
| `onion-error-test.json` | 5-hop failure: per-hop shared secrets, `ammag`/`um` keys, erring hop payload, final `errorpacket` |
| `route-blinding-test.json` | blinded route creation (`generate`), resulting `route`, per-hop `unblind` data |
| `blinded-payment-onion-test.json` | payment onion to a partially blinded route, per-hop `decrypt` data |
| `blinded-onion-message-onion-test.json` | onion message over a blinded route, per-hop `decrypt` data |

Do not edit these files. To refresh them, re-download from a newer upstream commit and update the SHA above.
The typed loader lives in `test/NLightning.Tests.Utils/Vectors/Bolt4Vectors.cs`.
