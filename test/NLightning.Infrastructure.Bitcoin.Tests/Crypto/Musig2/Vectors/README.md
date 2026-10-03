# MuSig2 and simple taproot channels test vectors

Verbatim copies, not edited. Loaded by `Musig2VectorKit` and run by the tests in the parent folder (NL-877, taproot
plan T0).

## BIP 327 (MuSig2 v1.0.0)

Source: `https://github.com/bitcoin/bips/tree/master/bip-0327/vectors` (fetched 2026-10-03), the official vectors of
the BIP's reference implementation `reference.py`, whose semantics `Bip327` follows line by line.

| File | SHA-256 | Tests |
|---|---|---|
| `key_sort_vectors.json` | `2389fa0c146cfd7455c643ca240ec32835dcfc916f430f50dd94d0b49c9ea16c` | `Bip327KeyVectorTests` |
| `key_agg_vectors.json` | `03c02a97e4ef3f2edfbc8e6013c127496dfcfd5889cfca60ddf009a4e9091cab` | `Bip327KeyVectorTests` |
| `tweak_vectors.json` | `80ce6385ce062644ad1f4edcb9d4797f70ddb0b74769e4099f51b3c9e6ab4aff` | `Bip327SignVectorTests` |
| `nonce_gen_vectors.json` | `2e823580fc072427f0db0f000212cc9124ad2b9dca2b58357eb65088aee4358d` | `Bip327NonceVectorTests` |
| `nonce_agg_vectors.json` | `8409e87b81ea769759598ad3ce53b277a78afffb3a490a86ce02c4d69984524b` | `Bip327NonceVectorTests` |
| `sign_verify_vectors.json` | `692eecc101f3e515c29137f05031935e1210d2a01bab91e674eb0234f095c15c` | `Bip327SignVectorTests` |
| `sig_agg_vectors.json` | `15f14c034fb2a5739d7ce638be94c5b37ea675a2e01159092dd93b59d69c3439` | `Bip327SignVectorTests` |
| `det_sign_vectors.json` | `3d4fdb64b24e31762f20830036dc0c59d39fa896649131b54b87906ffdc6e9e8` | `Bip327SignVectorTests` |

## Simple taproot channels

`simple-taproot-vectors.json` (SHA-256 `f0ff6d1f6b646b875ca4922da297a2fa7da86ed6857b1450f56788c42ea5a16e`): the test
vectors of the simple taproot channels extension BOLT (`bolt-simple-taproot.md`, merged with lightning/bolts PR 995 on
2026-05-04), fetched 2026-10-03. `SimpleTaprootMusig2VectorTests` uses its funding keys, nonces, partial signatures and
signed commitment transactions.