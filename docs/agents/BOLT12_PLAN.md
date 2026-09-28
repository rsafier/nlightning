# Onion Messages (ONION M6) and BOLT 12 Offers: Implementation Plan for NLightning

This plan covers two waves:
- **Wave M6: onion messages** (BOLT 4 "Onion Messages", BOLT 9 `option_onion_messages`). It covers the wire message, construct and peel, forwarding as an intermediate or introduction node of a blinded message path, receiving with dispatch to per-TLV handlers, reply paths, per-peer rate limits, and the feature bit, which is advertised only once proven.
- **Wave B12: BOLT 12 offers** (`12-offer-encoding.md`). It covers the TLV codecs, the bech32 string format without a checksum, Merkle signatures, offers (`createoffer`), paying an offer (`payoffer`: invoice_request, invoice, verify, and a blinded payment over the invoice's paths), answering invoice_requests, settling blinded payments with `path_id`, and persistence.

**Status (2026-09-27, `wip/fafo` @ `641a5fff`): wave M6 is done** (NL-080 fixed; Proof M6 10/10 against CLN v26.06.8; `option_onion_messages` Optional by default, D9 at 641a5fff). See "Wave M6 record" at the end of §5 for SHAs, deviations and follow-ups. **Wave B12 is next** (NL-447); its `ClientCommand` values start at **26** (25 is `withdraw`, NL-441).

**Status (2026-09-27, `wip/fafo` @ `a3445f3f`): wave B12 is integrated** (NL-447 partial). B0-T1/T2/T3/T5, B1, B2, B3 and B4-T1..T4 are done, Proof B12 receive and pay are green against CLN v26.06.8 (4/4 each), and `ClientCommand` 26-30 are `createoffer`, `listoffers`, `disableoffer`, `payoffer`, `fetchinvoice`. Open: B0-T4 CLN captures (NL-450), the expired-invoice prune timer (NL-448), reachability limits (NL-452). See "Wave B12 record" at the end of §5.

**Status (2026-09-27, `wip/fafo` @ `a6c633f9`): wave B12 is closed** (NL-447 fixed in wave lh1, lane l2). B0-T4 is done: CLN v26.06.8's offer, invoice_request, invoice and invoice_error are captured in `Bolt12ClnVectors` and checked byte-exact, and CLN's invoice is the B4-T2 positive case of `InvoiceVerifierTests` (NL-450, 3295f675). B2-T2's prune runs: `ExpiredBolt12InvoicePruner` with a grace of at least the MPP timeout (NL-448, b9faac3d, 84eef9ed, registered in a6c633f9). B1-T1 signs with fresh aux randomness (NL-455, e767fd30). OM0-T1: a malformed 513 is ignored and counted, no warning (NL-444, 8035c63b). OM1/OM2-T3/OM2-T5: one Domain codec and one path builder, the service reads through `IOnionMessageUnwrapper` (Domain) and the harness runs the production builder (NL-442, e1693d44). B0: both `Bolt12Wire` seams deleted (NL-453, 844318cc). §3.11: `Offers` template section and `listinvoices` kind/offer id (NL-454, c0587be9). Carried: NL-451, NL-452, NL-464.

Every repo claim cites a repo-relative path, verified at `wip/fafo` @ `88d046c7` unless marked otherwise. Claims marked **(unverified)** were not checked against the spec, code or a running peer. Confirm them before relying on them.

- **Spec source:** `lightning/bolts` master, fetched 2026-09-27 through the GitHub contents API:
  - `04-onion-routing.md`: §Route Blinding, §Onion Messages, the `blinded_path` and `encrypted_data_tlv` formats;
  - `01-messaging.md`: `sciddir_or_pubkey`;
  - `09-features.md`: bits 24/25, 38/39, 66/67 and the new context `B`;
  - `12-offer-encoding.md`: the whole document.

  Re-read the requirement block before you implement a handler. The payer-proof section (`lnp`, types 241 and 1001-1005) is new spec material and out of scope here (§0.8).
- **Relation to the other plans:**
  - M6 is the "M6 (optional): Onion messages" stub of `ONION_ROUTING_PLAN.md` §5, expanded here.
  - Both waves build on **M5 route blinding** (ABCD roadmap "Beyond", ledger NL-079). Wave RF1 is implementing M5 right now on `wip/fafo-rf1-m5-route-blinding-s2`, in the lanes `src/NLightning.Infrastructure.Bitcoin/Onion/**` and `src/NLightning.Application/Payments/**`.
  - M5 as seen on that branch (`a9dc7b64`, **not merged, may change**):
    - `Domain/Protocol/Onion/Interfaces/IRouteBlindingService`: `CreateBlindedPath(nodeIds, encryptedDataTlvs, sessionKey)`, `UnblindAsLocalNode(pathKey, encryptedRecipientData, pathKeySharedSecret)`, `Unblind`, `Decode/EncodeRecipientData`.
    - Domain models `BlindedPath(FirstNodeId, FirstPathKey, Hops)`, `BlindedPathHop`, `BlindedRecipientData` (types 1-14 plus unknown odd), `BlindedHopUnblinding(..., NextPathKey)` and `BlindedPathId` (`path_id = HMAC(preimage, "nltg_blinded_path_id")`).
    - `Infrastructure.Bitcoin/Onion/RouteBlinding/{RouteBlindingService,BlindedRecipientDataCodec}`, registered by `AddRouteBlindingServices()`.
    - Switch and final-hop support for **receiving and forwarding** blinded HTLCs.
  - The task brief calls the path creator `BlindedPathBuilder`; M5 ships it as `IRouteBlindingService.CreateBlindedPath`. This plan uses the M5 names.
  - **M5 does not include a blinded send** (paying *to* a blinded path): the M5 branch touches no file in `Payments/Send/` or `Payments/Routing/`. BOLT 12 needs it, so wave B12 owns it (lane B12-E, gap OG6).
- **Issue ledger:** onion messages are **NL-080** (fixed in wave M6; the M6 commits cite NL-079) and BOLT 12 offers are the epic **NL-447**; B12 lanes cite NL-447. Originally: cite **NL-079** (route blinding epic, the parent of this work) until the ledger agent files the new epics. New gaps found while writing this plan are the `OG#` rows in §2.2, marked "new". The next free ID at `88d046c7` is **NL-426**, and RF1 may take some. Tasks say "Resolves OG#" until IDs are assigned, and each fix updates its ledger entry in the same commit.
- **Out of scope for both waves:**
  - payer proofs (`lnp`, `payer-proof-test.json`);
  - recurrence (not in the spec; CLN's own format changed incompatibly in 25.12);
  - `offer_currency` other than BTC: we parse and store such offers, but refuse to pay them unless the caller passes an explicit msat amount, and we never create them;
  - the "refund" flow, an `invoice_request` without an offer (`lnr` strings), and `sendinvoice`: the codec parses and validates `lnr` with the vectors, but there is no IPC;
  - `invreq_bip_353_name` resolution: the field is kept, validated for charset and copied, but no DNS;
  - async payments and static invoices;
  - `option_onion_messages_only_channels` (66/67): we never advertise it, and we accept messages from peers without a channel within the rate limits;
  - dummy hops in our blinded paths (MAY);
  - onion-message pathfinding across more than one unknown hop.

---

## 0. How to use this document (agents)

1. **Order of work.** Wave M6 must be integrated (merged to `wip/fafo`, proofs green) before wave B12 starts. Wave M6 needs RF1's M5 merged for `IRouteBlindingService`. Each wave starts with a **contracts commit by the integrator** (§5 "Contracts first"): the Domain interfaces and records that the lanes code against. The lanes then fork from that commit.
2. **Definition of done for a task:**
   - `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121` and `-c Release.Native` (both waves touch crypto);
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`;
   - `dotnet test --no-build -c Release -f net10.0 --filter 'FullyQualifiedName!~Docker'`;
   - the task's own tests;
   - no new CS warnings (root `CLAUDE.md`).
3. **Definition of done for a milestone:** its **Proof** passes.
   - Spec vectors are byte-exact, in `test/NLightning.Integration.Tests/BOLT4/` (M6) and a new `BOLT12/` folder (B12).
   - The Docker proofs run against CLN in `test/NLightning.Integration.Tests/Docker/Interop/Cln/` (`ClnFixture`, collection `cln-interop`, `[Trait("Category", "Interop.Cln")]`). They run from the host process like the other CLN tests (`test/CLAUDE.md`).
   - NLightning-to-NLightning proofs go through `NLightningTestNode`.
4. **Schema.** Wave M6 has **no migration**. Onion messages are stateless, and reply correlation is kept in memory (D4). Wave B12 has exactly one migration, `AddBolt12Offers`, owned by lane **B12-C**, on all three providers through `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh`. Build the three provider projects in Debug first (NL-233). `HasPendingModelChanges` must stay false for all three.
5. **DI.** Register everything in the owning layer's `DependencyInjection.cs` or in a `*ServiceCollectionExtensions` called from it:
   - `AddOnionMessageServices()` (Application), called from `AddApplicationServices`;
   - `AddOffersServices()` (Application);
   - Bitcoin-side services in `AddBitcoinInfrastructure`.

   The Docker node uses `AddNltgNodeServices` (NL-156), so nothing is mirrored by hand.
6. **Safety gates:**
   - `OptionOnionMessages` stays in `FeatureOptions.ExperimentalFeatures` (`src/NLightning.Domain/Node/Options/FeatureOptions.cs:24-30`) and defaults to No until Proof M6 passes. Then the integrator makes it Optional by default and removes it from the experimental set (D9).
   - Offers work only when both `OptionOnionMessages` and `OptionRouteBlinding` are advertised: we cannot receive a blinded payment without M5's feature. `createoffer`/`payoffer` return "not available" otherwise.
7. **Commit shape:** one task per commit, lowercase imperative subject, citing the NL ID and the task ID (for example `(NL-079 / OM2-T1)`). Append the session trailer the integrator gives you.
8. **Unverified CLN behaviors** (§7) are verified by the proofs. When CLN differs from this plan, adjust the proof and record what CLN did. Never bend a spec rule to match CLN.

---

## 1. Spec requirements summary

Requirement IDs are used by the tasks and by the traceability matrix (§6).

### 1.1 Wire: `onion_message` (type 513) and `onionmsg_tlv`
| ID | Requirement |
|---|---|
| OM-W-01 | `onion_message` = `point path_key ‖ u16 len ‖ len*byte onion_message_packet`. It is not a channel message and is gated on `option_onion_messages` |
| OM-W-02 | `onion_message_packet` = `byte version (0) ‖ point public_key ‖ onionmsg_payloads ‖ 32*byte hmac`. The payloads length is `len - 66`, so it is not fixed at 1300 |
| OM-W-03 | `onionmsg_payloads` use the `hop_payloads` framing (bigsize length, TLV stream, 32-byte hmac). There is no legacy form, and length 0 is a valid empty payload (`OnionPacketKind.OnionMessage` already allows it: `src/NLightning.Domain/Protocol/Onion/Enums/OnionPacketKind.cs`) |
| OM-W-04 | `onionmsg_tlv`: 2 `reply_path` (blinded_path), 4 `encrypted_recipient_data`, 64 `invoice_request`, 66 `invoice`, 68 `invoice_error`. Types 64 and up are final-hop payload fields |
| OM-W-05 | `blinded_path` = `sciddir_or_pubkey first_node_id ‖ point first_path_key ‖ byte num_hops ‖ num_hops * (point blinded_node_id ‖ u16 enclen ‖ encrypted_recipient_data)` |
| OM-W-06 | `sciddir_or_pubkey` (BOLT 1) is 9 or 33 bytes. First byte 0 or 1: an 8-byte SCID follows, and the byte picks `node_id_1` (0) or `node_id_2` (1) of that channel. First byte 2 or 3: a compressed point |

### 1.2 Writer (sender) and creator of `encrypted_recipient_data`
| ID | Requirement |
|---|---|
| OM-S-01 | version 0; Sphinx with **empty associated data** |
| OM-S-02 | SHOULD set `len` to 1366 or 32834 (payloads 1300 or 32768) |
| OM-S-03 | The `onionmsg_tlv` of a non-final hop MUST contain only `encrypted_recipient_data` |
| OM-S-04 | The creator MUST NOT put `payment_relay` or `payment_constraints` in a message path's `encrypted_data_tlv`. Every non-final hop MUST get `next_node_id` or `short_channel_id` |
| OM-S-05 | To use a blinded path it was given, the sender builds unblinded hops up to the introduction node. It encrypts the first blinded hop to the first `blinded_node_id` and sets `next_path_key_override = first_path_key` in the previous hop's payload, which it built itself |
| OM-S-06 | Final hop: set `reply_path` only if a reply is allowed; the creator MAY put a secret in `path_id`. Without a reply allowed, MUST NOT set `reply_path` |
| OM-S-07 | SHOULD retry through a different path when an expected reply does not come |
| OM-S-08 | Route-blinding creator rules (M5): SHOULD pad all `encrypted_recipient_data` to the same length, and MAY add dummy hops (payment paths since NL-440; message paths not done, NL-520) |

### 1.3 Reader (every node)
| ID | Requirement |
|---|---|
| OM-R-01 | MAY rate-limit by dropping. There are **no error replies** to onion messages |
| OM-R-02 | Decrypt with empty associated data and the message's `path_key` (the Sphinx node key is tweaked by `HMAC("blinded_node_id", ECDH(path_key, k))`). On an HMAC failure, a malformed TLV, an unknown even type, or an `encrypted_recipient_data` that is missing or does not decrypt: **ignore** the message |
| OM-R-03 | `allowed_features` present: ignore the message if it holds any unknown bit, even or odd (no bit is defined for context `B` yet) |
| OM-R-04 | Non-final hop: ignore it if the `onionmsg_tlv` has anything besides `encrypted_recipient_data`, or if the decrypted data has `path_id` |
| OM-R-05 | Non-final hop: the next peer is `next_node_id`, else `short_channel_id` (announced SCID or local alias), else ignore. SHOULD forward with `path_key` = `next_path_key_override` if present, else `SHA256(E_i ‖ ss_i) * E_i` |
| OM-R-06 | Final hop: if `path_id` matches a reply_path we published but the message is not a reply to the onion that carried it, ignore it. A reply to an onion that had a `path_id` gets the same answer as if we had never sent the original |
| OM-R-07 | Final hop: ignore it if it carries more than one payload field (every type >= 64: "Field numbers 64 and above are reserved for payloads for the final hop"; wave M6 record) |
| OM-R-08 | Reply through `reply_path`: send `onion_message` to `first_node_id` with the reply path's `first_path_key` |
| OM-R-09 | A node that advertises `option_onion_messages_only_channels` MUST NOT accept messages from peers without a channel. We never advertise it. Otherwise it SHOULD accept messages from peers without a channel |

### 1.4 Features (BOLT 9)
| ID | Requirement |
|---|---|
| F-01 | `option_onion_messages` 38/39, context IN ("can forward onion messages"). `Feature.OptionOnionMessages = 39` exists (`src/NLightning.Domain/Enums/Feature.cs:137`) |
| F-02 | `option_onion_messages_only_channels` 66/67, IN, depends on 38/39. `Feature.OptionOnionMessagesOnlyChannels = 67` exists (`Feature.cs:209`) |
| F-03 | `option_route_blinding` 24/25, context IN9 (M5 gate) |
| F-04 | BOLT 12 has **no** init feature bit. Its bits live in `offer_features` (12), `invreq_features` (84), `invoice_features` (174) and `blinded_payinfo.features`. The only defined one is MPP (16/17) in invoices. Readers reject unknown even bits and ignore unknown odd ones |
| F-05 | Interop: CLN 25.12 requires `option_onion_messages` on the peers it puts in blinded paths (CLN #8682). We must advertise 39 to be used as an introduction node by CLN |

### 1.5 BOLT 12 encoding
| ID | Requirement |
|---|---|
| B12-ENC-01 | String = `hrp ‖ "1" ‖ bech32(data)` with **no checksum**. HRPs: offer `lno`, invoice_request `lnr` (payer proof `lnp`, out of scope). The spec defines **no HRP for invoices**, which travel only in onion messages. CLN's `lni` is a CLN convention **(unverified in the spec; accept it on input only)** |
| B12-ENC-02 | Writers use all lowercase (or all uppercase for QR codes), and MAY split with `+` plus optional whitespace. Readers MUST accept either case and MUST remove a `+` followed by whitespace between two bech32 characters. The other malformed cases come from `format-string-test.json` |
| B12-ENC-03 | TLV streams are strictly increasing and minimally encoded (BOLT 1). Unknown even types are rejected; unknown odd types are kept and copied (an invreq copies every offer field, unknown ones included) |
| B12-ENC-04 | Ranges: offer 1-79 and 1,000,000,000-1,999,999,999; invreq 0-159 and 1e9-2,999,999,999 (not counting signatures); invoice 0-239 and 1e9-3,999,999,999; signature types 240-1000 inclusive |

### 1.6 BOLT 12 TLV types
| Message | Types |
|---|---|
| offer | 2 `offer_chains` (chain_hash*), 4 `offer_metadata`, 6 `offer_currency` (ISO 4217 utf8), 8 `offer_amount` (tu64), 10 `offer_description`, 12 `offer_features`, 14 `offer_absolute_expiry` (tu64 s), 16 `offer_paths` (blinded_path*), 18 `offer_issuer`, 20 `offer_quantity_max` (tu64), 22 `offer_issuer_id` (point) |
| invoice_request | 0 `invreq_metadata`, 2-22 (offer copy), 80 `invreq_chain`, 82 `invreq_amount` (tu64 msat), 84 `invreq_features`, 86 `invreq_quantity`, 88 `invreq_payer_id`, 89 `invreq_payer_note`, 90 `invreq_paths`, 91 `invreq_bip_353_name`, 240 `signature` |
| invoice | 0-91 (invreq copy), 160 `invoice_paths`, 162 `invoice_blindedpay` (blinded_payinfo*), 164 `invoice_created_at` (tu64), 166 `invoice_relative_expiry` (**tu32**, default 7200), 168 `invoice_payment_hash`, 170 `invoice_amount` (tu64), 172 `invoice_fallbacks`, 174 `invoice_features`, 176 `invoice_node_id`, 240 `signature` |
| invoice_error | 1 `erroneous_field` (tu64), 3 `suggested_value`, 5 `error` (utf8). The spec's reader section is literally "FIXME!" |
| blinded_payinfo | u32 fee_base_msat ‖ u32 fee_proportional_millionths ‖ u16 cltv_expiry_delta ‖ u64 htlc_minimum_msat ‖ u64 htlc_maximum_msat ‖ u16 flen ‖ features |

### 1.7 BOLT 12 signatures
| ID | Requirement |
|---|---|
| B12-SIG-01 | Tagged hash `H(tag, msg) = SHA256(SHA256(tag) ‖ SHA256(tag) ‖ msg)`. The signature is BIP-340 over `H("lightning" ‖ messagename ‖ fieldname, merkle_root)`, for example `lightninginvoice_requestsignature` and `lightninginvoicesignature` |
| B12-SIG-02 | Merkle leaves, in ascending TLV order and excluding the signature types 240-1000: `H("LnLeaf", tlv)` paired with `H("LnNonce" ‖ first_tlv, tlv_type)`. `first_tlv` is the full encoding of the numerically first TLV, and `tlv_type` its bigsize type. Inner nodes are `H("LnBranch", lesser ‖ greater)`, ordered by value. With a leaf count that is not a power of 2, the tree is deepest on the lowest-order leaves. **The exclusion wording for 240-1000 is unverified; `signature-test.json` settles it** |
| B12-SIG-03 | Exactly one signature per invoice_request and invoice |

### 1.8 BOLT 12 behavior (the requirements the proofs and tests trace)
| ID | Who | Requirement (summarized) |
|---|---|---|
| B12-OFR-01 | offer writer | Only the allowed ranges. Omit `offer_chains` for bitcoin only. An amount requires a description, must be > 0 and needs a currency when it is not in msat. Without an amount, set neither amount nor currency |
| B12-OFR-02 | offer writer | MUST include `offer_paths` if it has only private channels. MUST set `offer_issuer_id` when there are no paths. `offer_quantity_max` 0 means unlimited; omit it for a single item |
| B12-OFR-03 | offer reader | MUST NOT respond when: a TLV is out of range; an `offer_features` even bit is unknown; the chain is unsupported; there is an amount without a description, an amount of 0, or a currency without an amount; there is neither issuer_id nor paths; a path has `num_hops` 0; the offer is expired; `offer_chains` is empty |
| B12-OFR-04 | offer reader | Send the invoice_request through an `offer_paths` path to that path's final `blinded_node_id`, else to `offer_issuer_id` |
| B12-IRQ-01 | invreq writer | Copy every offer field, unknown ones included. Set `invreq_chain` only if not bitcoin. Set `invreq_amount` when the offer has none; if set, it is >= the expected amount. Quantity per `offer_quantity_max`. Set an unpredictable `invreq_metadata`. Use a transient `invreq_payer_id` whose secret we keep, and sign with it |
| B12-IRQ-02 | invreq reader | Reject when: `invreq_payer_id` or `invreq_metadata` is missing; a type is out of range; an even feature is unknown; the signature is bad; a path has `num_hops` 0 |
| B12-IRQ-03 | invreq reader | The offer fields must exactly match a valid, unexpired offer of ours. Ignore the request if it did not arrive through one of `offer_paths`. Without `offer_paths`, ignore one that arrived through a blinded path |
| B12-IRQ-04 | invreq reader | Amount and quantity checks: expected = offer_amount × quantity; reject if `invreq_amount` < expected; MAY reject if much higher; reject if neither amount is set. Check the chain. Check the `bip_353` charset |
| B12-IRQ-05 | invreq reader | SHOULD reply with an invoice through `reply_path`. MAY resend a cached invoice only when `offer_issuer_id` is set and the metadata is identical |
| B12-INV-01 | invoice writer | Set `invoice_created_at`. Amount = `invreq_amount` or the expected amount. Copy every non-signature invreq field. `invoice_node_id` = `offer_issuer_id`, else the final `blinded_node_id` of the path the request arrived on. Exactly one signature. Set `relative_expiry` when it is not 7200 |
| B12-INV-02 | invoice writer | MUST include `invoice_paths` and one `invoice_blindedpay` per path, with `payinfo.features` = the path's `allowed_features`. Set the MPP bits per our `basic_mpp` support. SHOULD ignore payments that do not use one of the paths. Fallbacks must be valid witness programs (we set none) |
| B12-INV-03 | invoice reader | Reject when: amount, created_at, payment_hash or node_id is missing; the chain is unsupported; an even feature is unknown; it has expired; paths or blindedpay are missing or empty, a `num_hops` is 0, or the counts differ; no path is usable after dropping those whose payinfo has unknown even bits |
| B12-INV-04 | invoice reader | Fields 0-159 and 1e9-2,999,999,999 must exactly match our request. `invoice_node_id` must be `offer_issuer_id` (or the final blinded id we sent to). The signature must verify. `invoice_amount` must equal `invreq_amount` when we set it. It must have arrived through our `reply_path` |
| B12-INV-05 | invoice reader | MPP compulsory: MUST pay over several paths. No MPP bit: MUST NOT split. Ignore fallbacks with version > 16 or an address length outside 2-40 |
| B12-ERR-01 | invreq/invoice reader | MAY answer an invalid message with `invoice_error` through the reply path. We send it only for invoice_requests that pass the signature check (D10) |
| B12-PAY-01 | payer | Pay over the invoice's blinded paths: an unblinded route to the introduction node, `current_path_key` in the introduction node's payload, the blinded hops' `encrypted_recipient_data`, and `total_amount_msat` at the final blinded hop. Aggregate fee and CLTV from `blinded_payinfo` (the `blinded-payment-onion-test.json` shape) |
| B12-PAY-02 | payer | If we are the introduction node of the path, we decrypt our own hop and send `update_add_htlc` with `path_key` to the next node **(inferred from the route-blinding rules; LDK does this; unverified for CLN-made paths)** |
| B12-RCV-01 | payee | A blinded final hop is accepted only with our `path_id` and the invoice's amount and expiry. M5's `BlindedPathId` and `FinalHopProcessor` do this for invoice rows that carry a preimage |

### 1.9 Official test vectors
| File (lightning/bolts master) | Shape | Used in |
|---|---|---|
| `bolt04/blinded-onion-message-onion-test.json` (already committed: `test/NLightning.Integration.Tests/BOLT4/Vectors/`) | `generate{session_key, hops[4]{alias, path_key_secret, tlvs, encrypted_data_tlv, ss, HMAC256('blinded_node_id', ss), blinded_node_id, E, H(E‖ss), next_e, rho, encrypted_recipient_data}}`, `route{first_node_id, first_path_key, hops[4]}`, `onionmessage{unknown_tag_1, onion_message_packet}`, `decrypt{hops[4]{alias, privkey, onion_message, next_node_id / tlvs}}`. The sender prepends a hop to Alice to reach Dave's path Bob→Dave; Alice's hop carries `next_path_key_override` | OM1 (full construct + decrypt; today only the peel chain is used, `OnionVectorTests.Given_BlindedOnionMessageVector_When_PeelingChain_Then_EachNextPacketMatches`) |
| `bolt04/route-blinding-test.json` (committed) | generate/route/unblind with `next_path_key_override` | M5; OM1 reuses it for message-path creation |
| `bolt04/blinded-payment-onion-test.json` (committed) | `generate{session_key, associated_data, final_amount_msat, final_cltv, blinded_payinfo, blinded_route, full_route.hops[5], onion}`, `decrypt.hops[5]` | B12-E blinded send (byte-exact onion) |
| `bolt12/format-string-test.json` (new) | 12 × `{comment, valid, string}` | B12-A |
| `bolt12/offers-test.json` (new) | 53 × `{description, valid, bolt12, fields[{type, length, hex}]}` | B12-A |
| `bolt12/signature-test.json` (new) | 4 × `{comment, tlv, first-tlv, leaves[…], branches[…], merkle}`, with a full `invoice_request` case (issuer key 0x41×32, payer key 0x42×32, metadata 0x00×8) | B12-A (Merkle), B12-B (signature) |
| `bolt12/payer-proof-test.json` (exists) | payer proofs | **not used** (out of scope) |

Always fetch vectors from `https://raw.githubusercontent.com/lightning/bolts/master/<path>`, record the upstream commit in a `README.md` next to them (as `BOLT4/Vectors/README.md` does) and commit them unmodified. **No official vector exists for** an `invoice` or `invoice_error` round trip, or for a payer invoice_request flow. Those are covered by CLN-captured bytes (§5 B12-A-T4) and by the Docker proofs.

---

## 2. Current state (verified at `88d046c7`, plus the RF1 M5 branch where noted)

### 2.1 What exists
- **Sphinx for any payload length.** `ISphinxService.Construct(..., hopPayloadsLength)`, `PeelAsLocalNode(packet, associatedData, pathKey, OnionPacketKind)` and `Peel(..., PrivKey nodeKey, ...)` exist (`src/NLightning.Domain/Protocol/Onion/Interfaces/ISphinxService.cs:44,88,117`). The peeler applies the `path_key` tweak and exposes `PeeledOnion.PathKeySharedSecret` (ONION plan §5 "M1/M2 as built"). The message vector's peel chain is proven byte-exact (`test/NLightning.Integration.Tests/BOLT4/OnionVectorTests.cs:305`).
- **Route blinding (M5, RF1 branch, not merged):** blinded path creation, unblinding as the local node, the `encrypted_data_tlv` codec, `BlindedPathId`, and a blinded final hop and forward in the switch (§ header). Nothing for messages: `BlindedRecipientDataValidator` applies the **payment** rules (payment_relay and constraints required). Message paths need a separate rule set (OM-S-04, OM-R-04).
- **Features:** `FeatureOptions.OptionOnionMessages` defaults to `No` and is in `ExperimentalFeatures` (`FeatureOptions.cs:30,156`). It is already written to init and node_announcement when advertised (`FeatureOptions.cs:369-371`; `NodeAnnouncementService` takes `GetNodeFeatures(FeatureContext.NodeAnnouncement)`, `src/NLightning.Application/Gossip/Announcements/NodeAnnouncementService.cs:144`).
- **Peer plumbing:**
  - `PeerService.HandleMessage` is an `else if` chain: channel messages, error, warning, stfu, gossip queries, channel_update, 256/257 (`src/NLightning.Infrastructure/Node/Services/PeerService.cs:276-383`). Anything else is **dropped**, so a 513 today is silently ignored (correct for an odd type).
  - Non-channel sends are type-gated methods (`SendGossipMessageAsync`, `PeerService.cs:261-269`).
  - RF1 R2 (peer storage, `wip/fafo-rf1-r2-peer-storage-s2`) adds the same pattern: `SendPeerStorageMessageAsync`, an `IPeerStorageService?` ctor argument wired by `PeerServiceFactory`, and one `else if` arm. M6 copies it.
- **Outbox:** `PeerOutbox` is the FIFO per connection, with a capped gossip share (`src/NLightning.Application/Node/Services/PeerOutbox.cs:86-118`). `IPeerManager.GetPeer(nodeId)`/`ListPeers()` find a connection (`src/NLightning.Domain/Node/Interfaces/IPeerManager.cs:39-40`).
- **Graph:** `IGraphView` (BOLT 7 G2) has nodes with their `node_announcement` features and the channels needed to resolve `sciddir_or_pubkey` and to find an onion-message path (`src/NLightning.Domain/Gossip/`).
- **Invoices and payments:**
  - `InvoiceEntity` requires `Bolt11` (`src/NLightning.Infrastructure.Persistence/Entities/Payment/InvoiceEntity.cs:41`). It holds a preimage, a secret, an amount, an expiry and a status, and its Settled save is the HTLC-set commit point (root `CLAUDE.md`).
  - `PaymentEntity` holds `Bolt11?` and `PayeeNodeId` (`Entities/Payment/PaymentEntity.cs:22,27`).
  - `IPaymentService.PayInvoiceAsync(bolt11, ...)` only takes BOLT 11 (`src/NLightning.Domain/Payments/Interfaces/IPaymentService.cs:40,66`).
- **BIP-340:** `LocalLightningSigner` already produces a BIP-340 Schnorr signature for the wallet (`src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs:1565`), so NBitcoin's secp256k1 Schnorr support is available in `Infrastructure.Bitcoin`.
- **Bech32:** `src/NLightning.Infrastructure.Bitcoin/Encoders/Bech32Encoder.cs` is an `internal` NBitcoin subclass for BOLT 11 with a checksum. It is not usable for BOLT 12 (no checksum, `+` continuation), and BOLT 12 needs no length limit.
- **IPC:** `ClientCommand` goes up to `DescribeGraph = 20`, so the next free value is 21 (`src/NLightning.Domain/Client/Enums/ClientCommand.cs`). **RF1 R4 takes 21 for `DisconnectPeer`** (`wip/fafo-rf1-r4-operator-ipc-s1`). Every number in this plan is assigned by the integrator at merge time.
- **CLN fixture:** `elementsproject/lightningd:v26.06.8` with `--developer --dev-bitcoind-poll=1 --ignore-fee-limits=false` (`test/NLightning.Integration.Tests/Fixtures/ClnFixture.cs:34-36,216-231`). `ClnClient.CallAsync(method, ct, params)` reaches any RPC (`test/NLightning.Integration.Tests/Docker/Utils/ClnClient.cs:23`).

### 2.2 Gaps (the ledger agent files the "new" ones)
| # | Gap | NL |
|---|---|---|
| OG1 | No `onion_message` type, payload, serializer or dispatch; `PeerService` drops 513 | NL-080, fixed in wave M6 |
| OG2 | No message-path rule set for `encrypted_data_tlv` (M5's validator is for payments only) | NL-080, fixed (db29eccd) |
| OG3 | No sender for onion messages, and no way to prepend unblinded hops to a blinded path (`next_path_key_override = first_path_key`) | NL-080, fixed (abd81802, e4c10d9c) |
| OG4 | No rate limits for non-channel peer traffic besides gossip | NL-080, fixed for onion messages (3149da79, 76ee51af); tuning NL-446 |
| OG5 | No BOLT 12 codecs, bech32 without checksum, Merkle tree or BIP-340 message signatures | NL-447, done in wave B12 (95066ae5, b7ac3543, 3acf5ca6, 80bd155c) |
| OG6 | No blinded **send**: `PaymentOnionFactory`/`PaymentRoutePlanner` cannot target a blinded path, and there is no introduction-node-is-us case | partly done in wave rf1 (`PaymentService.PayBlindedAsync`, 8675ca37, NL-079); introduction = us and MPP done in wave B12 (9e963b82, ba314f36); BOLT 11 blinded paths and dummy hops remain (NL-440) |
| OG7 | `InvoiceEntity.Bolt11` is required and `PaymentEntity` has no BOLT 12 fields | NL-447, done in wave B12 (`AddBolt12Offers`, 03c2bfde, ffc5c62b) |
| OG8 | `sciddir_or_pubkey` resolution (SCID + direction → node id) through our own channels and the graph | NL-080, fixed for onion messages (e4c10d9c) |
| OG9 | No IPC for offers | NL-447, done in wave B12 (IPC 26-30; 16019fb9, 5a99843b, a3445f3f) |

---

## 3. Design

### 3.1 Layering
| Layer | M6 | B12 |
|---|---|---|
| Domain (BCL only) | `Protocol/Payloads/OnionMessagePayload`, `Protocol/Messages/OnionMessageMessage : BaseMessage`; `Protocol/OnionMessages/` (`OnionMessageTlvs` record, `BlindedPathCodec` + `SciddirOrPubkey` (shared with B12), `MessagePathRecipientDataRules`, `Interfaces/{IOnionMessageService, IOnionMessageHandler, IOnionMessagePacketBuilder, IOnionMessageRateLimiter}`) | `Offers/` (`Bolt12Bech32`, `Bolt12TlvRecord`/`Bolt12TlvStream` (raw, order-preserving), typed views `Offer`, `InvoiceRequest`, `Bolt12Invoice`, `InvoiceError`, `BlindedPayInfo`, `Bolt12MerkleTree`, `Validators/*`, `Interfaces/{IBolt12Signer, IOfferService, IOfferPaymentService, IOfferDbRepository}`, `Models/OfferModel`) |
| Infrastructure.Bitcoin | `Onion/OnionMessages/OnionMessagePacketBuilder` (hops + blinded path → packet, via `ISphinxService` and `IRouteBlindingService`) | `Offers/Bolt12Signer` (BIP-340 sign/verify, transient payer keys, signer hook) |
| Infrastructure.Serialization | `Payloads/OnionMessagePayloadSerializer`, `Messages/Types/OnionMessageMessageTypeSerializer`, both factory dictionaries | none: BOLT 12 bytes live inside onion payloads, and Domain codecs handle them, as with `ChannelUpdatePayload` (BOLT 7 plan D1) |
| Infrastructure (node) | `PeerService` 513 arm + `SendOnionMessageAsync`; `PeerServiceFactory` passes the service | none |
| Application | `OnionMessages/` (`OnionMessageService`, `OnionMessageForwarder`, `OnionMessageDispatcher`, `OnionMessageRateLimiter`, `ReplyPathFactory`, `OnionMessagePathFinder`, `PendingReplyRegistry`, `OnionMessageOptions`) | `Offers/` (`Receive/{OfferService, InvoiceRequestHandler, BlindedPaymentPathFactory, OfferInvoiceFactory}`, `Send/{OfferPaymentService, InvoiceHandler, InvoiceErrorHandler, InvoiceRequestFactory, InvoiceVerifier}`); `Payments/Send/Blinded/*` (blinded send) |
| Persistence | none | `Entities/Payment/OfferEntity`; `InvoiceEntity` and `PaymentEntity` columns; migration `AddBolt12Offers` |
| Daemon / Client / Transport.Ipc | none | `createoffer`, `listoffers`, `disableoffer`, `payoffer` (numbers by the integrator) |

`NLightning.Bolt11` stays BOLT 11 only. BOLT 12 lives in Domain (D1), not in a new project, which would need new sln configurations (`scripts/check-sln-configs.py`).

### 3.2 Receive pipeline (M6)
1. `PeerService` gets `OnionMessageMessage` and calls `IOnionMessageService.HandleIncoming(this, message)`, which only queues and never awaits in the read loop (the peer storage pattern). Without a service, or with the feature not advertised, the message is dropped (logged at trace).
2. **Rate limit** (`OnionMessageRateLimiter`, OM-R-01): a token bucket per incoming peer and a global one, in bytes and in messages. Over the limit, the message is dropped and the `dropped{reason=rate}` counter increments. Nothing is sent back.
3. **Peel:** `ISphinxService.PeelAsLocalNode(packet, [], path_key, OnionPacketKind.OnionMessage)`. Any `OnionException` → ignore (no replay set: onion messages have no replay rule; a replayed message is only forwarded again, inside the rate limit).
4. **Parse** `onionmsg_tlv` strictly (known types 2, 4, 64, 66, 68; unknown even → ignore).
5. **Unblind:** `IRouteBlindingService.UnblindAsLocalNode(path_key, encrypted_recipient_data, peeled.PathKeySharedSecret)`, then `MessagePathRecipientDataRules` (OM-R-03, OM-R-04, and no payment_relay or constraints expected). *As built:* BOLT 4 has no reader rule against `payment_relay`/`payment_constraints` in a message path, so the reader accepts and ignores them; only the writer refuses them (OM-S-04).
6. **Forward** (`peeled.NextPacket` is not null):
   - Only `encrypted_recipient_data` may be present.
   - Resolve the next node from `next_node_id`, or from `short_channel_id` through our channels by real SCID or alias, then the graph (OG8).
   - Send `onion_message(unblinding.NextPathKey, next packet)` to that peer through its `PeerOutbox` (§3.4).
   - If the peer is not connected, drop the message. We never open a connection to forward (D6).
7. **Final hop:**
   - Exactly one of 64/66/68 (OM-R-07). A final hop with no payload field is valid for tests; it is delivered to the "empty" handler, which only counts it.
   - `path_id` is looked up in `PendingReplyRegistry`. A match with the wrong expected type, or a match on a message that must not be a reply, is ignored (OM-R-06).
   - Dispatch to the `IOnionMessageHandler` registered for the field, with a context: the `reply_path`, the `path_id` match, and whether the message came through a blinded path of ours (the `path_id` says which) or direct (no `path_id`). B12-IRQ-03 needs that.
   - Handlers run on a bounded queue (`OnionMessageOptions.MaxQueuedHandlerWork`), never on the read loop.
8. **Blinded path whose introduction node is us** (our own `reply_path`, or a CLN path that starts at us): the sender side handles it (§3.3). The receiver never sees it as a special case.

### 3.3 Send pipeline (M6)
`IOnionMessageService.SendAsync(OnionMessageDestination destination, OnionMessageContents contents, BlindedPath? replyPath, CancellationToken ct)`:
- **Destination** is a node id or a `BlindedPath`.
- **Blinded path destination:**
  - Resolve `first_node_id` (a `sciddir` needs OG8).
  - If the introduction node is **us**, unblind our own first hop, which gives the next node and the next path key, and continue from there. Repeat while the next node is us.
  - Otherwise find a path to the introduction node with `OnionMessagePathFinder`: a direct peer first, then a short graph path over nodes that advertise 38/39 and have a connected first hop (D6).
  - Build the unblinded prefix hops with `encrypted_recipient_data` for each prefix hop, as in the vector: the sender blinds its own prefix, and the last prefix hop carries `next_path_key_override = first_path_key` (OM-S-05).
- **Node-id destination:** create a blinded path to it ourselves over the prefix (every hop gets `encrypted_recipient_data`, as in the vector) and send with no introduction.
- **Size:** payloads of 1300 bytes when the contents fit, else 32768 (OM-S-02). Contents larger than 32768 are refused.
- **Reply path:** `ReplyPathFactory` builds a path to us whose introduction node is a connected peer that advertises 38/39 (preferring one with a channel), or ourselves when we have public channels or none qualifies (D7). Our hop's `path_id` = `HMAC(nodeReplySecret, correlationId)`, 32 bytes. It is registered in `PendingReplyRegistry` with the expected reply types and a deadline. Padding makes every hop's data the same length (OM-S-08).
- **Retry** (OM-S-07) is the caller's choice (B12-E retries on other paths); `SendAsync` itself sends once.
- **Contents:** a type (64/66/68, or an odd custom type ≥ 64 for tests) and raw bytes. M6 never interprets BOLT 12 bytes.

### 3.4 Transport and DoS (M6)
- `IPeerService.SendOnionMessageAsync(OnionMessageMessage)` is type-gated like gossip.
- `PeerOutbox.TryEnqueueOnionMessage(message)` is **capped** (`OnionMessages:MaxOutboxPerPeer`, default 64) and dropped when full. Onion messages never delay channel messages beyond FIFO order, and a slow reader only loses onion messages.
- **Rate limit defaults** (to be tuned; LND 0.21 uses 512 kbps with a 256 KiB burst per peer and 5,120 kbps with a 1,600 KiB burst globally **(unverified defaults, from LND release notes)**):
  - per peer 64 KiB/s with a 256 KiB burst and 20 messages/s;
  - global 640 KiB/s;
  - handler work (invoice_requests) 5/s per offer and 20/s in total (B12).
- A message that would go back to the peer it came from is dropped (LND 0.21 does this too), and so is a forward to ourselves that is not a reply-path step.
- Metrics: `Meter("NLightning.OnionMessages")` counters for received, forwarded, delivered, dropped (by reason) and sent, plus the queue depth.

### 3.5 BOLT 12 codecs (B12-A)
- `Bolt12TlvStream` keeps the **raw records in wire order** (type, raw value). Typed views (`Offer`, `InvoiceRequest`, `Bolt12Invoice`) read and build over it. That way copy rules (B12-IRQ-01, B12-INV-01, B12-INV-04 "exactly match") compare raw bytes, unknown odd types survive, and the Merkle tree hashes exactly what was received.
- It uses the Domain `BigSizeCodec` and `TruncatedInt` (`src/NLightning.Domain/Protocol/Tlv/`) and the shared `BlindedPathCodec`.
- `Bolt12Bech32.Encode(hrp, bytes)`/`Decode(string)` implement BIP-173 5-bit conversion without a checksum, case and `+` rules, and no length limit.
- Validators are pure functions returning the first violated requirement ID, which the tests assert: `OfferValidator` (B12-OFR-01/03), `InvoiceRequestValidator` (B12-IRQ-02/04 and the refund writer rules, for `lnr` parsing), `InvoiceValidator` (B12-INV-03/05).

### 3.6 Signatures and keys (B12-B)
- The Merkle root is computed in Domain (`Bolt12MerkleTree`, SHA256 from the BCL, as `BlindedPathId` already uses HMAC).
- `IBolt12Signer` (Domain port, implemented in `Infrastructure.Bitcoin/Offers/Bolt12Signer`) has:
  - `Verify(tag, merkleRoot, xOnlyFromCompressed(pubkey), sig)`;
  - `SignAsNode(tag, merkleRoot)` for invoices of offers whose `offer_issuer_id` is our node id;
  - `DerivePayerKey(invreqMetadata)` and `SignAsPayer(metadata, tag, merkleRoot)`.
- The transient payer key is `HMAC-SHA256(nodeOffersSecret, "nltg_bolt12_payer" ‖ invreq_metadata)` reduced to a scalar. The payer secret is never stored, and it is re-derived from the metadata (D3).
- `nodeOffersSecret` is derived from the node key by the key manager (label `"nltg_bolt12"`). It does not leave `ISecureKeyManager`/`LocalLightningSigner`: the signer gets new members `SignBolt12(...)` and `GetBolt12PayerKey(...)` in a new partial file, and `IBolt12Signer` delegates to them.
- `invoice_node_id` for an offer with `offer_paths` and no `offer_issuer_id` is the final `blinded_node_id` of the path. Its signing key is `HMAC("blinded_node_id", ss) · node_key`, which M5's unblinding already computes. `Bolt12Signer.SignAsBlindedRecipient(pathKey, ...)` recomputes it. Our offers always set `offer_issuer_id` in this plan (D2), so this is only needed if D2 changes. It is built and unit-tested but not wired.
- Schnorr uses BIP-340 through NBitcoin `ECPrivKey.SignBIP340` / `ECXOnlyPubKey.SigVerifyBIP340` **(API names to confirm against the NBitcoin version in the repo)**. Keys on the wire are 33-byte compressed. Verification uses the x-only part (BIP-340 ignores the parity).

### 3.7 Receiving: offers and invoice_requests (B12-D)
1. **`createoffer`** (`OfferService.CreateOfferAsync(amount?, description, issuer?, quantityMax?, absoluteExpiry?, withPaths)`):
   - `offer_issuer_id` = our node id (D2).
   - `offer_paths` are added when we have no public channel (B12-OFR-02): one or two paths from `BlindedMessagePathFactory` (M6 `ReplyPathFactory` logic: introduction = a connected 38/39 peer with a channel to us), with `path_id` = `HMAC(offerSecret, offer_id ‖ "path")`.
   - `offer_metadata` = 16 random bytes; it identifies the offer to us.
   - The offer is stored as an `OfferEntity` (B12-C) with its raw bytes and the `lno` string. `offer_id` = SHA256 of the offer TLV bytes (CLN convention **(unverified)**; internal only).
2. **`InvoiceRequestHandler`** (`IOnionMessageHandler` for type 64):
   - Rate limit.
   - Parse and validate (B12-IRQ-02).
   - Look up the offer by the copied offer fields (exact byte match of the offer TLVs, B12-IRQ-03) and check it is active and unexpired.
   - Check the arrival path: the `path_id` of one of this offer's `offer_paths`, or direct when the offer has none.
   - Amount and quantity checks (B12-IRQ-04), signature (B12-IRQ-02).
   - On a failure after the signature is valid: `invoice_error` with `erroneous_field` (B12-ERR-01, D10). Before that: silence.
3. **Invoice** (`OfferInvoiceFactory`):
   - A fresh preimage, `payment_hash`, `created_at` and `relative_expiry` (`Offers:InvoiceRelativeExpiry`, default 7200 → omitted).
   - **Blinded payment paths** (`BlindedPaymentPathFactory` on M5's `IRouteBlindingService.CreateBlindedPath`):
     - for each usable incoming channel's peer, up to `Offers:MaxPaymentPaths` (3), a two-hop path `peer → us`, or a one-hop path at us when we have public channels;
     - `payment_relay` = the peer's `channel_update` policy toward us (the data `InvoiceService` already uses for `r` hints, NL-245);
     - `payment_constraints` = `max_cltv_expiry` (now + invoice expiry in blocks + our `min_final_cltv_expiry` + margin) and `htlc_minimum_msat`;
     - our hop's `path_id` = `BlindedPathId.Compute(preimage)` (M5).
   - `blinded_payinfo` aggregates the path's fees and CLTV with the BOLT 4 rounding (the vector's `blinded_payinfo` checks the formula).
   - MPP bit 17 when `basic_mpp` is advertised.
   - Sign as the node (`lightninginvoicesignature`).
4. **Store, then reply:** the invoice row (`InvoiceEntity` with `Kind = Bolt12`, `OfferId`, the invoice bytes and the payer id) is saved **before** the reply goes out, so a payment cannot beat the row. Then the invoice (66) is sent through the request's `reply_path` with `SendAsync`.
5. **Payment:** the switch's blinded final hop (M5) finds the invoice by `payment_hash` and checks `path_id` against its preimage. Settling works as for BOLT 11 invoices (Settled is the commit point). An HTLC that arrives unblinded for a BOLT 12 invoice is failed with `incorrect_or_unknown_payment_details` (B12-INV-02 "SHOULD ignore payments not using the paths").
6. **Caps:** at most `Offers:MaxUnpaidInvoicesPerOffer` (1,000) unpaid BOLT 12 invoice rows per offer and `Offers:MaxUnpaidInvoices` (10,000) in total. An expired unpaid row is pruned by the existing invoice expiry sweep, or by a new sweep in the lane if none exists **(check `InvoiceService` for an expiry sweep)**. Over the cap: `invoice_error` "temporarily unavailable" (D11).

### 3.8 Paying an offer (B12-E)
`OfferPaymentService.PayOfferAsync(lno, amountMsat?, quantity?, payerNote?, PayOfferOptions{MaxFeeMsat, MaxParts, Timeout, FetchTimeout (30 s), MaxFetchAttempts (3)})`:
1. Decode and validate the offer (B12-OFR-03), then the chain and expiry. Refuse `offer_currency` unless `amountMsat` is given (§0).
2. **Build the invoice_request** (`InvoiceRequestFactory`): copy every offer TLV raw, then add 32 random bytes of `invreq_metadata`, `invreq_amount` when needed, `invreq_quantity`, the derived payer id and the note. Sign as payer.
3. **Send:**
   - Destination: an `offer_paths` path (rotating on retry), else `offer_issuer_id`.
   - `reply_path` from `ReplyPathFactory`, expecting types 66 and 68, with a deadline of `FetchTimeout`.
   - Retry on the next path, or on a fresh reply path, up to `MaxFetchAttempts` (OM-S-07).
4. **Receive** (`InvoiceHandler` type 66 and `InvoiceErrorHandler` type 68, completing the `PendingReplyRegistry` entry):
   - `InvoiceVerifier`: B12-INV-03/04/05, including the exact byte match of fields 0-159 and 1e9-2,999,999,999 against our request and a signature by `offer_issuer_id` (or the final blinded id we sent to).
   - An `invoice_error` ends the attempt with its message.
5. **Pay:**
   - Persist a `PaymentEntity` (payment_hash, `Bolt12Invoice` bytes, `OfferId` string, the invreq metadata, amount).
   - Then `IPaymentService.PayBlindedAsync(BlindedPaymentTarget, options)`: the blinded send (OG6, §3.9).
   - Payment results reuse `PaymentService`'s tracking, `listpayments` and restart reconciliation, since the payment row is the same.

### 3.9 Blinded send (B12-E, OG6)
- `BlindedPaymentTarget(paths[(BlindedPath, BlindedPayInfo)], amount, paymentHash, features)`.
- For each path, `BlindedRouteBuilder` builds:
  - **introduction node reachable:** an unblinded route to `first_node_id` (direct channel, graph route from `PaymentRoutePlanner`/`GraphPathSource`, or none);
  - the blinded hops' payloads: `encrypted_recipient_data`, plus `current_path_key` at the introduction node, plus `amt_to_forward`, `outgoing_cltv_value` and `total_amount_msat` at the final hop (the BOLT 4 blinded final-hop rules, M5 `HopPayloadValidator`);
  - fee and CLTV: `blinded_payinfo`'s aggregate for the blinded part, BOLT 7 fees on the unblinded part;
  - **introduction node = us (B12-PAY-02):** unblind our own hop, then `OfferHtlcAsync` on the channel named by `short_channel_id`/`next_node_id` with `path_key` in `update_add_htlc` (`BlindedPathTlv`; the M5 branch already sends it on forwards).
- `PaymentOnionFactory` gets a blinded mode. Its proof is byte-exact against `blinded-payment-onion-test.json` (`generate.full_route` → `generate.onion`).
- MPP over several paths when the invoice sets bit 16 or 17 and one path's `htlc_maximum_msat` is too small. The existing `PaymentRoutePlanner` split logic takes paths as candidates.
- Failures inside a blinded path come back as `invalid_onion_blinding` from the introduction node. The retry policy marks that path as failed and tries the next (no graph penalty: the blinded hops are unknown).

### 3.10 Persistence (B12-C, migration `AddBolt12Offers`)
| Table / column | Fields |
|---|---|
| `Offers` (new) | `OfferId` (32 bytes, PK), `Bolt12` (string), `OfferBytes` (bytes), `Description`, `AmountMsat?`, `Currency?`, `QuantityMax?`, `AbsoluteExpiry?`, `Metadata` (16), `IssuerKind` (byte: node id or paths), `Status` (Active / Disabled / Expired), `CreatedAt`, `DisabledAt?` |
| `Invoices` (existing) | `Bolt11` becomes **nullable**; add `Kind` (byte: 0 Bolt11, 1 Bolt12; default 0 for existing rows), `OfferId?` (FK to `Offers`), `Bolt12InvoiceBytes?`, `InvoiceRequestPayerId?` (33), `Quantity?`, `PayerNote?`. Index on `(OfferId, Status)` for the caps |
| `Payments` (existing) | add `Bolt12InvoiceBytes?`, `OfferBolt12?` (the `lno` string paid), `InvoiceRequestMetadata?`, `PayerNote?`. `PayeeNodeId` = `invoice_node_id` |

- The repository changes: `IOfferDbRepository` (Add, GetById, GetByOfferBytes, List, SetStatus), the `IInvoiceDbRepository`/`IPaymentDbRepository` mappings, `IUnitOfWork.OfferDbRepository`, `CrashingUnitOfWork` in `test/NLightning.Tests.Utils/Mocks/`, and the Postgres/SQL Server round trips in `Docker/PostgresTests`/`SqlServerTests` plus the SQLite `Persistence/` round trip.
- `InvoiceModel`/`PaymentModel` get the new fields, so `ChannelRoundTripTests` does not change, but the invoice and payment round trips must cover every new field.

### 3.11 IPC (append-only `ClientCommand`; numbers by the integrator)
| Command | Request | Response | Lane |
|---|---|---|---|
| `createoffer` | amount_msat?, description, issuer?, quantity_max?, absolute_expiry?, `--paths` (force blinded paths) | `lno`, offer_id | B12-D |
| `listoffers` | active_only? | offers (id, lno, amount, description, status, invoices paid/unpaid) | B12-D |
| `disableoffer` | offer_id | status | B12-D |
| `payoffer` | lno, amount_msat?, quantity?, payer_note?, `--max-fee-msat`/`--max-parts`/`--timeout` | payment (as `payinvoice`) plus the BOLT 12 invoice fields (node_id, amount, paths used) | B12-E |
| `fetchinvoice` (optional, low priority) | lno, amount?, quantity? | the verified invoice (hex + decoded), without paying | B12-E |

At `88d046c7` the next free value is 21. RF1 R4 takes 21 (`DisconnectPeer`). The integrator appends B12's commands after whatever RF1 and later waves have taken, in the order above. M6 adds **no** command (D8).

---

## 4. Decisions

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | **BOLT 12 codecs in Domain** (`Domain/Offers`), raw TLV records plus typed views | BCL-only is enough (bech32, bigsize, SHA256). Copy and match rules compare raw bytes. Application and Infrastructure.Bitcoin both need the types | A new `NLightning.Bolt12` project (sln configs, a new package); reusing the checksummed `Bech32Encoder` |
| D2 | **Our offers set `offer_issuer_id` = node id**, with `offer_paths` added only when we have no public channel | Simplest signer (node key). CLN's own default offers also include an issuer id **(unverified)**. Privacy from a per-offer key is deferred | Per-offer derived issuer keys (key management plus signing as a blinded recipient) |
| D3 | **Transient payer keys derived from `invreq_metadata`** with a node-derived secret, never stored | Unpredictable metadata (B12-IRQ-01), a new key per request, no key storage; the payment row keeps the metadata, so the key can be re-derived for a future payer proof | A random key stored per request |
| D4 | **Reply correlation in memory** (`PendingReplyRegistry`, deadline-bounded), no migration in M6 | A reply that arrives after a restart has no waiting caller; `payoffer` is interactive | A persisted pending-request table |
| D5 | **One invoice row per answered invoice_request** (stateful), capped and pruned | Reuses M5's `path_id = HMAC(preimage)` final-hop check and the invoice Settled commit point unchanged | LDK-style stateless invoices (preimage derived from `path_id` contents): less state, but a second final-hop path through the switch |
| D6 | **Forward only to connected peers; never connect to forward or reply** in these waves | Bounded resources, no connection amplification. CLN and LDK do connect to reply (CLN changelog), and that is a later option (`OnionMessages:ConnectToReply`, default false) | Connect on demand |
| D7 | **Reply-path introduction node = a connected 38/39 peer with a channel**, else ourselves | Our node may be private; CLN (25.12) needs 38/39 on path peers (F-05) | Always ourselves (reveals the node id of a private node) |
| D8 | **No IPC in M6** | The proofs drive `IOnionMessageService` in-process through `NLightningTestNode`, and an operator has nothing to send by hand | A `sendonionmessage` command (CLN removed its own in 24.08) |
| D9 | **Feature 39 Optional by default only after Proof M6**, removed from `ExperimentalFeatures` in the integration commit that records the proof | Root `CLAUDE.md` rule: unimplemented features are refused | Advertising with the first commit |
| D10 | **`invoice_error` only after a valid signature**; silence before | The spec reader rules are "FIXME". Answering unauthenticated junk is a reflection vector | Always answering |
| D11 | **Caps and rate limits in the handler, not in the offer table** | A flood of invoice_requests from one payer would otherwise fill `Invoices`; rows are cheap, but unbounded rows are not | No caps |
| D12 | **Blinded send in wave B12** (lane B12-E), not in M5 | The M5 branch contains no send side; offers are its first user | Blocking B12 on an M5b |

---

## 5. Milestones

### Contracts first (both waves)
Before the lanes fork, the integrator lands one commit that adds only interfaces, records and enum values. It carries no behavior, and every lane rebases on it:
- **M6-0:**
  - `MessageTypes.OnionMessage = 513`;
  - `OnionMessagePayload`/`OnionMessageMessage` (records, no serializer);
  - `Domain/Protocol/OnionMessages/Interfaces/{IOnionMessageService, IOnionMessageHandler, IOnionMessagePacketBuilder, IOnionMessageRateLimiter}`, `OnionMessageTlvs`, `OnionMessageContents`, `OnionMessageDestination`, `SciddirOrPubkey`;
  - `IPeerService.SendOnionMessageAsync` (default `NotSupportedException` in fakes).
- **B12-0:**
  - `Domain/Offers/Interfaces/{IBolt12Signer, IOfferService, IOfferPaymentService, IOfferDbRepository}`;
  - `OfferModel`, `Bolt12TlvStream` (signature only);
  - `IPaymentService.PayBlindedAsync` and `BlindedPaymentTarget`/`BlindedPayInfo` records;
  - `InvoiceModel`/`PaymentModel` field additions (no persistence).

### M6 milestones
**OM0: Wire and codecs** (lane M6-A)
| Task | Files | Acceptance |
|---|---|---|
| OM0-T1 `onion_message` serializer | `src/NLightning.Infrastructure.Serialization/Payloads/OnionMessagePayloadSerializer.cs`, `Messages/Types/OnionMessageMessageTypeSerializer.cs`, both dictionaries in `Factories/PayloadSerializerFactory.cs` and `Factories/MessageTypeSerializerFactory.cs` | Round trip of every `decrypt.hops[i].onion_message` in the message vector byte-exact. `len` other than 1366/32834 is accepted (only the writer SHOULD). `len < 66` or a truncated message → `PayloadSerializationException` (the peer gets a warning and close, NL-207). Not raised as a channel message |
| OM0-T2 `onionmsg_tlv` codec | `src/NLightning.Domain/Protocol/OnionMessages/OnionMessageTlvsCodec.cs` | Strict: increasing types, minimal bigsize, unknown even → rejected, unknown odd kept. Final-field count helper (OM-R-07) |
| OM0-T3 `blinded_path` + `sciddir_or_pubkey` codec | `src/NLightning.Domain/Protocol/OnionMessages/{BlindedPathCodec,SciddirOrPubkeyCodec}.cs` (the `SciddirOrPubkey` record is M6-0's `SciddirOrPubkey.cs`) | Round trip of the vector's `route`; `num_hops` 0 and `enclen` overflow rejected; 0/1 prefix → SCID with direction, 2/3 → point, any other byte rejected. The codec maps to and from M5's `BlindedPath` (introduction as a node id, or unresolved SCID + direction) |
| OM0-T4 Message-path recipient-data rules | `src/NLightning.Domain/Protocol/OnionMessages/MessagePathRecipientDataRules.cs` | Table tests: OM-R-03 (any `allowed_features` bit → ignore), OM-R-04 (non-final with `path_id` → ignore), OM-S-04 (writer refuses `payment_relay` and constraints), a non-final without next node or SCID → ignore |

**OM1: Construct and peel with vectors** (lane M6-B)
| Task | Files | Acceptance |
|---|---|---|
| OM1-T1 Message path creation | `src/NLightning.Infrastructure.Bitcoin/Onion/OnionMessages/BlindedMessagePathBuilder.cs` (on `IRouteBlindingService.CreateBlindedPath`, with equal-length padding) | From `generate.hops[*].tlvs`/`path_key_secret` (with the `next_path_key_override` join at Alice): every `encrypted_data_tlv`, `ss`, `blinded_node_id`, `E`, `next_e`, `rho` and `encrypted_recipient_data` equal the vector, and `route` equals the vector's `route` |
| OM1-T2 Packet builder | `.../OnionMessages/OnionMessagePacketBuilder.cs` (`IOnionMessagePacketBuilder`: prefix hops + blinded path + final `onionmsg_tlv` → `OnionMessageMessage`; size choice 1300/32768) | With `generate.session_key` **(confirm which key the vector uses for the Sphinx session; the comment ties `session_key` to the path generation)** the builder reproduces `onionmessage.onion_message_packet` byte-exact, including `unknown_tag_1` = "hello" at Dave; the first wire message equals `decrypt.hops[0].onion_message` |
| OM1-T3 Receive-side crypto | `.../OnionMessages/OnionMessageUnwrapper.cs` (peel via `ISphinxService`, then unblind via `IRouteBlindingService`) | For each `decrypt.hops[i]`, with its privkey: the next `onion_message` equals `decrypt.hops[i+1].onion_message`, `next_node_id` matches, and Dave gets `path_id` and the unknown odd TLV. Any flipped bit → the "ignore" result, never an exception to the caller |
| OM1-T4 Vector test file | `test/NLightning.Integration.Tests/BOLT4/OnionMessageVectorTests.cs` | Every value above, plus a 32768-byte payload round trip (builder → unwrapper, 4 hops) |

**OM2: Node pipeline** (lanes M6-C, M6-D)
| Task | Files | Acceptance |
|---|---|---|
| OM2-T1 Transport arm | `PeerService.cs` (one `else if` arm + `SendOnionMessageAsync`), `PeerServiceFactory.cs` (passes the optional `IOnionMessageService`), `PeerOutbox.TryEnqueueOnionMessage` (capped), `PeerManager` exposing the outbox for a node id through a Domain port `IPeerOnionMessageOutbox` (the `IPeerGossipOutbox` pattern) | `PeerServiceOnionMessageTests`: a 513 is handed over and never raised as a channel message; dropped without a service; send is type-gated; outbox cap drops and counts |
| OM2-T2 Rate limiter | `src/NLightning.Application/OnionMessages/OnionMessageRateLimiter.cs` (per peer + global token buckets, `TimeProvider`) | Fake-clock tests: burst, refill, per-peer isolation, global cap |
| OM2-T3 Receive, forward, deliver | `.../OnionMessages/{OnionMessageService,OnionMessageForwarder,OnionMessageDispatcher,PendingReplyRegistry}.cs` | Every OM-R row in §6.1 as a unit test (ignore cases produce no send and a `dropped{reason}` count); forward by `next_node_id`, by SCID (real and alias) and by `sciddir`; no forward to a disconnected peer; no echo to the sender |
| OM2-T4 Send and reply paths | `.../OnionMessages/{OnionMessagePathFinder,ReplyPathFactory}.cs` | Direct, via one graph hop, introduction = us (self-unblind loop), introduction unreachable → `SendResult.NoPath`; reply path introduction = a 38/39 peer with a channel, else us; a reply is delivered only with a matching `path_id` and type (OM-R-06) |
| OM2-T5 Three-node harness | `test/NLightning.Application.Tests/OnionMessages/OnionMessageHarnessTests.cs` (three in-process nodes, real Sphinx, blinding and outboxes) | A → B → C with a reply C → B → A through A's reply path; B as the introduction node of C's path; 32 KiB message; rate limit at B drops the 21st message in a second |

**OM3: Options, features, metrics** (lane M6-D)
| Task | Files | Acceptance |
|---|---|---|
| OM3-T1 Options | `src/NLightning.Application/OnionMessages/OnionMessageOptions.cs` (binding `OnionMessages:*`: limits §3.4, `MaxOutboxPerPeer`, `ReplyTimeout`, `ConnectToReply` = false), the template in `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs`, `AddOnionMessageServices()` | Options validation tests; template snapshot |
| OM3-T2 Feature gate | Service active only when `IsAdvertised(OptionOnionMessages)` | With the feature off: 513 dropped, `SendAsync` → `NotAvailable` |
| OM3-T3 Metrics | `Meter("NLightning.OnionMessages")` | Counters asserted in OM2 tests |

**Proof M6 (lane M6-E; Docker, `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnOnionMessageTests.cs`; our nodes run with `AllowExperimentalFeatures` and `OptionOnionMessages = Optional`)**
- (a) **We send through CLN:** N1 – CLN – N2 are connected (no channels needed; CLN SHOULD accept from channelless peers, OM-R-09 **(verify)**). N1 sends N2 a message whose path runs through CLN (CLN is an unblinded prefix hop that N1 blinds itself) with an odd test field 65 = "hello". N2 delivers it with the right bytes. This proves CLN peels and unblinds our construction and forwards it, and that we accept CLN's forward.
- (b) **CLN forwards what it was given:** the test builds an onion with our packet builder whose first hop is CLN and whose next hop is N1, and calls CLN's `injectonionmessage {path_key, message}` (v24.11+). N1 receives it. This is a second check of CLN's forward path with a blinded path we created, including `next_path_key_override`.
- (c) **CLN builds, we receive and reply:** N1 has an offer string for its node id made by a test-only encoder in `test/NLightning.Tests.Utils/Bolt12/MinimalOfferEncoder.cs` (TLVs 10 and 22, bech32 without checksum; replaced by B12-A later). CLN runs `fetchinvoice offer=<lno>`. N1 receives an `invoice_request` (type 64) with a CLN-made `reply_path`, and dispatches it to a test handler that answers `invoice_error` (type 68, `error` = "nltg-m6-proof") through the reply path. CLN's `fetchinvoice` fails with our error text **(unverified: the exact CLN error surface for a received invoice_error; record it)**. This proves CLN's onion construction and reply path, our decode, and our send through a CLN blinded path (with CLN or us as the introduction node).
- (d) **Rate limit and junk:** 200 messages from N2 in one second: CLN stays connected to N1, and N1 drops above its per-peer limit (counter). A message with a corrupt HMAC is ignored, and no warning goes to the sender.
- (e) **Feature interop:** CLN's `listpeers` shows our features with bit 39. Our node_announcement (a public channel from `ClnGossipTests`' topology, if present) carries 39 **(optional)**.

LND 0.20 has no onion messages. LND 0.21 forwards them (§7), and a forward proof against it is optional, for a later LND bump.

### Wave M6 record (integrated into `wip/fafo` @ `641a5fff`, 2026-09-27)

Five lanes on the M6-0 contracts (b4d3eed3), each with a review/fix step, plus lane W1 (on-chain `withdraw`, unrelated to M6, NL-441). Lane commits cherry-picked with `-x` in the order A, B, C, D, W1, E (one conflict: `src/NLightning.Application/CLAUDE.md`, both sections kept). The commits cite NL-079; the ledger records the work on NL-080.

| Lane | Tasks | `wip/fafo` SHAs | Result |
|---|---|---|---|
| M6-A wire + codecs | OM0-T1..T4 | a16baab2, 6aea59de, a99af0d2, db29eccd, 47adc130, 3fdf4b00 | done: 513 serializer (any `len` >= 66; strict trailing TLVs), `OnionMessageTlvsCodec`, `BlindedPathCodec` + `SciddirOrPubkeyCodec`, `MessagePathRecipientDataRules`; vector wire messages byte-exact; `blinded_path` independently proven against the `bolt12/offers-test.json` offer_paths (3fdf4b00) |
| M6-B crypto | OM1-T1..T4 | abd81802, 7f0afe02 | done: `BlindedMessagePathBuilder`, `OnionMessagePacketBuilder`, `OnionMessageUnwrapper` (`AddOnionMessageCryptoServices()` in `AddBitcoinInfrastructure`); `blinded-onion-message-onion-test.json` byte-exact (generate, route, packet, every decrypt hop). Risk 2 resolved: `generate.session_key` is the Sphinx session key; each path segment uses its own first `path_key_secret` |
| M6-C transport | OM2-T1, OM2-T2 | 413d420b, 3149da79, 76ee51af | done: `PeerService` 513 arm and gated send, `PeerOutbox` onion class (cap 64, 1 onion message per 8 gossip sends, never ahead of channel/warning/error/disconnect), `IPeerOnionMessageOutbox` on `PeerManager`, `OnionMessageRateLimiter` (per peer 64 KiB/s burst 256 KiB and 20 msg/s burst 20; global 640 KiB/s burst 2,560 KiB and 200 msg/s burst 200; buckets swept once refilled) |
| M6-D pipeline | OM2-T3..T5, OM3 | e4c10d9c, b81a1fd9 | done: `OnionMessageService` (forwards and sends through the capped outbox, never awaited), path finder, reply paths, `PendingReplyRegistry`, `OnionMessageOptions` (invalid options keep the service off), meter, three-node harness, the BOLT 4 vector through four service nodes |
| M6-E Docker proof | Proof M6 (a)-(e) | dcd9d5d8, 365e3128 (+ 5ae1701c) | done: `ClnOnionMessageTests` 10/10 against CLN v26.06.8 |
| Integration | registrations, codec swap, D9 | fc686ff0, 9639b7cf, 5ae1701c, 641a5fff | outbox, cap and limiter bound from `OnionMessages`; lane D's codec copy deleted; `OptionOnionMessages` Optional by default and out of `ExperimentalFeatures` |

Deviations from this plan (spec wins):
- OM-R-07 counts every type >= 64 as a payload field (the row above is corrected); a test shows 65 counts.
- The codec file is `SciddirOrPubkeyCodec.cs`; `SciddirOrPubkey.cs` is M6-0's record.
- A message path may carry both `next_node_id` and `short_channel_id`; `next_node_id` wins (BOLT 4 onion-message reader). M5's payment validator refuses that case, so the rule sets differ on purpose.
- `payment_relay`/`payment_constraints` in a message path are accepted and ignored by the reader (§3.2 step 5); the writer refuses them.
- A forward to ourselves is dropped as `loop` rather than processed as a dummy hop (our message paths have no dummy hops; payment paths have them since NL-440, message paths are NL-520).
- D7 relaxed: the reply path's introduction node is a connected onion-message peer with an open channel first, then any connected onion-message peer, then us, so channelless topologies (the CLN proof) work.
- A malformed 513 still takes the NL-207 warning-and-close path (NL-444; fixed in wave lh1, 8035c63b: ignored and counted).
- The rate-limit values beyond §3.4 were chosen by lane M6-C (NL-446).

Gates at `641a5fff`: Release and Release.Native 0 errors, the 5 baseline CS86xx warnings, net11.0 compile check green, `dotnet format` clean; 8332 non-Docker tests on net10.0 (both configs), Long simulator 1/1; Docker (net10.0, in-container runner, SQL Server skipped) CLN 33/33 incl. Proof M6, LND 66/66, gossip 28/28, ABCD 3 x 10/10, on-chain legacy 24 (+2 Explicit), anchors 18/18. Proof M6 passed before the D9 flip, and every suite above ran after it.

Follow-ups: NL-442 (one codec, one path builder, the service on `IOnionMessageUnwrapper`, interfaces to Domain, the harness on the real builder/limiter/outbox), NL-444, NL-446, NL-445 (a flake seen by lane M6-C). Proof M6 (e) checked bit 39 in `listpeers` both ways; our `node_announcement` carrying 39 was not asserted (optional). A forward proof through LND 0.21 waits for a fixture bump.

### B12 milestones
**B0: Codecs and strings** (lane B12-A)
| Task | Files | Acceptance |
|---|---|---|
| B0-T1 `Bolt12Bech32` | `src/NLightning.Domain/Offers/Encoding/Bolt12Bech32.cs` | `format-string-test.json`: every `valid` string decodes to the same bytes as its canonical form, and every invalid one is rejected. Uppercase output round-trips |
| B0-T2 TLV stream + typed views | `src/NLightning.Domain/Offers/{Bolt12TlvStream,Bolt12TlvRecord,Offer,InvoiceRequest,Bolt12Invoice,InvoiceError,BlindedPayInfo}.cs` | `offers-test.json`: every `valid` offer parses to exactly `fields[]` (type, length, hex) and re-encodes byte-exact |
| B0-T3 Validators | `src/NLightning.Domain/Offers/Validators/{OfferValidator,InvoiceRequestValidator,InvoiceValidator}.cs` | Every `valid: false` offer in `offers-test.json` is rejected, with the requirement ID asserted per case; table tests for B12-IRQ-02/04 and B12-INV-03/05 |
| B0-T4 Captured vectors | `test/NLightning.Tests.Utils/Vectors/Bolt12Vectors.cs`: an offer, an invoice_request and an invoice made by CLN v26.06.8 (captured by an `Explicit` test through CLN's `offer` and `fetchinvoice`, and the raw onion payloads recorded by `RawOnionMessageRecorder`) | Each parses, re-encodes byte-exact and validates. The invoice's and invreq's signatures verify (after B1) |
| B0-T5 Merkle tree | `src/NLightning.Domain/Offers/Signing/Bolt12MerkleTree.cs` | `signature-test.json`: every leaf, nonce leaf, branch and `merkle` value byte-exact, including the full `invoice_request` case |

**B1: Signatures** (lane B12-B)
| Task | Files | Acceptance |
|---|---|---|
| B1-T1 BIP-340 tagged sign and verify | `src/NLightning.Infrastructure.Bitcoin/Offers/Bolt12Signer.cs` | `signature-test.json` full invoice_request: our signature with the payer key 0x42×32 equals the vector's signature bytes (BIP-340 with the auxiliary randomness the vector uses; **confirm whether the vector signs deterministically (aux = 0)**; if not, assert verify only). The vector's signature verifies against `invreq_payer_id`. Wrong tag, key or bit → false |
| B1-T2 Signer members | `src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs` (+2 members), `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.Bolt12.cs` (new partial) | Node-key signature verifies under our node id. The payer key is deterministic from metadata and differs per metadata. No secret leaves the signer |
| B1-T3 Blinded-recipient signing key | `Bolt12Signer.SignAsBlindedRecipient` | For `route-blinding-test.json` hop keys, the derived key's pubkey equals `blinded_node_id` (built, not wired; D2) |

**B2: Persistence** (lane B12-C, migration owner)
| Task | Files | Acceptance |
|---|---|---|
| B2-T1 Migration `AddBolt12Offers` | `src/NLightning.Infrastructure.Persistence/Entities/Payment/OfferEntity.cs`, `InvoiceEntity`/`PaymentEntity` columns, `EntityConfiguration/…`, `Contexts/NLightningDbContext.cs`, the migration, Designer and Snapshot files in each of `src/NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}` | `HasPendingModelChanges() == false` × 3 (`PersistenceConfigurationTests`); existing invoice rows keep `Kind = 0` and their `Bolt11` (data test on the SQLite migration like `ChannelMigrationDataTests`) |
| B2-T2 Repositories | `src/NLightning.Infrastructure.Repositories/Database/Payment/OfferDbRepository.cs`, invoice/payment repository mappings, `UnitOfWork`, `test/NLightning.Tests.Utils/Mocks/CrashingUnitOfWork.cs` | SQLite round trip of every new field (`test/NLightning.Integration.Tests/Persistence/Bolt12PersistenceTests.cs`); Postgres round trip in `Docker/PostgresTests` (SQL Server case written, skipped per the standard cycle) |

**B3: Receive** (lane B12-D)
| Task | Files | Acceptance |
|---|---|---|
| B3-T1 `OfferService` + `createoffer`/`listoffers`/`disableoffer` | `src/NLightning.Application/Offers/Receive/OfferService.cs`; IPC DTOs in `src/NLightning.Transport.Ipc/{Requests,Responses}/`, daemon handlers `src/NLightning.Daemon/Ipc/Handlers/{CreateOffer,ListOffers,DisableOffer}IpcHandler.cs` + `src/NLightning.Daemon/Handlers/*ClientHandler.cs`, CLI in `src/NLightning.Client/Handlers/` + printers | Offers valid under `OfferValidator`; paths present when we have no public channel; IPC round trips (`Daemon.Tests`) |
| B3-T2 `InvoiceRequestHandler` | `.../Offers/Receive/InvoiceRequestHandler.cs` | Every B12-IRQ row as a unit test (§6.3); `invoice_error` only after a valid signature; caps (D11) |
| B3-T3 Invoice with blinded payment paths | `.../Offers/Receive/{OfferInvoiceFactory,BlindedPaymentPathFactory}.cs` | Invoice passes `InvoiceValidator`; payinfo aggregate equals the BOLT 4 formula on the `blinded-payment-onion-test.json` payinfo; row saved before the reply (crash test with `CrashingUnitOfWork`) |
| B3-T4 Final hop | `src/NLightning.Application/Payments/FinalHop/FinalHopProcessor.cs` (small change after RF1 lands: an unblinded HTLC for a `Kind = Bolt12` invoice → 0x400F) | Unit test on both paths |

**B4: Pay** (lane B12-E)
| Task | Files | Acceptance |
|---|---|---|
| B4-T1 Blinded send | `src/NLightning.Application/Payments/Send/Blinded/{BlindedRouteBuilder,BlindedPaymentTarget}.cs`, a blinded mode in `Payments/Routing/PaymentOnionFactory.cs`, `PaymentService.PayBlindedAsync` | `blinded-payment-onion-test.json`: `full_route` → `onion` byte-exact; B12-PAY-02 (introduction = us) unit test; MPP over two paths; `invalid_onion_blinding` → the next path |
| B4-T2 `InvoiceRequestFactory` + `InvoiceVerifier` | `src/NLightning.Application/Offers/Send/{InvoiceRequestFactory,InvoiceVerifier}.cs` | The request passes `InvoiceRequestValidator` and its signature verifies; every B12-INV-03/04/05 row as a unit test, with a CLN-captured invoice (B0-T4) as the positive case |
| B4-T3 `OfferPaymentService` + `payoffer` (+ optional `fetchinvoice`) | `.../Offers/Send/{OfferPaymentService,InvoiceHandler,InvoiceErrorHandler}.cs`, IPC DTOs, handlers, CLI | Fetch retries over paths; `invoice_error` surfaced; timeout; the payment row holds the BOLT 12 fields; `listpayments` shows it |
| B4-T4 Two-node offer harness | `test/NLightning.Application.Tests/Offers/OfferHarnessTests.cs` (in-process A–B–C with channels) | A pays C's offer (C private, path introduced by B); B pays C's offer (B is the introduction node, B12-PAY-02); C restarts between invoice and payment: the payment still settles (row persisted) |

**Proof B12 (Docker, CLN; lanes B12-D and B12-E each own one file):**
- `ClnOfferReceiveTests` (B12-D): CLN pays our offer.
  - (a) We `createoffer` 10,000 sat with a description. CLN `fetchinvoice` succeeds, and `decode` of the returned invoice shows our node id, the amount and our paths. CLN `pay`/`xpay` the invoice, our invoice row is Settled, and `listinvoices` shows it.
  - (b) An amountless offer: CLN `fetchinvoice amount_msat=...` then pays.
  - (c) Our node with only a private channel to CLN: the offer has `offer_paths` introduced by CLN, and CLN still fetches and pays **(verify that CLN accepts a path whose introduction node is itself)**.
  - (d) CLN `xpay <offer>` directly (CLN ≥ 25.09 **(verify)**).
  - (e) `disableoffer`, then CLN `fetchinvoice` times out or gets our `invoice_error` (record which).
- `ClnOfferPayTests` (B12-E): we pay CLN's offer.
  - (a) CLN `offer 10000sat "nltg"`. We `payoffer`. CLN's `listinvoices` shows it paid, and our payment row is Succeeded with CLN's `invoice_node_id`.
  - (b) An amountless CLN offer with `--amount-msat`.
  - (c) CLN's invoice paths introduced by **us** (we are CLN's only peer): B12-PAY-02 in the wild **(CLN's path choice is unverified; record the introduction node)**.
  - (d) A quantity offer (`quantity_max`).
  - (e) The payer note appears in CLN's `listinvoices` **(verify the field name)**.
- Proof B12 passes when the vector tests, the harness and both Docker files are green three runs in a row (`--filter "Category=Interop.Cln&FullyQualifiedName~Offer"`).

### Wave B12 record (integrated into `wip/fafo` @ `a3445f3f`, 2026-09-27)

Five lanes on the B12-0 contracts (6f4bdaad), each with a review/fix step; B12-C was the migration owner (`AddBolt12Offers`, all three providers). Lane commits cherry-picked with `-x` in the order C, A, B, D, E (lane E's four merge commits left out; conflicts in `NamedPipeIpcClient.cs` and the Application, Daemon, Transport.Ipc and test CLAUDE.md files, both sides kept).

| Lane | Tasks | `wip/fafo` SHAs | Result |
|---|---|---|---|
| B12-A codecs | B0-T1, T2, T3, T5 (T4 open) | 95066ae5, b7ac3543 | done: `Bolt12Bech32` (12/12 `format-string-test.json`), `Bolt12TlvStream` + typed views (20 valid offers byte-exact, 33 invalid rejected with the requirement id), validators, `Bolt12MerkleTree` (every `signature-test.json` value). B0-T4 open (NL-450) |
| B12-B signer | B1-T1..T3 | 3acf5ca6, 80bd155c | done: `IBolt12Signer`, `ILightningSigner.GetBolt12PayerId`/`SignBolt12(Bolt12SigningKey, tag, root)`; the vector signature reproduced byte for byte (the vector signs with zero aux randomness: open question of B1-T1 answered); each tag bound to its key kind; `SignAsBlindedRecipient` built, not wired |
| B12-C schema | B2-T1, B2-T2 | 03c2bfde, ffc5c62b | done: `AddBolt12Offers` x3 (generated with dotnet-ef 10.0.12, matching the EF packages), `OfferDbRepository`, BOLT 12 invoice/payment mappings, nullable `InvoiceModel.Bolt11`, SQL-side unpaid counts incl. Accepted, `PruneExpiredBolt12InvoicesAsync` (no caller, NL-448); SQLite and Postgres round trips |
| B12-D receive | B3-T1..T4, Proof B12 receive | 61ea7b02, 883a883f, 16019fb9, bf8da622, 5ec25ce9, f01081a8, 59a0072d, c7f0586d, 1e43d24a | done: `OfferService`, `InvoiceRequestHandler` (type 64), `OfferInvoiceFactory`, `BlindedPaymentPathFactory`, final-hop rule, IPC 26-28; `ClnOfferReceiveTests` 4/4 (a)-(e) with the channel balance checked |
| B12-E pay | B4-T1..T4, Proof B12 pay | 9e963b82, bc59fa90, 5a99843b, 4fd7d44e, e6248970, 33e49e03, 8ae4c1b8, b0d3f056, 64f2b1db, 52494429, ba314f36 | done: blinded send with MPP and introduction = us, `InvoiceRequestFactory`, `InvoiceVerifier`, `OfferPaymentService`, IPC 29-30, `OfferHarnessTests` (production issuer, onion messages and signer); `ClnOfferPayTests` 4/4 |
| Integration | registrations, command numbers, codec seams | a3445f3f | `ClientCommand` 26-30; `AddOffersServices`/`AddOfferSendServices` after `AddOnionMessageServices`, `AddOfferIpcServices`/`AddOfferSendIpcServices`, `OfferOptions` from `Offers`; both `Bolt12Wire` files delegate to lane A's codecs (NL-453); the BOLT 11 fallback removed; composition test for the offer wiring |

Deviations from this plan (spec wins):
- B12-SIG-03 on the reader side means "a signature is present": odd 241-1000 elements are ignored (BOLT 1), even ones are refused by the strict TLV parse; "exactly one" is a writer rule (b7ac3543, ba314f36).
- `InvoiceRequestValidator` requires a signature only when the request answers an offer (`offer_issuer_id` or `offer_paths` set): the spec's refund writer rule ("MUST NOT include signature") contradicts its reader rule. Refunds are out of scope; to confirm when they are added.
- An empty `offer_paths` (or `invoice_paths`) value is rejected as B12-OFR-03 (B12-INV-03). To confirm against CLN/LDK.
- A currency offer without a converter, or an expected amount that overflows u64, fails closed (B12-IRQ-04). `ValidateAgainstRequest` needs the expected node id for a paths-only offer.
- §3.6: `nodeOffersSecret` = HMAC-SHA256(node_key, "nltg_bolt12") is derived inside `LocalLightningSigner`, not by `ISecureKeyManager`; the members are `GetBolt12PayerId`/`SignBolt12(Bolt12SigningKey, ...)`, and the signer takes the Merkle root and computes the tagged hash itself.
- B12-IRQ-03: an invoice_request for an unknown offer is ignored silently (not answered with `invoice_error`), so a prober cannot link offers to our node (1e43d24a). Every invoice_request takes a node-wide rate-limit token before parsing.
- Offer paths are introduced by connected onion-message peers with an Open channel; others only when there is none, with a logged warning (NL-452).
- B4-T3: no `InvoiceHandler`/`InvoiceErrorHandler`: the M6 `PendingReplyRegistry` delivers types 66/68 to the waiting fetch.
- B4-T1: `BlindedPaymentOnionVectorTests` not added: M5's `BlindedPaymentSendVectorTests` already covers `blinded-payment-onion-test.json` byte-exact.
- Proof B12 receive (e): after `disableoffer` CLN gets our `invoice_error` (CLN error 1004). Proof B12 pay (c): CLN introduces its invoice path itself even when we are its only peer, so B12-PAY-02 is proven in-process only.
- No feature bit (F-04): offers ride on the advertised onion-message and route-blinding bits.
- Upstream vector quirk: eight malformed `offers-test.json` cases fail at the TLV layer before the rule they name (NL-451).

Gates at `a3445f3f`: Release and Release.Native (net10.0) 0 errors, the 5 baseline CS86xx warnings (plus the three pre-existing MsgPack017 warnings, NL-456); SDK 11 rc1 net10.0 + net11.0 compile check 0 errors; `dotnet format` clean; `HasPendingModelChanges` false x3; **8985** non-Docker tests on net10.0 (both configs), Long simulator 1/1; Docker (net10.0, in-container runner, SQL Server skipped) CLN 39/39 (+3 Explicit) incl. both offer proofs, LND 62/62, `MultiNodeHarnessTests` 5/5 facts (server-database theory not run, NL-429), gossip 28/28, on-chain legacy + anchors 40/40 (+2 Explicit), ABCD 3 x 10/10. Proof B12's "three runs in a row" was met by the lanes on local merges (receive 6 runs, pay per lane E); the integrated branch ran the CLN suite once.

Follow-ups: NL-448 (prune timer), NL-450 (B0-T4), NL-451, NL-452, NL-453, NL-454, NL-455 (all but NL-451 and NL-452 fixed in wave lh1); NL-440 (BOLT 11 blinded paths, dummy hops); NL-449 (onion message harness test fails alone).

### Waves and lanes (for the multi-agent wave workflow)
| Wave | Lane | Files owned (exclusive) | Depends on | Proof |
|---|---|---|---|---|
| **M6** | **M6-A wire + codecs** (OM0) | `src/NLightning.Infrastructure.Serialization/{Payloads/OnionMessagePayloadSerializer.cs, Messages/Types/OnionMessageMessageTypeSerializer.cs}` + the two factory registrations; `src/NLightning.Domain/Protocol/OnionMessages/{OnionMessageTlvsCodec,BlindedPathCodec,SciddirOrPubkeyCodec,MessagePathRecipientDataRules}.cs`; tests in `test/NLightning.Infrastructure.Serialization.Tests/Messages/OnionMessageMessageTests.cs`, `test/NLightning.Domain.Tests/Protocol/OnionMessages/` | M6-0 | vector round trips, codec/rule tables |
| M6 | **M6-B crypto** (OM1) | `src/NLightning.Infrastructure.Bitcoin/Onion/OnionMessages/**`, its DI line in `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs`; `test/NLightning.Integration.Tests/BOLT4/OnionMessageVectorTests.cs`, `test/NLightning.Infrastructure.Bitcoin.Tests/Onion/OnionMessages/` | M6-0, RF1 M5 merged | `blinded-onion-message-onion-test.json` byte-exact (generate, route, packet, decrypt) |
| M6 | **M6-C transport** (OM2-T1, OM2-T2) | `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (513 arm + send method only), `src/NLightning.Infrastructure/Node/Factories/PeerServiceFactory.cs`, `src/NLightning.Domain/Node/Interfaces/{IPeerService,IPeerOnionMessageOutbox}.cs`, `src/NLightning.Application/Node/Services/PeerOutbox.cs`, `src/NLightning.Application/Node/Managers/PeerManager.cs` (outbox port only), `src/NLightning.Application/OnionMessages/OnionMessageRateLimiter.cs`; tests `test/NLightning.Infrastructure.Tests/Node/Services/PeerServiceOnionMessageTests.cs`, `test/NLightning.Application.Tests/Node/`… | M6-0 | transport + limiter unit tests |
| M6 | **M6-D pipeline** (OM2-T3..T5, OM3) | `src/NLightning.Application/OnionMessages/**` except the rate limiter; `src/NLightning.Application/DependencyInjection.cs` (one `AddOnionMessageServices()` line); `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs` (template keys); `test/NLightning.Application.Tests/OnionMessages/**` | M6-0 (codes against the M6-A/B/C interfaces with fakes) | three-node harness |
| M6 | **M6-E Docker proof** | `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnOnionMessageTests.cs`, `test/NLightning.Tests.Utils/Bolt12/MinimalOfferEncoder.cs`, `test/NLightning.Integration.Tests/Docker/Utils/RawOnionMessageRecorder.cs` | all M6 lanes (run by the integrator after the merge; the lane writes the tests against the contracts) | Proof M6 (a)-(e) |
| **B12** | **B12-A codecs** (B0) | `src/NLightning.Domain/Offers/**` except `Interfaces/` (contracts) and `Models/OfferModel.cs`; `test/NLightning.Integration.Tests/BOLT12/**` (vectors + `Bolt12VectorTests`), `test/NLightning.Domain.Tests/Offers/**`, `test/NLightning.Tests.Utils/Vectors/Bolt12Vectors.cs`, the csproj `Content` line for `BOLT12/Vectors/*.json` | B12-0 | format, offers and Merkle vectors byte-exact; CLN captures parse |
| B12 | **B12-B signer** (B1) | `src/NLightning.Infrastructure.Bitcoin/Offers/**`, `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.Bolt12.cs`, the two members in `src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs`, the DI line in `AddBitcoinInfrastructure`; `test/NLightning.Infrastructure.Bitcoin.Tests/Offers/**` | B12-0 (uses B12-A's Merkle root only through the vector's `merkle` values) | `signature-test.json` signature |
| B12 | **B12-C schema (migration owner: `AddBolt12Offers`)** (B2) | `src/NLightning.Infrastructure.Persistence*/**`, `src/NLightning.Infrastructure.Repositories/**`, `src/NLightning.Domain/Offers/Models/OfferModel.cs` mapping, `test/NLightning.Tests.Utils/Mocks/CrashingUnitOfWork.cs`, `test/NLightning.Integration.Tests/Persistence/Bolt12PersistenceTests.cs`, the new cases in `Docker/{PostgresTests,SqlServerTests}.cs` | B12-0 | `HasPendingModelChanges` × 3, round trips |
| B12 | **B12-D receive** (B3) | `src/NLightning.Application/Offers/Receive/**`, `src/NLightning.Application/Offers/OffersServiceCollectionExtensions.cs`, `src/NLightning.Application/Payments/FinalHop/FinalHopProcessor.cs` (B3-T4 only), IPC files for create/list/disable offer (Transport.Ipc DTOs, Daemon IPC + client handlers, Client CLI + printers), `test/NLightning.Application.Tests/Offers/Receive/**`, `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnOfferReceiveTests.cs` | B12-0 (fakes for A/B/C until integration) | unit + `ClnOfferReceiveTests` |
| B12 | **B12-E pay + blinded send** (B4) | `src/NLightning.Application/Offers/Send/**`, `src/NLightning.Application/Payments/Send/Blinded/**`, `src/NLightning.Application/Payments/Routing/PaymentOnionFactory.cs` (blinded mode), `src/NLightning.Application/Payments/Send/PaymentService.cs` (`PayBlindedAsync` only), IPC files for payoffer/fetchinvoice, `test/NLightning.Application.Tests/Offers/Send/**`, `test/NLightning.Application.Tests/Offers/OfferHarnessTests.cs`, `test/NLightning.Integration.Tests/BOLT4/BlindedPaymentOnionVectorTests.cs`, `test/NLightning.Integration.Tests/Docker/Interop/Cln/ClnOfferPayTests.cs` | B12-0 | blinded-payment vector, harness, `ClnOfferPayTests` |

**Migration owner:**
- wave M6: none;
- wave B12: **B12-C** (`AddBolt12Offers`). No other lane touches `Infrastructure.Persistence*` or `Infrastructure.Repositories`.

**`ClientCommand`:**
- M6: none.
- B12: `CreateOffer`, `ListOffers`, `DisableOffer` (B12-D), then `PayOffer` and optional `FetchInvoice` (B12-E).
- The integrator appends them after the current maximum at merge time: 21 is free at `88d046c7` and taken by RF1 R4's `DisconnectPeer` on its branch, so expect **22-26** if nothing else lands first. *Update:* 21-23 are the backup commands, 24 `disconnect` and 25 `withdraw`, so B12 starts at **26**. Lanes use placeholder values in their branches and never edit `ClientCommand.cs`; the integrator edits it and the `NodeServiceExtensions` registrations.

**Seams to reconcile at integration:**
- `PeerService.HandleMessage`: M6-C adds the 513 arm next to RF1 R2's peer-storage arm.
- `PeerServiceFactory`: M6-C and R2 each add an optional argument.
- `FeatureOptions`: flipped by the integrator only (D9).
- `AddApplicationServices`: M6-D and B12-D each add one line.
- `PaymentService`: B12-E and any concurrent payments lane. Rebase onto RF1's switch and final-hop changes first, because B12-D's `FinalHopProcessor` edit is on top of M5's.
- `NLightningTestNode`: no edit expected (DI comes from `AddNltgNodeServices`). If a lane needs a hook, it uses `ConfigureServices`.

---

## 6. Requirements traceability matrix

Everything below is **MISSING** at `88d046c7`. Test prefixes:
- `DT/` Domain.Tests;
- `AT/` Application.Tests;
- `BT/` Infrastructure.Bitcoin.Tests;
- `ST/` Serialization.Tests;
- `IT/` Integration.Tests;
- `IST/` Infrastructure.Tests;
- `CLN/` `IT/Docker/Interop/Cln/…`.

### 6.1 Onion messages
| ID | Level | Task | Test |
|---|---|---|---|
| OM-W-01/02 | MUST | OM0-T1 | `ST/Messages/OnionMessageMessageTests` (vector wire messages) |
| OM-W-03 | MUST | OM1-T3 | `IT/BOLT4/OnionMessageVectorTests` (empty payload case) |
| OM-W-04 | MUST | OM0-T2 | `DT/Protocol/OnionMessages/OnionMessageTlvsCodecTests` |
| OM-W-05/06 | MUST | OM0-T3 | `DT/…/BlindedPathCodecTests`, `BlindedPathOffersVectorTests`, `SciddirOrPubkeyCodecTests` |
| OM-S-01 | MUST | OM1-T2 | vector packet byte-exact |
| OM-S-02 | SHOULD | OM1-T2 | size-choice test (1300 / 32768 / refuse above) |
| OM-S-03 | MUST | OM1-T2, OM2-T4 | `BT/Onion/OnionMessages/OnionMessagePacketBuilderTests` (only field 4 in prefix hops) |
| OM-S-04 | MUST | OM0-T4 | `MessagePathRecipientDataRulesTests` (writer side) |
| OM-S-05 | MUST | OM1-T1, OM1-T2 | vector (Alice's `next_path_key_override`) |
| OM-S-06 | MUST | OM2-T4 | `AT/OnionMessages/ReplyPathFactoryTests` |
| OM-S-07 | SHOULD | B4-T3 | `AT/Offers/Send/OfferPaymentServiceTests` (retry on other path) |
| OM-S-08 | SHOULD | OM1-T1 | equal-length padding test |
| OM-R-01 | MAY | OM2-T2 | `OnionMessageRateLimiterTests`, CLN Proof M6 (d) |
| OM-R-02 | MUST | OM1-T3, OM2-T3 | vector bit flips; `OnionMessageServiceTests` (ignore, no send) |
| OM-R-03 | MUST | OM0-T4 | rules table |
| OM-R-04 | MUST | OM0-T4, OM2-T3 | rules table; service test |
| OM-R-05 | MUST / SHOULD | OM2-T3 | forward by node id / SCID / alias; next path key incl. override |
| OM-R-06 | MUST | OM2-T4 | `PendingReplyRegistryTests` |
| OM-R-07 | MUST | OM2-T3 | two final fields → ignored |
| OM-R-08 | MUST | OM2-T4, OM2-T5 | harness reply; CLN Proof M6 (c) |
| OM-R-09 | SHOULD | OM2-T3 | channelless peer accepted |
| F-01 | — | OM3-T2 | feature-gate tests; CLN Proof M6 (e) |
| F-05 | interop | OM3-T2 | CLN Proof B12 receive (c) |

### 6.2 BOLT 12 encoding and signatures
| ID | Level | Task | Test |
|---|---|---|---|
| B12-ENC-01/02 | MUST | B0-T1 | `IT/BOLT12/Bolt12VectorTests.FormatStrings` (`format-string-test.json`) |
| B12-ENC-03 | MUST | B0-T2 | `offers-test.json` valid/invalid; unknown odd kept |
| B12-ENC-04 | MUST | B0-T3 | range cases in `offers-test.json`; invreq/invoice range tables |
| B12-SIG-01 | MUST | B1-T1 | `signature-test.json` signature |
| B12-SIG-02 | MUST | B0-T5 | `signature-test.json` leaves, branches, root |
| B12-SIG-03 | MUST | B0-T3 | validator: zero or two signatures rejected |

### 6.3 BOLT 12 behavior
| ID | Level | Task | Test |
|---|---|---|---|
| B12-OFR-01/02 | MUST | B3-T1 | `AT/Offers/Receive/OfferServiceTests`; CLN receive (a)(c) |
| B12-OFR-03 | MUST | B0-T3, B4-T3 | `offers-test.json` invalid set; `payoffer` refuses |
| B12-OFR-04 | MUST | B4-T3 | path-vs-issuer destination test |
| B12-IRQ-01 | MUST | B4-T2 | `InvoiceRequestFactoryTests` (raw copy incl. unknown odd; metadata random; payer key derived) |
| B12-IRQ-02 | MUST | B0-T3, B3-T2 | validator table; handler ignores |
| B12-IRQ-03 | MUST | B3-T2 | offer match; arrival-path rule both ways |
| B12-IRQ-04 | MUST / MAY | B3-T2 | amount/quantity/chain/bip353 tables |
| B12-IRQ-05 | SHOULD / MAY | B3-T2, B3-T3 | reply sent; no cached re-send (we always mint) |
| B12-INV-01/02 | MUST / SHOULD | B3-T3 | `OfferInvoiceFactoryTests`; CLN receive (a) (CLN accepts our invoice) |
| B12-INV-03/04/05 | MUST | B0-T3, B4-T2 | `InvoiceVerifierTests` incl. CLN capture; CLN pay (a) |
| B12-ERR-01 | MAY | B3-T2, B4-T3 | `invoice_error` after valid signature only; CLN Proof M6 (c), receive (e) |
| B12-PAY-01 | MUST | B4-T1 | `IT/BOLT4/BlindedPaymentOnionVectorTests`; CLN pay (a) |
| B12-PAY-02 | (inferred) | B4-T1 | harness B as introduction; CLN pay (c) |
| B12-RCV-01 | MUST | B3-T4 (M5 check reused) | `AT/Payments/FinalHop/…`; CLN receive (a) |

### 6.4 Interop proofs
| Proof | Peer | What it proves |
|---|---|---|
| M6 (a)(b) | CLN v26.06.8 | CLN peels and forwards our constructions (prefix + blinded path + override); we accept CLN's forwards |
| M6 (c) | CLN | CLN-built invoice_request and reply path decoded; our reply through a CLN blinded path accepted |
| M6 (d)(e) | CLN | DoS behavior; feature bit seen |
| B12 receive (a)-(e) | CLN | CLN fetches our invoice, verifies our signature and paths, pays through our blinded paths |
| B12 pay (a)-(e) | CLN | we verify CLN's invoice and pay over CLN-made blinded paths, including introduction = us |

---

## 7. Interop notes (verified from changelogs and release notes, not yet from our own runs)
- **Core Lightning.**
  - Onion messages are on by default since **v24.08** (`--experimental-onion-messages` is ignored; `sendonionmessage` was removed, #7461).
  - BOLT 12 is on by default since **v24.11** (#7833); the `--experimental-offers` flag was removed in 25.12 (#8523).
  - Our fixture's `v26.06.8` therefore needs **no extra flags** for either (verified in Proof M6: bit 39 is set by default and onion messages work without flags). `--developer` stays.
  - Verified in Proof M6 (wave M6): `injectonionmessage` takes `path_key` and `message` (the `onion_message_packet` hex; the old parameter name `blinding` is refused with -32602; a bad packet returns -1 `onion_message_parse: can't parse onionpacket`). `decode` accepts a minimal regtest offer (TLVs 2, 10, 22). `fetchinvoice` to an issuer that is not its peer fails with 1003 "could not route or connect directly". An `invoice_error` we send back through CLN's reply path makes `fetchinvoice` fail with code **1004** "Remote node sent failure message" and `data.error` = our text (CLN logs `plugin-offers: Received onion message reply for invoice_request`).
  - RPCs used by the proofs: `offer`, `listoffers`, `fetchinvoice`, `pay`, `xpay` (a plain offer since 25.09), `decode`, `listinvoices`, `injectonionmessage {path_key, message}` (v24.11+), `listpeers`.
  - There is no raw-send RPC. `injectonionmessage` processes an onion as if a peer had sent it, which is how Proof M6 (b) makes CLN send.
  - The hooks `onion_message_recv` and `onion_message_recv_secret` need a plugin, which we do not use.
  - CLN 25.12 requires `option_onion_messages` on the peers in its blinded paths (#8682). It may use the `sciddir` form for introduction nodes, which is why OG8 is needed.
  - CLN adds a peer's path itself when it has no public channels, and replies even when that needs an outgoing connection.
- **LND.**
  - **0.20** (our Docker suite): no onion messages or BOLT 12. BOLT 12 needs the external LNDK.
  - **0.21** (June 2026) forwards onion messages (38/39, pathfinding over 38/39 nodes, rate limits `protocol.onion-msg-*`, a channel-presence gate that `protocol.onion-msg-relay-all` turns off). It has no native BOLT 12 (LND #10220 stage 3, #10736). **Unverified:** which release ships `SendOnionMessage`/`SubscribeOnionMessages`.
  - A forward proof through LND 0.21 is optional once the fixture moves to it.
- **Eclair:** onion messages are on by default and relayed from peers with channels. BOLT 12 since v0.11.0, offer RPCs since v0.12.0 **(versions unverified)**.
- **LDK:** onion messages and offers/refunds are supported, stable in 0.1 **(exact versions unverified)**. LDK connects to reply along reply paths.

---

## 8. Mapping to other plans
| This plan | Other plan | Relation |
|---|---|---|
| OM1, OM2 | ONION M2 (Sphinx, `OnionPacketKind.OnionMessage`), M5 (`IRouteBlindingService`) | reused unchanged; M6 adds message-path rules next to M5's payment rules |
| OM2-T1 | RF1 R2 peer storage (`PeerService` arm, `PeerServiceFactory` optional service) | same pattern; merge seam |
| OM2-T4 | BOLT7 G2 (`IGraphView` node features, channels for `sciddir`) | read-only use |
| B3-T3 | ABCD NL-245 (`channel_update` policy for `r` hints), M5 `BlindedPathId` | payment_relay from the same policy; path_id unchanged |
| B4-T1 | ABCD W6-C `PaymentRoutePlanner`/`PaymentRetryPolicy`, BOLT7 G4 `GraphPathSource` | blinded paths as a new candidate kind; retries unchanged |
| B2-T1 | ABCD W1-C invoices/payments tables | extended; `Bolt11` nullable |
| D9 | root `CLAUDE.md` experimental-features rule | 39 leaves `ExperimentalFeatures` after Proof M6 |

---

## 9. Risks
1. **M5 is still moving.** The RF1 branch names (`IRouteBlindingService`, `BlindedRecipientData`, `BlindedPathId`) may change before the merge. *Mitigation:* the M6-0 contracts commit is written against the merged M5, and M6 does not start before RF1 is integrated.
2. **Vector session key ambiguity** (OM1-T2): the message vector's top-level `session_key` may be the path generator's key rather than the Sphinx session key. *Mitigation:* the peel chain and path generation are asserted separately. If the packet cannot be reproduced, record why (for example a random Sphinx session) and assert decrypt only.
3. **Signature exclusion range and BIP-340 aux randomness** (B12-SIG-02, B1-T1). *Mitigation:* the vectors decide. Verify-only is the fallback for non-deterministic signatures.
4. **DoS:**
   - onion messages are free to send: per-peer and global token buckets, capped outboxes, no connect-to-forward (D6), no error replies;
   - invoice_request floods: handler rate limits, unpaid-invoice caps, no response before the signature check (D10, D11).
   - *Residual:* a peer can make us sign invoices up to the rate limit.
5. **Privacy:**
   - our offers reveal our node id (D2);
   - reply paths through a peer reveal that peer as our neighbour;
   - per-offer keys and dummy hops are deferred.
6. **Introduction node = us** (B12-PAY-02, §3.3) on both the message and the payment side is easy to miss and is exactly what CLN produces when we are its only peer. Covered by the harness and CLN pay (c).
7. **`sciddir` introduction nodes** need our channel list or the graph. A private node without the graph resolves only its own channels. *Mitigation:* a `NoPath` result is surfaced clearly and retried on another path.
8. **CLN behavior drift** (fetchinvoice error texts, `xpay <offer>`, path choices, the requirement that path peers have channels) is marked unverified throughout. The proofs log CLN's version and every value they read.
9. **Schema:** `Invoices.Bolt11` becomes nullable. Every reader of `InvoiceModel.Bolt11` (IPC `listinvoices`, printers) must handle null. The B12-C lane greps all uses.
10. **Three-provider drift:** a single migration owner, `HasPendingModelChanges` on all three, and the SQL Server migration generated even though its container tests are skipped.
11. **Legacy / experimental TLVs:** CLN's recurrence and experimental ranges (1e9+) must be kept raw and copied, never interpreted. Covered by the raw-record design (§3.5).

### Critical files for implementation
- `src/NLightning.Infrastructure/Node/Services/PeerService.cs`
- `src/NLightning.Domain/Protocol/Onion/Interfaces/ISphinxService.cs` and M5's `IRouteBlindingService.cs`
- `src/NLightning.Application/Payments/Send/PaymentService.cs`, `Payments/Routing/PaymentOnionFactory.cs`
- `src/NLightning.Application/Payments/FinalHop/FinalHopProcessor.cs`
- `src/NLightning.Infrastructure.Persistence/Entities/Payment/{InvoiceEntity,PaymentEntity}.cs`
- `src/NLightning.Domain/Node/Options/FeatureOptions.cs`
- `test/NLightning.Integration.Tests/Fixtures/ClnFixture.cs`, `test/NLightning.Integration.Tests/Docker/Utils/ClnClient.cs`

---

## Appendix: compact lane table (for the workflow args)
| Lane | Files owned | Proof |
|---|---|---|
| M6-A | `Infrastructure.Serialization/{Payloads/OnionMessagePayloadSerializer,Messages/Types/OnionMessageMessageTypeSerializer}.cs` + factory registrations; `Domain/Protocol/OnionMessages/{OnionMessageTlvsCodec,BlindedPathCodec,SciddirOrPubkeyCodec,MessagePathRecipientDataRules}.cs`; their tests | wire round trip of the vector's `onion_message`s; codec/rule tables |
| M6-B | `Infrastructure.Bitcoin/Onion/OnionMessages/**` (+ DI line); `IT/BOLT4/OnionMessageVectorTests.cs`; `BT/Onion/OnionMessages/**` | `blinded-onion-message-onion-test.json` byte-exact (generate, route, packet, decrypt) |
| M6-C | `Infrastructure/Node/Services/PeerService.cs` (513 arm + send), `Infrastructure/Node/Factories/PeerServiceFactory.cs`, `Domain/Node/Interfaces/{IPeerService,IPeerOnionMessageOutbox}.cs`, `Application/Node/Services/PeerOutbox.cs`, `Application/Node/Managers/PeerManager.cs` (port), `Application/OnionMessages/OnionMessageRateLimiter.cs`; tests | transport and rate-limiter unit tests |
| M6-D | `Application/OnionMessages/**` (not the limiter), one line in `Application/DependencyInjection.cs`, template keys in `Daemon/Extensions/NodeConfigurationExtensions.cs`; `AT/OnionMessages/**` | three-node harness (forward, reply, 32 KiB, rate limit) |
| M6-E | `IT/Docker/Interop/Cln/ClnOnionMessageTests.cs`, `Tests.Utils/Bolt12/MinimalOfferEncoder.cs`, `IT/Docker/Utils/RawOnionMessageRecorder.cs` | Proof M6 (a)-(e) against CLN v26.06.8 |
| B12-A | `Domain/Offers/**` (not `Interfaces/`, `Models/OfferModel.cs`); `IT/BOLT12/**`; `DT/Offers/**`; `Tests.Utils/Vectors/Bolt12Vectors.cs` | format-string, offers and signature (Merkle) vectors byte-exact; CLN captures |
| B12-B | `Infrastructure.Bitcoin/Offers/**`, `Infrastructure.Bitcoin/Signers/LocalLightningSigner.Bolt12.cs`, 2 members in `Domain/Bitcoin/Interfaces/ILightningSigner.cs`; `BT/Offers/**` | `signature-test.json` signature |
| B12-C (migration owner `AddBolt12Offers`) | `Infrastructure.Persistence*/**`, `Infrastructure.Repositories/**`, `Tests.Utils/Mocks/CrashingUnitOfWork.cs`, `IT/Persistence/Bolt12PersistenceTests.cs`, Postgres/SqlServer round-trip cases | `HasPendingModelChanges` × 3; SQLite + Postgres round trips |
| B12-D | `Application/Offers/Receive/**`, `Application/Offers/OffersServiceCollectionExtensions.cs`, `Application/Payments/FinalHop/FinalHopProcessor.cs` (B3-T4), IPC create/list/disable offer (Transport.Ipc, Daemon, Client); `AT/Offers/Receive/**`; `IT/Docker/Interop/Cln/ClnOfferReceiveTests.cs` | CLN fetches and pays our offer |
| B12-E | `Application/Offers/Send/**`, `Application/Payments/Send/Blinded/**`, `Application/Payments/Routing/PaymentOnionFactory.cs` (blinded mode), `PaymentService.PayBlindedAsync`, IPC payoffer/fetchinvoice; `AT/Offers/Send/**`, `AT/Offers/OfferHarnessTests.cs`, `IT/BOLT4/BlindedPaymentOnionVectorTests.cs`, `IT/Docker/Interop/Cln/ClnOfferPayTests.cs` | `blinded-payment-onion-test.json` byte-exact; harness; we pay CLN's offer |
