# Trampoline Routing: Implementation Plan for NLightning

**Status (2026-10-03, `wip/fafo` @ `6188986`):** the plan is written and wave tr1 has started. Lanes TR0, TR1 and TR3-P run in parallel worktrees.

**Spec source:** lightning/bolts PR #836, "Trampoline onion format (Feature 56/57)".
- Author t-bast, branch `trampoline-onion`, head `8f5f37a8`, last rebased 2026-08-28, open.
- It changes `04-onion-routing.md`, `09-features.md` and `12-offer-encoding.md`, and adds three `bolt04/` vector files.
- The high-level overview is PR #829.
- The PR is not merged. Every number below is tracked against `8f5f37a8` (risk R1).

**Ledger:** NL-875 (epic).

**Relation to other plans:** builds on `ONION_ROUTING_PLAN.md`: M1-M4 (Sphinx, error onions, `HtlcSwitch`, `PaymentService`), M5 (route blinding) and M3b (attribution_data). Also builds on `BOLT12_PLAN.md` B12 (offers, blinded payment paths).

**Owner decisions (2026-10-03):**
- Spec wire format only (D-TR1).
- Blinded and BOLT 12 trampoline are in the first wave.
- Proofs are the three spec vector files byte-exact, plus in-process multi-node harness tests. **No Docker:** the Docker suites are being removed on another branch, except Tor.
- The work lands directly on `wip/fafo` (the cloud session can push only there; sync before every push, never force-push).

**Out of scope for this plan:**
- The Eclair/Phoenix/Electrum prototype format: bits 148/149 and 150/151, TLVs 66097-66102, errors NODE|51/52.
- Trampoline to legacy (non-trampoline) BOLT 11 recipients. The spec forbids it.
- Interop proofs against Eclair (after Eclair PR #2819 ships) and LDK. These are follow-ups under NL-875.

## 0. How to use this document (agents)
**Order:** TR0 contracts first. Then TR1 (crypto), TR2 (target), TR3 (relay) and TR4 (client). Then the TR5 proofs. §5 has the waves and lanes.

**Definition of done for every task:**
- `dotnet build NLightning.sln -c Release -p:MSBuildWarningsAsMessages=MSB4121` with 0 CS warnings, plus `-c Release.Native` when `src/NLightning.Infrastructure` changes.
- `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`.
- `dotnet test --no-build -c Release -f net10.0 --filter 'FullyQualifiedName!~Docker'`.
- The ledger updated in the same commit.
- No Docker or cluster suites, and no new Docker tests.

**Commits:** `trampoline: <what> (NL-875 / TRn-Tm)`.

**Schema:** owned by TR3-P (`AddTrampolineRelays`, all three providers, compiled models regenerated).

**Safety gate:** `Feature.OptionTrampolineRouting` stays in `FeatureOptions.ExperimentalFeatures` until TR5 passes and the owner decides. While the feature is off, a payload carrying TLV 20 is refused as today: TLV 20 is an unknown even type, so the answer is `invalid_onion_payload`.

## 1. Spec requirements summary
### Features
| ID | Requirement |
|---|---|
| TR-R-01 | Bits 56/57 `trampoline_routing`. Contexts I, N and 9, plus the BOLT 12 invoice features. |
| TR-R-02 | "If it doesn't support `trampoline_routing`: MUST report a route failure to the origin node." |

### Onion format
| ID | Requirement |
|---|---|
| TR-R-03 | The outer final-hop TLV **20** `trampoline_onion_packet` = `[byte version][point public_key][...*byte hop_payloads][32*byte hmac]`. Variable size; the length of hop_payloads = TLV length − 66. "MUST include the `trampoline_onion_packet` tlv in the _last_ hop's payload of the `onion_packet`." |
| TR-R-04 | Same Sphinx construction as `onion_packet`, with associated data = payment_hash. "MUST use a different `session_key` for the `trampoline_onion_packet` and the `onion_packet`." Trailing filler is recommended when there are few hops. |
| TR-R-05 | Payload TLVs **14** `outgoing_node_id`, **21** `recipient_features` and **22** `recipient_blinded_paths` (`payment_blinded_path` = blinded_path + blinded_payinfo). Non-final hops: "MUST include `short_channel_id` or `outgoing_node_id`". Final hop: "MUST NOT include `short_channel_id` nor `outgoing_node_id`". |

### Sender
| ID | Requirement |
|---|---|
| TR-R-06 | BOLT 11 payer: "MUST include the invoice's `payment_secret` in the _last_ trampoline hop's payload". Use MPP to the first trampoline with "a random `payment_secret` … MUST NOT use the invoice's `payment_secret` in the outer onion". Without the recipient's bit: "MUST NOT use trampoline routing to pay that invoice." |
| TR-R-07 | BOLT 12 payer, recipient has the bit: the blinded hops become trampoline hops. "MUST include the `encrypted_recipient_data`. For the first node in the blinded route: MUST include the `current_path_key` … For the final node: MUST include `amt_to_forward`, `outgoing_cltv_value` and `total_amount_msat`. MUST NOT include any other field." The payer may prepend trampoline hops (2, 4 and 14). |
| TR-R-08 | BOLT 12 payer, recipient without the bit: the last trampoline's payload "MUST include a subset of the invoice's blinded paths in `recipient_blinded_paths`. MUST NOT include `outgoing_node_id`. SHOULD include the invoice features in `recipient_features`." |

### Intermediate and final nodes
| ID | Requirement |
|---|---|
| TR-R-09 | Intermediate: "MUST wait to receive all the payment parts before forwarding". Reject when outer `outgoing_cltv_value` < trampoline `outgoing_cltv_value`. Reject when outer `total_msat` < trampoline `amt_to_forward`. "MUST compute a route to the next trampoline node." "MUST include the peeled `trampoline_onion_packet`". With MPP: "MUST generate a random `payment_secret`". |
| TR-R-10 | Intermediate with `encrypted_recipient_data`: `current_path_key` is in exactly one of the trampoline onion (we are the introduction node) or the outer onion. Validate the content as for a non-trampoline hop. Put the next `current_path_key` in the next node's payload. |
| TR-R-11 | Intermediate with `recipient_blinded_paths`: "MUST forward the payment using the blinded paths provided. MAY use features included in `recipient_features`." |
| TR-R-12 | Final: the same two rejections as TR-R-09. Amount, CLTV, total, secret and metadata come from the trampoline payload. |

### Errors
| ID | Requirement |
|---|---|
| TR-R-13 | New failures: NODE\|25 `temporary_trampoline_failure` (0x2019). NODE\|26 `trampoline_fee_or_expiry_insufficient` (0x201A, `u32 fee_base_msat, u32 fee_proportional_millionths, u16 cltv_expiry_delta`). PERM\|27 `unknown_next_trampoline` (0x401B). |
| TR-R-14 | Relay error wrapping: decrypt the downstream error with our forward-path keys. An error from before the next trampoline may be replaced by our own. Otherwise obfuscate twice: our trampoline `ammag` first, then our outer `ammag`. Blinded trampoline paths: the introduction node replaces errors; the others send `invalid_onion_blinding`. |
| TR-R-15 | Origin: decrypt with the outer secrets first. If no `hmac` matches, continue with the trampoline secrets. |

### Vectors
| ID | Requirement |
|---|---|
| TR-R-16 | `bolt04/trampoline-payment-onion-test.json`, `trampoline-onion-error-test.json` and `trampoline-to-blinded-path-payment-onion-test.json`, all byte-exact. |

## 2. Current state (verified at `6188986`)
### 2.1 What exists
**Sphinx:**
- `ISphinxService.Construct`, `ConstructWithSharedSecrets`, `Peel` and `PeelAsLocalNode` take `hopPayloadsLength`.
- `OnionPeeler` keeps the incoming packet's length.
- `OnionPacket(ReadOnlySpan<byte>, int hopPayloadsLength)`.
- `OnionMessageUnwrapper` parses variable packets.
- The crypto needs no change.

**Hop payloads:**
- `HopPayload`, `OnionPayloadTlvTypes` and `KnownTypes` {2, 4, 6, 8, 10, 12, 16, 18}.
- `HopPayloadSerializer`.
- `HopPayloadValidator`: it rejects unknown even types.

**Failures:**
- `FailureCode` (2..24), `FailureMessage`.
- `IFailureOnionService` (create, wrap, decrypt).
- `IAttributionDataService`, `FailureInterpreter`, `PaymentRetryPolicy`.

**Receive:**
- `IncomingOnionProcessor`: its outer-final classification and dummy-hop re-peel loop.
- `FinalHopProcessor`.
- `HtlcSwitch.ReceiveAsync`, the in-memory `HtlcSet`, the MPP timer.
- `KnownPreimage`, the settle commit point (NL-316, NL-322, NL-323).

**Forward:**
- `HtlcSwitch.ForwardAsync` and `ForwardCircuitModel`. They are strictly 1:1.
- `HtlcOrigin` with `HtlcOriginKind` `Local=1` and `Forwarded=2`, persisted on `Htlcs`.

**Send:**
- `PaymentService`: `RunSessionAsync`, `PayBlindedAsync`, `PayKeysendAsync`, reconciliation at startup.
- `PaymentRoutePlanner`.
- `PaymentOnionFactory`: it has no hook for extra final TLVs.
- `Payments` and `PaymentParts`, with per-hop shared secrets.

**Features:**
- `Feature`, `FeatureSet` contexts and dependencies, `FeatureOptions`; `ExperimentalFeatures` is empty.
- BOLT 11 invoice features are set in `InvoiceService`.
- `GraphNode.Features`.

**Pattern for a cross-cutting payment feature:** keysend (NL-459).

### 2.2 Gaps
- No trampoline TLVs, failure codes, feature bit or inner-payload validator.
- No trampoline onion service, and no two-layer error crypto.
- No N:M relay engine.
- No relay persistence, and no HTLC origin for a relay's outgoing HTLCs.
- No trampoline send path or budget logic.
- No invoice advertisement of the bit.

## 3. Design
### TR0: contracts and codecs (Domain, Infrastructure, Serialization)
**Feature bit:**
- `Feature.OptionTrampolineRouting = 57`, with contexts Init, NodeAnnouncement and Invoice, plus the BOLT 12 invoice context.
- Depends on `PaymentSecret` and `VarOnionOptin`.
- A `FeatureOptions.OptionTrampolineRouting` property, default No, kept in `ExperimentalFeatures`.

**TLVs:**
- Constants 14, 20, 21 and 22.
- Typed TLVs: `OutgoingNodeIdTlv`, `TrampolineOnionPacketTlv` (raw, variable), `RecipientFeaturesTlv`, `RecipientBlindedPathsTlv`. The last reuses `BlindedPaymentPath`/`BlindedPayInfo` and the B12 codecs.
- Converters, the serializer map, and `HopPayload` properties.

**Failure codes:** 0x2019, 0x201A and 0x401B, with data layouts and factories, and `FailureInterpreter` rows.

**Validators:**
- `HopPayloadValidator` takes an opt-in that accepts TLV 20 on the outer final hop. Without the opt-in, behavior is unchanged.
- The new `TrampolinePayloadValidator` checks the inner payload rules (TR-R-05, -07, -08, -10) and the cross-onion rejections (TR-R-09, -12).

### TR1: trampoline onion crypto (Infrastructure.Bitcoin)
**`ITrampolineOnionService`** (Domain port):
- `Build(hops, sessionKey, paymentHash, sizePolicy)` returns the packet and the trampoline shared secrets.
- `Peel(tlvValue, paymentHash, pathKey?)`.

**Size policy (D-TR3):**
- `Auto` pads hop_payloads to 650 bytes (LDK's default) when the payloads fit. Otherwise it uses the exact size, never above the maximum that keeps the outer final hop inside 1300 bytes.
- `Exact` is used for the vectors.

**Errors:**
- `CreateTrampolineErrorPacket` and `WrapTrampolineErrorPacket`: trampoline then outer obfuscation.
- `DecryptTrampolineErrorPacket(outerSecrets, trampolineSecrets, packet)` returns the layer, the index and the message.
- attribution_data: as the PR text says; otherwise outer hops only (risk R2).

**Vectors:** every stage of the three files.

### TR2: target (final trampoline recipient)
**Onion processing:**
- `IncomingOnionProcessor`: an outer-final payload that carries TLV 20 (feature on) is peeled with the node key.
- If the inner layer is final, the result is `IncomingOnionTrampolineFinal(outerSecret, outerPayload, trampolineSecret, innerPayload, blinded?)`.
- Validation follows TR-R-12.

**Payment set (D-TR4):** each HTLC counts toward the **inner** set (inner total and inner secret, keyed by payment hash). This is Eclair's model.
- `FinalHopProcessor` sees a merged payload: the outer amount, plus the inner total, secret, metadata and CLTV.

**Switch:** final-hop failures from `HtlcSwitch` are double-wrapped (the trampoline secret, then the outer secret).

**Invoices:** BOLT 11 and BOLT 12 invoices carry bit 57 when the feature is on.
- Blinded final: our BOLT 12 invoice paths double as trampoline hops; the existing M5 receive logic decrypts the inner `encrypted_recipient_data`.

### TR3: intermediate trampoline (relay)
A relay is N incoming HTLCs → one outgoing payment of M HTLCs. It is its own engine, `Application/Payments/Trampoline/TrampolineRelayService` (D-TR5).

**Persistence (TR3-P):**
- `TrampolineRelays`, keyed by payment hash. Columns:
  - status: Collecting, Sending, Fulfilled or Failed
  - next node, next blinded data, recipient features and blinded paths
  - the peeled next packet
  - amount and CLTV out, incoming total, fee, outgoing secret, preimage, failure, timestamps
- `TrampolineRelayParts`: channel, HTLC, amount, CLTV, outer and trampoline secrets.
- `PaymentTrampolineHops`, used by TR4.
- `Payments.IsTrampolineRelay`.
- `HtlcOriginKind.Trampoline = 3` and `HtlcOrigin.Trampoline(hash)`. The switch sends events for origin 3 to `ITrampolineHtlcHandler` and never to the local-payment handlers.

**Flow:**
1. **Lock-in.** `IncomingOnionTrampolineRelay` saves the part under the payment-hash lock and starts the MPP timer. A replay finds the saved part.
2. **Set complete.** Check `TrampolineRelayPolicy` (`Node:Trampoline:FeeBaseMsat`, `FeeProportionalMillionths`, `CltvExpiryDelta`, `MaxRelaysInFlight`). The fee must satisfy sum in − amount out ≥ fee(amount out). The CLTV must satisfy min CLTV in − CLTV out ≥ delta and CLTV out > height. Otherwise every part fails with NODE|26 carrying our policy.
3. **Outgoing leg.** `PaymentService` starts it, non-blocking:
   - The target is `outgoing_node_id`, the blinded paths (22), or the next blinded hop.
   - The final CLTV is absolute, and the first-hop CLTV is ≤ min CLTV in − our plain forwarding delta (`Node:Routing:CltvExpiryDelta`; TR5: the trampoline delta pays for the route).
   - MaxFee = sum in − amount out (TR5: our fee pays for the route, as Eclair; we keep what it leaves).
   - The final-payload hook adds TLV 20, a random outer secret and the total.
   - Split only when the next node is a trampoline or `recipient_features` has `basic_mpp`.
   - The outgoing HTLCs carry origin 3.
4. **Fulfill.** One save sets `KnownPreimage` on every incoming part, then every part is fulfilled at once.
5. **Failure.** An error from the next trampoline is double-wrapped per part. An error from an outer hop becomes our own `temporary_trampoline_failure` or `unknown_next_trampoline`. Blinded paths follow the TR-R-14 rules.
6. **Restart.** Collecting relays get their timer back; Sending relays are resolved by the payment reconciliation and the switch replays.

**Consumers of origin 3:**
- `HtlcExpiryMonitor`, `HtlcUpstreamOutcomeReader`, `OnchainResolutionExecutor`, `RemoteCommitResolver`, `FinalHopClaims`, `DustExposureHtlcSwitch`.
- Accounting: `TrampolineRelaySettled`, booked as relay income. The outgoing leg is never our spend.
- `listforwards`: rows of kind `trampoline`.

### TR4: client (payer)
**Selection:** `Node:Payments:Trampoline` = `Never` (default) / `Auto` / `Always`, and `payinvoice`/`payoffer --trampoline <node>` (a new key on the existing requests).
- The trampoline is a peer or graph node advertising bit 57.
- `Auto` uses it when the planner finds no route or the graph is off or empty.

**Recipients:**
- BOLT 11 with the bit: inner route [T, recipient].
- BOLT 11 without the bit: refused.
- BOLT 12 with the bit: blinded hops as trampoline hops.
- BOLT 12 without the bit: TLVs 22 and 21 in T's payload.
- The payee is T: a plain payment.

**Outer leg:** to T, MPP over our channels or routes, with a random outer secret. The same trampoline onion goes in every part of an attempt.

**Budget (D-TR6):**
- Start from T's policy cached from an earlier NODE|26 (per node), else 1000 msat + 1000 ppm, CLTV delta 576.
- On NODE|26, retry with the returned values within MaxFee.
- On `temporary_trampoline_failure`, one retry with a higher budget.
- Attempts are capped.

**Errors and persistence:**
- Two-layer decryption. Interpretation runs against the inner route for trampoline-layer errors and against the outer route (with mission control) otherwise.
- `PaymentTrampolineHops` stores the inner secrets of each attempt.

### TR5: proofs (fast tests)
- The TR1 vectors.
- An in-process harness `Application.Tests/Payments/Trampoline/` on the `ThreeNodeSwitchTests` and `PaymentHarnessTests` patterns (SQLite restarts). Topology A → T → X → C. Scenarios:
  - BOLT 11 single part, then MPP on both legs
  - NODE|26 refusal and retry
  - C's error read by A
  - T restarted while Collecting and while Sending
  - T paying `recipient_blinded_paths`
  - A paying C's BOLT 12 invoice through blinded trampoline hops
  - CLTV, fee and in-flight refusals, and the MPP timeout
- Unit tests for every origin-3 consumer.

## 4. Decisions
| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D-TR1 | Spec format only (56/57, TLV 14/20/21/22, NODE\|25/26, PERM\|27) | Owner, 2026-10-03. LDK main speaks it, and Eclair moves to it with #2819 | Prototype 148/149 for Phoenix/Electrum relays: a moving target, and it reveals the recipient |
| D-TR2 | One feature switch covers advertising, relaying and receiving | The bit promises relaying (TR-R-02); limits apply on top | Advertise but refuse relays |
| D-TR3 | Trampoline onion padded to 650 bytes when the payloads fit, else exact up to the 1300-byte budget | Privacy (size hides the hop count) and LDK's default | Always exact, as Eclair does (leaks the hop count); fixed 400 (lightning-kmp; too small for blinded paths) |
| D-TR4 | At the target, the set is counted against the inner total | Supports MPP split across several trampoline legs (Eclair) | Two-level sets per outer secret |
| D-TR5 | The relay is a payment-backed engine (origin 3, `TrampolineRelays`) | N:M does not fit 1:1 circuits; reuses `PaymentService` retries and MPP | N:M forward circuits |
| D-TR6 | Defaults: relay fee 1000 msat + 1000 ppm, CLTV delta 576. Client starts from a cached NODE\|26 policy | Covers typical routes; NODE\|26 tells us the exact price | A fee ladder blind to the trampoline's policy (Eclair's +0.2 % per attempt) |
| D-TR7 | `Payments:Trampoline` = `Never`, and the feature is experimental until TR5 and an owner decision | Real-funds safety | On by default |

## 5. Milestones, waves and lanes
| Wave | Lane | Owns | Depends on |
|---|---|---|---|
| tr1 | TR0 contracts | Domain feature, TLVs, failure codes, `HopPayload`, validators; Infrastructure converters; serializer | — |
| tr1 | TR1 crypto | `Infrastructure.Bitcoin/Onion/Trampoline/`, the trampoline error crypto, vectors | — |
| tr1 | TR3-P persistence | `HtlcOriginKind.Trampoline`, relay models, repositories, migration `AddTrampolineRelays`, origin-3 consumers, accounting kind, `ITrampolineHtlcHandler` seam | — |
| tr2 | TR2 target | `IncomingOnionProcessor` trampoline classification (final and relay results), switch receive, invoice features | tr1 |
| tr2 | TR3 relay engine | `Payments/Trampoline/TrampolineRelayService`, switch relay dispatch, policy options, `listforwards` | tr1, and the TR4 leg API |
| tr2 | TR4 client | `PaymentService` trampoline send, the leg API for TR3, `PaymentOnionFactory` final-TLV hook, IPC keys | tr1 |
| tr3 | TR5 proofs | `Application.Tests/Payments/Trampoline/` harness | tr2 |

No new `ClientCommand` is needed. New request and response keys go on `payinvoice`, `payoffer` and `listforwards`.

## 6. Traceability
| Requirement | Code | Test |
|---|---|---|
| TR-R-01, -02 | TR0 feature, TR0 validator opt-in | Domain feature and validator tests |
| TR-R-03, -04, -16 | TR1 | Infrastructure.Bitcoin vector tests |
| TR-R-05, -07, -08, -10 | TR0 TLVs and validator | Domain and Serialization tests |
| TR-R-06 | TR4 | TR5 harness |
| TR-R-09, -11, -14 | TR3 | TR5 harness, relay unit tests |
| TR-R-12 | TR0 validator, TR2 | TR5 harness |
| TR-R-13, -15 | TR0, TR1, TR4 | Vectors and TR5 harness |

## 7. Interop notes
| Implementation | What it speaks today |
|---|---|
| Eclair master, Phoenix (lightning-kmp), Electrum | Prototype only: 148/149 (Electrum also 150/151), TLVs 66097-66102, NODE\|51/52 |
| Eclair PR #2819 (draft) | Moves to the spec values and keeps the legacy bit for about six months. Our Eclair harness pins 0.14.3, which is prototype only; bump it when #2819 ships |
| LDK main | Spec bits; receives; sends only to blinded recipients. A candidate follow-up proof through the `ldk` harness once its ldk-server exposes trampoline |
| LND, CLN | Nothing |

## 9. Risks
- **R1:** the PR is still open and may change. Track it against `8f5f37a8`; the vectors catch drift.
- **R2:** how attribution_data interacts with trampoline layers is not fully specified in the vectors.
- **R3:** a relay holds N incoming HTLCs while it routes, which costs liquidity and invites griefing. Mitigations: `MaxRelaysInFlight`, the MPP timeout, CLTV margins, the experimental gate.
- **R4:** the onion size can leak privacy (D-TR3).
- **R5:** no proof against another implementation in this pass; the spec vectors are the interop evidence.
