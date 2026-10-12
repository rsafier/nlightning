# BOLT 4 trampoline test vectors (BOLTs PR 836)

Verbatim copies of the trampoline JSON test vectors of [lightning/bolts PR 836](https://github.com/lightning/bolts/pull/836)
(branch `trampoline-onion`, not merged).

- Upstream commit: `8f5f37a817ae680bc564b97e43b46195afd5869e` (head of `trampoline-onion`, fetched 2026-10-03)
- Source: `https://raw.githubusercontent.com/lightning/bolts/8f5f37a817ae680bc564b97e43b46195afd5869e/bolt04/<file>`

| File | Contents | Tests |
|---|---|---|
| `trampoline-payment-onion-test.json` | Alice's trampoline onion (Carol, Eve) inside her outer onion (Bob, Carol); every peel; Carol's new outer onion to Eve via Dave | `TrampolinePaymentVectorTests` |
| `trampoline-onion-error-test.json` | [0] Eve's failure double-wrapped, Dave's wrap, Carol's unwrap and re-wrap, Bob's wrap, Alice's two-stage decryption; [1] a trampoline payment over Eve's blinded path (Carol introduction node), Dave's malformed failure turned into `temporary_trampoline_failure` by Carol | `TrampolineErrorVectorTests` |
| `trampoline-to-blinded-path-payment-onion-test.json` | [0] Carol pays Eve's blinded path from `recipient_blinded_paths` (Eve without trampoline); [1] every blinded hop as a trampoline hop (Dave introduction node, Eve blinded) | `TrampolineBlindedPathVectorTests` |

Do not edit these files. To refresh them, re-download from a newer upstream commit and update the SHA above.
