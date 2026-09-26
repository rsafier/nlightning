# BOLT 4 Onion Routing: Implementation Plan for NLightning

This plan is for agents that will implement BOLT 4 (Sphinx onion routing) in this repo. Work through it one task at a time. Every repo claim cites a repo-relative path. Claims marked **(unverified)** have not been checked against code and should be confirmed before you rely on them.

- Spec source: `lightning/bolts` master, `04-onion-routing.md`, `02-peer-protocol.md` (HTLC messages), `01-messaging.md` (BigSize/TLV). Vector JSON files live at `https://raw.githubusercontent.com/lightning/bolts/master/bolt04/<name>.json`.
- Status: **M1 and M2 are done** on `wip/fafo`: crypto primitives, canonical BigSize, truncated ints, strict/open TLV streams, OnionPacket and onion TLV types, Sphinx construct/peel, hop payload serializer/validator and the replay cache. See §5 "M1/M2 as built" for the as-built file list and the deviations from this plan. §2 and §4 describe the pre-M1 design and are kept for reference; §3 is updated with the M1 status. **M3 (legacy error onions) is done** too (`wip/fafo` @ `3c625e1`): failure message model/serializer, create/wrap/decrypt byte-exact against `onion-error-test.json` and the inline Returning Errors trace, malformed conversion and an origin-side interpreter; see §5 "M3 as built". M3b (attribution_data), M4, M5 and M6 are not started, and nothing in Application calls the onion code yet (the BOLT 2 plan wires it in N6/N8).
- Status after ABCD wave 0 (`wip/fafo` @ `0b7e617`): M4 prerequisites moved. The BOLT 2 engine raises lock-in, irrevocable-fail and immediate-fulfill events (BOLT2 N4-T4, b166ea0) and is persisted (N5, 4472a8b), and `HtlcEntity.OnionSharedSecret` exists with `IChannelStateDbRepository.Set/GetOnionSharedSecretAsync` (M4-T7 part). Domain contracts for M4 exist: `IForwardingPolicy` + `ForwardingRequest`/`ForwardingDecision`, `ForwardingFee` (BOLT 7 fee formula, 128-bit, rounded down), `RoutingOptions`, `ForwardCircuitModel`, `HtlcOrigin`, `InvoiceModel`/`PaymentModel` and their repository/service ports (2ede2ee, 1390027). `Invoice.Encode` validates (NL-120 fixed). `channel_update` is typed and node-signed, and `FailureChannelUpdateFactory.Encode(ChannelUpdateMessage)` embeds a real update (NL-099 part, e7b5269, 3ba2e4f). No M4 task is done yet: they are ABCD W1-B (processor, final hop, policy, onion/route build), W1-C (tables), W2-B (switch, propagation, replay) and W2-C (send).
- Status after ABCD wave 5 (`wip/fafo` @ `1a5ab49`): no milestone changed, but the M4 switch now takes on-chain events (BOLT 5 O3-T4): `HtlcRemovalKind.OnchainTimeout = 4` (never persisted, no reason bytes) makes `HtlcSwitch` fail the upstream HTLC with our own `permanent_channel_failure` (5af263f), `PaymentService.InterpretFailure` maps it to `PermanentChannelFailure` without a source index, a preimage found on chain fulfills upstream (`OutgoingHtlcFulfilled`, staged into `HtlcRecord.KnownPreimage` by the remote resolver), and `ResumeCircuitAsync` fails a Failed circuit whose on-chain fail was refused (037c04b). An upstream HTLC forwarded onto a force-closed channel is therefore resolved (Docker O3 (d), O4 (e)); a future or unknown commitment still resolves it only at Closed (NL-320). ABCD 3 × 10/10. Open as before: NL-078, NL-265..NL-268, NL-270, NL-279, M3b, M5, MPP.
- Status after ABCD wave 4 (`wip/fafo` @ `6b5d50e`): no milestone changed; wave 4 was BOLT 5 groundwork, net11, signet and interop follow-ups. The ABCD suite stayed green (`scripts/run-abcd.sh 3`, 3 × 10/10) and CLN payments both ways stayed green, now at the corrected feerates (NL-288, NL-289). The on-chain resolution of HTLCs (BOLT 5 O3-T4: fail or fulfill upstream from on-chain events, `HtlcRemovalKind.OnchainTimeout`) is still open, so an upstream HTLC forwarded onto a force-closed channel stays `AwaitingDownstream`. Open as before: NL-078, NL-265..NL-268, NL-270, NL-279, M3b, M5, MPP.
- Status after ABCD wave 3 (`wip/fafo` @ `c92d837`): no milestone changed. W3-D added `IOnionReplayStore` (Domain) and `InMemoryOnionReplayStore` (Infrastructure, `AddOnionReplayStore()`; eb597d7): replay entries owned by the incoming HTLC and expired at its cltv_expiry, ready for a table, but not yet wired into the switch or persisted (NL-078 partial). W3-C added `DustExposureHtlcSwitch`, an `IHtlcSwitch` decorator that fails a locked-in incoming HTLC over the dust limit with `temporary_channel_failure` before forward or preimage (1dbbc1f). W3-A's `HtlcExpiryMonitor` fails back an unresolved incoming HTLC at its deadline with `temporary_node_failure` wrapped with the stored onion secret (36d2270, 06da54b). CLN interop (W3-E): CLN pays our invoice and we pay CLN's (Docker `Interop/Cln`, green). Open: HTLCs added after our shutdown are not failed back (NL-279), NL-265..NL-268, NL-270, M3b, M5, MPP.
- Status after ABCD wave 2 (`wip/fafo` @ `a5675cb`, superseded by the line above): **M4 is done.** `Payments/Switch/HtlcSwitch` (W2-B, ca87313, d1476a4; registered by `AddHtlcSwitchServices` from `AddApplicationServices`, f2f1ef6) peels locked-in HTLCs (M4-T2 wiring). It runs the final hop atomically under a per-payment-hash lock, with the invoice settled in the fulfill's save (M4-T3, NL-253). It forwards by scid with `HtlcForwardingPolicy` and a Pending→Offered circuit (M4-T4). It propagates upstream: fulfill at once, fail only when irrevocable, wrapped with the incoming secret, malformed converted (M4-T5). It replays at startup and on every link-up (M4-T7). `Payments/Send/PaymentService` (W2-C, 6cb279f, 083a726) sends directly or through a route hint and decrypts failures at the origin (M4-T6). Invoices carry `r` hints from the peer's `channel_update` (NL-245, 0870ab1). Proofs: `ThreeNodeSwitchTests` (in-process A→B→C on SQLite with restarts, c4ad8e9), `PaymentHarnessTests`, and the Docker N8 proofs and ABCD suite (LND alice → our bob → our carol → LND david: exact BOLT 7 fees, `incorrect_or_unknown_payment_details` decoded at index 3, restarts of bob with an HTLC in flight, bob as sender and receiver). Deviations: `channel_update` in UPDATE failures is our signed update when its scid matches the onion's, otherwise `len=0` (NL-266); no MPP; no per-call fee limit (NL-270). Not done: persistent replay set (NL-078; in memory by ABCD decision 5), M3b attribution_data, M5 route blinding.
- Status after ABCD wave 1 (`wip/fafo` @ `342d22e`, superseded by the line above): the M4 components exist in `src/NLightning.Application/Payments/` (W1-B, 6156173, 234607e): `IncomingOnionProcessor` (M4-T2 processing), `FinalHopProcessor` (M4-T3, single part), `HtlcForwardingPolicy` (M4-T4 policy), `HintRouteBuilder` + `PaymentOnionFactory` + `PaymentTarget.FromInvoice` (M4-T6 build), `InvoiceService`; proof `ThreeHopPaymentTests` (3-hop onion built from a decoded BOLT11 with hints and peeled by each hop's real processor, fees per BOLT 7, error wrapped twice and decoded at the origin). M4-T7 tables are done (W1-C, 899e36b: invoices, payments + hops with shared secrets, forward circuits, HTLC origins). M4-T1 is done by BOLT2 N6-T1 (a604dff), and `LocalOnlyHtlcSwitch` peels every locked-in HTLC and fails it back (a02afa7). Not done: the switch that calls the processor, policy and final hop (M4-T2 wiring, T4 forward, T5 propagation, replay: W2-B), send (M4-T6 `PaymentService`: W2-C), a persistent replay set (NL-078), atomic final-hop accept (NL-253), invoice route hints (NL-245).

---

## 0. How to use this document (agents)

1. Do milestones in order: M1 → M2 → M3, then M4, which needs the prerequisites in §7. M5 and M6 are optional or later.
2. Each task gives its target files, acceptance criteria and required vectors. A task is done only when:
   - `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121` passes,
   - `dotnet build -c Release.Native -p:MSBuildWarningsAsMessages=MSB4121` passes (this is required for any crypto change),
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` passes, because the `.editorconfig` IDE and naming rules are errors,
   - the new tests pass under both configurations, run with `dotnet test --no-build -c <cfg> --filter 'FullyQualifiedName!~Docker'`.
3. Every test project is discovered by `dotnet test`, including `test/NLightning.Application.Tests` and `test/NLightning.Daemon.Tests` (NL-167); `scripts/check-sln-configs.py` fails CI if one loses its runner.
4. There are no known environmental failures: `PeerAddressTests` is hermetic (NL-168). The non-Docker run has one intentional skip (the NL-056 HTLC second-stage test).
5. The WASM build (`Release.Wasm`) only works on linux-x64 (CI). `src/NLightning.Infrastructure/Crypto/Providers/JS/package.json` pins linux-x64 esbuild/rollup. On macOS, write the JS provider code and let CI verify it.

---

## 1. Spec requirements summary

### 1.1 Primitives and conventions
| Primitive | Definition |
|---|---|
| Shared secret | `ss = SHA256(compressed(k * P))` (libsecp256k1 default ECDH) |
| Key derivation | `key = HMAC-SHA256(key = ASCII(label), msg = 32-byte secret)`, with no NUL terminator |
| PRG / stream | ChaCha20 (RFC 8439) with a **96-bit all-zero nonce**, **block counter 0**, run as a keystream (XOR over zeros). This is not AEAD |
| HMAC compare | Constant time (`CryptographicOperations.FixedTimeEquals`) |
| Blinding factor | `bf = SHA256(ephemeral_pubkey(33) \|\| ss)`; next ephemeral pubkey = `epk * bf` (point tweak-mul); sender privkey = `ek * bf mod n` |

### 1.2 Key-type labels (ASCII → hex)
| Label | Hex | Use |
|---|---|---|
| `rho` | `72686f` | hop_payloads stream; blinded-path `encrypted_recipient_data` AEAD key |
| `mu` | `6d75` | packet HMAC |
| `um` | `756d` | error-packet HMAC; attribution HMACs |
| `pad` | `706164` | initial mix-header fill (keyed from the **session key**) |
| `ammag` | `616d6d6167` | error-packet obfuscation stream |
| `ammagext` | `616d6d6167657874` | attribution_data obfuscation (M3b) |
| `fulfillment` | ASCII | fulfillment_payload AEAD key (optional) |
| `blinded_node_id` | ASCII | route-blinding tweak |

The hex values for `ammag` and `ammagext` were derived from ASCII, not quoted from the spec. `ammag` is now confirmed: `SphinxKeyGeneratorTests` and `OnionVectorTests` reproduce every hop's `ammag_key` from `onion-error-test.json`. `ammagext` is still unconfirmed (M3b). The labels live in `src/NLightning.Domain/Protocol/Onion/Constants/OnionConstants.cs`.

### 1.3 Sizes and constants
| Constant | Value |
|---|---|
| `onion_packet` total | **1366** = version(1) + public_key(33) + hop_payloads(**1300**) + hmac(**32**) |
| Packet version | `0x00`. Any other version fails with `invalid_onion_version` |
| hop payload framing | `bigsize(len) \|\| payload(len, TLV stream) \|\| hmac(32)`; `len` 0 = legacy (unsupported), 1 = reserved, so `len < 2` fails |
| shift_size | `bigsize_len(len) + len + 32` (1 + len + 32 if len < 253, else 3 + len + 32) |
| Peel stream length | 2 × len(hop_payloads) = 2600 for payments (hop_payloads ‖ 1300 zero bytes). The spec says peeling is identical for onion messages with variable `len` (~L909-914, L942), so parameterize the Sphinx core on hop_payloads length now (see §4.1) |
| Error packet | `hmac(32) \|\| u16 failure_len \|\| failuremsg \|\| u16 pad_len \|\| pad`; `failure_len + pad_len >= 256` (SHOULD be = 256, which gives a **292-byte** packet); max 32768 |
| Origin error-decrypt iterations | SHOULD run 27 constant iterations |
| `max_htlc_cltv` | 2016 blocks (`expiry_too_far`) |
| onion_message (M6) | type 513; packet payload 1300 or 32768 bytes (message len 1366 / 32834) |
| Associated data | `payment_hash` for payments; empty for onion messages |

### 1.4 Hop payload TLV (`payload` namespace)
| Type | Name | Encoding |
|---|---|---|
| 2 | amt_to_forward | tu64 |
| 4 | outgoing_cltv_value | tu32 |
| 6 | short_channel_id | 8 bytes (u64 BOLT 7 scid) |
| 8 | payment_data | `[32 payment_secret][tu64 total_msat]` |
| 10 | encrypted_recipient_data | bytes |
| 12 | current_path_key | point (33) |
| 16 | payment_metadata | bytes |
| 18 | total_amount_msat | tu64 |

Writer and reader rules:
- Non-final hop: amt, cltv and scid; no payment_data.
- Final hop: amt and cltv; payment_data if the invoice has a payment_secret; payment_metadata if the invoice has one.
- Blinded non-final hop: **only** encrypted_recipient_data and (optionally) current_path_key; amt_to_forward/outgoing_cltv_value are absent and computed from `payment_relay` (04-onion-routing.md ~L317-321). Any other TLV → error.
- Blinded final hop: only amt, cltv, total_amount_msat, encrypted_recipient_data and current_path_key; amt, cltv and total_amount_msat are required.
- path_key (in `update_add_htlc`) XOR current_path_key (in the payload): error if both are present, or if encrypted_recipient_data is present and neither is. Non-blinded payloads must have neither.
- Final non-blinded hop: error if `total_msat` (in payment_data) is not present (~L339-341).
- Reject unknown **even** types. Reject non-strictly-increasing types. Require minimal encodings for BigSize and truncated ints.

### 1.5 Packet construction (sender)
1. `ek_1 = session_key`. For each hop i compute `ss_i`, `epk_i` and `bf_i`, then `ek_{i+1} = ek_i * bf_i`.
2. Filler: over hops `0..n-2`, left-shift by that hop's shift_size, zero-fill, XOR with the tail of the hop's 2600-byte `rho` stream. Filler length = Σ shift_size(0..n-2). **Implement the variable-length version**, not the spec's legacy fixed-65-byte Go sample.
3. `mix_header = ChaCha20(HMAC("pad", session_key))[0..1300]`.
4. For `i = n-1 .. 0`:
   - right-shift the mix_header by shift_size_i;
   - write `bigsize(len) ‖ payload_i ‖ next_hmac` (`next_hmac` = 32 zero bytes for the last hop);
   - XOR with `rho_i[0..1300]`;
   - for i = n-1 only, overwrite the tail with the filler;
   - `next_hmac = HMAC(mu_i, mix_header ‖ associated_data)`.
5. Output `0x00 ‖ epk_1 ‖ mix_header ‖ next_hmac`.

### 1.6 Packet processing (reader), in this order
1. `version != 0` → `invalid_onion_version`.
2. Invalid pubkey → `invalid_onion_key`.
3. Replay check on the HMAC (payments) (MAY redeem if the preimage is known). The spec lists it here, but **record** the HMAC in `IOnionReplayCache` only after step 5 verified it (check before, record after): recording unauthenticated HMACs lets a peer flush the bounded cache for free and then replay an old onion.
4. If `path_key` is present: `blinding_ss = ECDH(path_key, node_priv)`, then tweak the node privkey by `HMAC("blinded_node_id", blinding_ss)`. `path_key` here is **only** the key received alongside the onion (update_add_htlc TLV 0, or the `onion_message` path_key). It is never the payload's `current_path_key`: an introduction point's onion is encrypted to its real node id, and `current_path_key` is used only afterwards to decrypt `encrypted_recipient_data` and derive the next path_key. (An earlier version of this plan said "or onion current_path_key"; that contradicts BOLT 4.)
5. `ss = ECDH(public_key, node_priv)`. Verify `HMAC(mu, hop_payloads ‖ associated_data)` in constant time. Mismatch → `invalid_onion_hmac`.
6. XOR `hop_payloads ‖ 1300 zeros` with the 2600-byte `rho` stream.
7. Parse bigsize len (must be minimal and `>= 2` for payments; onion messages have no legacy length, so 0 is valid), then the payload, then 32 bytes of next_hmac. If anything is short → `invalid_onion_payload`. That code is not BADONION, so it goes back in `update_fail_htlc` encrypted with `ss`: the peeler exposes `ss` on the exception (`OnionException.SharedSecret`).
8. `next_hmac == 0` → final hop. Otherwise forward with `version 0 ‖ epk * SHA256(epk ‖ ss) ‖ unwrapped[0..1300] ‖ next_hmac`.
9. If a `path_key` came in `update_add_htlc`, every failure above (version, key, HMAC, framing) is reported as `invalid_onion_blinding` with sha256_of_onion (BOLT 4 "Returning Errors"). `SphinxService.Peel` does this remap itself for `OnionPacketKind.Payment`.

### 1.7 Failure codes
Flags: `BADONION=0x8000`, `PERM=0x4000`, `NODE=0x2000`, `UPDATE=0x1000`. `failuremsg = u16 code ‖ data ‖ [optional TLV stream]`. The origin MUST ignore trailing bytes.

| Code | Name | Data |
|---|---|---|
| 0x2002 | temporary_node_failure | – |
| 0x6002 | permanent_node_failure | – |
| 0x6003 | required_node_feature_missing | – |
| 0xC004 | invalid_onion_version | sha256_of_onion |
| 0xC005 | invalid_onion_hmac | sha256_of_onion |
| 0xC006 | invalid_onion_key | sha256_of_onion |
| 0x1007 | temporary_channel_failure | u16 len ‖ channel_update |
| 0x4008 | permanent_channel_failure | – |
| 0x4009 | required_channel_feature_missing | – |
| 0x400A | unknown_next_peer | – |
| 0x100B | amount_below_minimum | u64 htlc_msat ‖ u16 len ‖ channel_update |
| 0x100C | fee_insufficient | u64 htlc_msat ‖ u16 len ‖ channel_update |
| 0x100D | incorrect_cltv_expiry | u32 cltv_expiry ‖ u16 len ‖ channel_update |
| 0x100E | expiry_too_soon | u16 len ‖ channel_update |
| 0x400F | incorrect_or_unknown_payment_details | u64 htlc_msat ‖ u32 height |
| 0x0012 | final_incorrect_cltv_expiry | u32 cltv_expiry |
| 0x0013 | final_incorrect_htlc_amount | u64 incoming_htlc_amt |
| 0x1014 | channel_disabled | u16 disabled_flags ‖ u16 len ‖ channel_update |
| 0x0015 | expiry_too_far | – |
| 0x4016 | invalid_onion_payload | bigsize type ‖ u16 offset |
| 0x0017 | mpp_timeout | – |
| 0xC018 | invalid_onion_blinding | sha256_of_onion |

`channel_update` is now optional (`len = 0` is allowed). This matters here because BOLT 7 `channel_update` is not implemented (§7).

BOLT 2 rules:
- `update_fail_malformed_htlc` (135): the receiver MUST reject it if the BADONION bit is missing. Otherwise the receiver converts it into an upstream `update_fail_htlc`, wrapping `failure_code ‖ sha256_of_onion` with its own shared secret.
- `update_fail_htlc` (131) optionally carries TLV 1 `attribution_data` = 20×u32 hold times ‖ 210×4-byte truncated HMACs (920 bytes). See M3b.

### 1.8 Official test vectors (all required)
| File | Used in |
|---|---|
| `bolt04/onion-test.json` (5 hops, session key `0x41`×32, assoc data `0x42`×32, one 275-byte payload) | M2 |
| `bolt04/onion-error-test.json` (failure 0x2002 from hop 4, per-hop shared secret/ammag, 292-byte packet) | M3 |
| Inline BOLT 4 "Test Vector > Returning Errors" (attribution) and "Returning success" traces | M3b / optional |
| `bolt04/route-blinding-test.json` | M5 |
| `bolt04/blinded-payment-onion-test.json` | M5 |
| `bolt04/blinded-onion-message-onion-test.json` | M6 |
| BOLT 1 Appendix A (BigSize) and Appendix B (TLV decoding) | M1 |

Always fetch them from the canonical URLs above and commit them under the test project's `Vectors/` folder.

---

## 2. Existing primitives to reuse (verified)

> Pre-M1 snapshot. After M1: `Hkdf` delegates to the public `HmacSha256`; the EC helpers moved out of `KeyDerivationService` into `ISecp256K1Math`; `BigSizeTypeSerializer` is canonical; `TlvStreamSerializer` looks converters up by runtime type (no closed switch); and `UpdateAddHtlcPayload.OnionRoutingPacket` is a mandatory 1366-byte `ReadOnlyMemory<byte>`. Line numbers below may be stale.

| Need | Existing API | Location | Notes |
|---|---|---|---|
| ECDH shared secret | `IEcdh.SecP256K1Dh(PrivKey k, ReadOnlySpan<byte> rk, Span<byte> sharedKey)` | interface `src/NLightning.Infrastructure/Crypto/Interfaces/IEcdh.cs`; impl `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs` (NBitcoin `GetSharedPubkey` then SHA256 of `Compress()`) | Already the BOLT 4 definition. Singleton registered in `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs`. Its only test checks key length (`test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/Functions/EcdhTests.cs`) |
| Session key / keypair | `IEcdh.GenerateKeyPair()` | same | NBitcoin `new Key()` |
| Node private key | `ISecureKeyManager.GetNodeKeyPair()` | `src/NLightning.Domain/Protocol/Interfaces/ISecureKeyManager.cs` | Needed for peel. `ILightningSigner` exposes only the node pubkey |
| SHA256 | `ISha256` / `new Sha256()` | `src/NLightning.Domain/Crypto/Hashes/ISha256.cs`, `src/NLightning.Infrastructure/Crypto/Hashes/Sha256.cs` | Every instance allocates provider memory, so reuse one per packet operation |
| EC tweak-mul (pub/priv) | `MultiplyPubKey`, `MultiplyPrivateKey` (**private static**) | `src/NLightning.Infrastructure.Bitcoin/Services/KeyDerivationService.cs` (~L167, ~L209) on `NLightningCryptoContext.Instance` (`src/NLightning.Infrastructure.Bitcoin/Crypto/Contexts/NLightningCryptoContext.cs`) | Must be extracted (M1-T3) |
| HMAC-SHA256 | `Hkdf.HmacHash` (**private**, `Debug.Assert(key.Length == 32)`) | `src/NLightning.Infrastructure/Crypto/Functions/Hkdf.cs` | Cannot be reused as is because labels are 2–15 bytes |
| ChaCha20-Poly1305 AEAD | `ChaCha20Poly1305.Encrypt/Decrypt(key, ulong nonce, ad, in, out)` | `src/NLightning.Infrastructure/Crypto/Ciphers/ChaCha20Poly1305.cs` | nonce 0 → 12 zero bytes. Use it for route-blinding `encrypted_recipient_data` (M5). **Do not** derive a keystream from it: AEAD starts at counter 1 |
| Constant-time compare | `CryptographicOperations.FixedTimeEquals` | used in `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs` | Never compare HMACs with value-object `Equals`, which uses `SequenceEqual` |
| BigSize | `BigSize` value object; `BigSizeTypeSerializer` | `src/NLightning.Domain/Protocol/ValueObjects/BigSize.cs`; `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs` | Decoder is **not canonical**. Fix it in M1 |
| TLV model | `BaseTlv`, `TlvStream` (file `TLVStream.cs`), `ITlvConverter<T>`, `TlvConverterFactory` | `src/NLightning.Domain/Protocol/Tlv/BaseTlv.cs`, `src/NLightning.Domain/Protocol/Models/TLVStream.cs`, `src/NLightning.Domain/Protocol/Interfaces/ITlvConverter.cs`, `src/NLightning.Infrastructure/Protocol/Factories/TlvConverterFactory.cs` | `TlvStream` re-sorts silently and has no unknown-even check |
| TLV wire | `TlvSerializer`, `TlvStreamSerializer` | `src/NLightning.Infrastructure.Serialization/Tlv/` | `SerializeAsync` has a **closed** pattern switch (unknown TLV class → `SerializationException`). Deserialize reads to `stream.Length` |
| Route-blinding TLV on update_add_htlc | `BlindedPathTlv` (type 0, `CompactPubKey PathKey`) + converter | `src/NLightning.Domain/Protocol/Tlv/BlindedPathTlv.cs`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/BlindedPathTlvConverter.cs` | Used in M5 |
| Value objects | `PrivKey`, `CompactPubKey`, `Secret`, `Hash`, `CryptoKeyPair`, `ShortChannelId`, `LightningMoney` | `src/NLightning.Domain/Crypto/ValueObjects/`, `src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs`, `src/NLightning.Domain/Money/LightningMoney.cs` | See gotchas in §7 (ShortChannelId ulong-ctor mask bug) and §9 (`LightningMoney` implicit conversions = msat) |
| Feature bits | `VarOnionOptin=9`, `OptionRouteBlinding=25`, `OptionAttributionData=37`, `OptionOnionMessages=39` | `src/NLightning.Domain/Enums/Feature.cs` | RouteBlinding and AttributionData default to No and are in `FeatureOptions.ExperimentalFeatures`, so they are refused unless `Features:AllowExperimentalFeatures=true` (NL-074, NL-206) |
| Wire messages | `UpdateAddHtlcMessage/Payload`, `UpdateFailHtlcPayload` (opaque `Reason`), `UpdateFailMalformedHtlcPayload` (`Sha256OfOnion`, `ushort FailureCode`) + serializers + `IMessageFactory.Create*` | `src/NLightning.Domain/Protocol/{Messages,Payloads}/`, `src/NLightning.Infrastructure.Serialization/Payloads/`, `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` | Onion is `ReadOnlyMemory<byte>? OnionRoutingPacket` (`UpdateAddHtlcPayload.cs:54`) |

## 3. Missing primitives and types (status after M1)

Everything in this section was delivered in M1. The original gap is kept in each item, and the item says where the fix lives.

### 3.1 Raw hooks (now typed or fixed)
- **Done (M1-T9).** `UpdateAddHtlcPayload.OnionRoutingPacket` used to be optional raw bytes, and a truncated message was silently accepted with a null onion.
  - It is now a mandatory `ReadOnlyMemory<byte>` of exactly `OnionConstants.PacketLength`, enforced by the constructor.
  - `UpdateAddHtlcPayloadSerializer` reads it with `ReadExactlyAsync`, so a short read throws.
  - `IMessageFactory.CreateUpdateAddHtlcMessage` takes it as non-nullable.
  - It stays raw bytes, not a typed `OnionPacket`. Parse it with `new OnionPacket(bytes.Span)`.
- **Done (M1-T9).** `UpdateAddHtlcMessageSerializer` looks up the blinded path with `TlvConstants.BlindedPath`. It reads the extension with `DeserializeStrictAsync`, so unknown even types are rejected, and a malformed blinded path is rejected too.

### 3.2 Crypto primitives
| Primitive | Status | Where |
|---|---|---|
| Raw ChaCha20 keystream (IETF, 12-byte zero nonce, counter 0) | done (M1-T2) | `ICryptoProvider.StreamChaCha20IetfXor` (named with the `Stream` prefix, not `ChaCha20IetfXor` as planned) in all three providers:<br>• libsodium `crypto_stream_chacha20_ietf_xor` (`Providers/Libsodium/LibsodiumWrapper.cs`, `SodiumCryptoProvider.cs`)<br>• Native BouncyCastle `ChaCha7539Engine` (`Providers/Native/NativeCryptoProvider.cs`)<br>• JS `sodium.crypto_stream_chacha20_ietf_xor` (`Providers/JS/LibsodiumJsWrapper.cs`, `SodiumJsCryptoProvider.cs`; compiles only in CI `Release.Wasm`; `blazorSodium.js` was not edited, and it is unverified whether the sumo export needs a re-export there)<br>Wrapper: `src/NLightning.Infrastructure/Crypto/Ciphers/ChaCha20Stream.cs` (`GenerateStream`, `Xor`). |
| HMAC-SHA256, any key length | done (M1-T1) | `src/NLightning.Infrastructure/Crypto/Functions/HmacSha256.cs` (`ComputeHash(key, data, out)` and a two-part `ComputeHash(key, data1, data2, out)`). `Hkdf` now delegates to it. |
| EC tweak-mul / tweak-add | done (M1-T3) | `src/NLightning.Domain/Crypto/Interfaces/ISecp256K1Math.cs` (`MultiplyPubKey`, `MultiplyPrivKey`, `AddPubKeys`, `AddPrivKeys`), implemented by `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Secp256K1Math.cs` (singleton). `KeyDerivationService` uses it. |

### 3.3 Serialization support
- **Canonical BigSize (M1-T5): done.** `BigSizeTypeSerializer` rejects non-minimal encodings, and the three vectors in `test/NLightning.Infrastructure.Serialization.Tests/Vectors/BigSize.txt` are re-enabled.
- **Truncated ints (M1-T6): done.**
  - `src/NLightning.Domain/Protocol/Tlv/TruncatedInt.cs` is the single strict decoder/encoder, used by the Domain TLV constructors and the Infrastructure converters (NL-084 removed the encode-only Domain copy and moved the codec out of Infrastructure).
  - `EndianBitConverter` trim/pad is still non-compliant and is not used for onion fields.
- **Strict and open TLV streams (M1-T7): done.**
  - `TlvStreamSerializer` serializes through `ITlvConverterFactory.GetConverter(Type)` and writes a raw `BaseTlv` verbatim.
  - `DeserializeAsync` enforces strictly increasing types and length <= remaining.
  - `DeserializeStrictAsync(stream, knownTypes)` also rejects unknown even types.
  - Tests: `TlvStreamBolt1VectorTests.cs` covers BOLT 1 Appendix B, and `TlvStreamSerializerTests.cs` checks that every registered converter, `RemoteAddressTlv` included, serializes.

## 4. Proposed types and placement

Naming follows repo conventions:
- file-scoped namespace, with relative `using` placed after the namespace line;
- one type per file;
- `_camelCase` / `s_camelCase` fields;
- tests named `Given_X_When_Y_Then_Z`;
- `*Tlv : BaseTlv` with a matching `*TlvConverter : ITlvConverter<T>`.

Onion TLV type numbers MUST NOT go into the flat `src/NLightning.Domain/Protocol/Constants/TlvConstants.cs`, where numbers already collide by design. Give them their own constants classes.

### 4.1 Domain (`src/NLightning.Domain`)
| File | Type | Purpose |
|---|---|---|
| `Protocol/Onion/Constants/OnionConstants.cs` | `static class OnionConstants` ([ExcludeFromCodeCoverage]) | `PacketLength=1366`, `HopPayloadsLength=1300`, `HmacLength=32`, `Version=0`, `MaxErrorPacketLength=32768`, `MinFailurePadLength=256`, `MaxHtlcCltv=2016`, key labels as `ReadOnlySpan<byte>`/`byte[]` (`Rho`, `Mu`, `Um`, `Pad`, `Ammag`, `AmmagExt`, `BlindedNodeId`, `Fulfillment`) |
| `Protocol/Onion/Constants/OnionPayloadTlvTypes.cs` | static BigSize fields 2,4,6,8,10,12,16,18 | hop payload namespace |
| `Protocol/Onion/Constants/EncryptedDataTlvTypes.cs` | 1,2,4,6,8,10,12,14 | route-blinding namespace (M5) |
| `Protocol/Onion/Constants/FailureTlvTypes.cs` | failure TLV namespace | M3 |
| `Protocol/Onion/ValueObjects/OnionPacket.cs` | `readonly struct OnionPacket : IValueObject` (`byte Version`, `ReadOnlyMemory<byte> PublicKey` (33 raw bytes, **not** `CompactPubKey`), `ReadOnlyMemory<byte> HopPayloads`, `ReadOnlyMemory<byte> Hmac` (32)); the ctor validates **only** total length and never the version byte or pubkey prefix; `ToBytes()` / from raw bytes | typed packet. `CompactPubKey`'s ctor throws on a prefix other than 0x02/0x03 (`src/NLightning.Domain/Crypto/ValueObjects/CompactPubKey.cs:10-19`). If deserialization rejected bad versions or keys, `update_add_htlc` parsing would fail and the spec-required `update_fail_malformed_htlc` with `invalid_onion_version`/`invalid_onion_key` (04-onion-routing.md ~L918-921) could not be sent. Version and pubkey validation belong in `Peel` |
| `Protocol/Onion/Models/HopPayload.cs` | `HopPayload` with a `TlvStream` plus typed accessors, **all nullable** (AmtToForward?, OutgoingCltvValue?, ShortChannelId?, PaymentData?, EncryptedRecipientData?, CurrentPathKey?, PaymentMetadata?, TotalAmountMsat?) | parsed per-hop payload |
| `Protocol/Onion/Models/OnionHop.cs` | `(CompactPubKey NodeId, ReadOnlyMemory<byte> Payload)`. `Payload` is the serialized TLV stream **without** the bigsize length prefix (the builder adds it) | construction input. Raw bytes because `NLightning.Infrastructure.Bitcoin` has no reference to `NLightning.Infrastructure.Serialization` (`src/NLightning.Infrastructure.Bitcoin/NLightning.Infrastructure.Bitcoin.csproj:49`), so the builder cannot call `HopPayloadSerializer`. Callers (Application) serialize `HopPayload` first. This also lets unknown odd TLVs pass through verbatim |
| `Protocol/Onion/Models/PeeledOnion.cs` | `(HopPayload Payload, Secret SharedSecret, OnionPacket? NextPacket, bool IsFinal)` | peel output |
| `Protocol/Onion/Tlv/AmtToForwardTlv.cs`, `OutgoingCltvValueTlv.cs`, `OnionShortChannelIdTlv.cs`, `PaymentDataTlv.cs`, `EncryptedRecipientDataTlv.cs`, `CurrentPathKeyTlv.cs`, `PaymentMetadataTlv.cs`, `TotalAmountMsatTlv.cs` | `: BaseTlv` | typed records. Do not reuse `Protocol/Tlv/ShortChannelIdTlv.cs` (channel_ready namespace) |
| `Protocol/Onion/Enums/FailureCode.cs` | `enum FailureCode : ushort` (all codes in §1.7) | failure codes |
| `Protocol/Onion/Enums/FailureCodeFlags.cs` | `[Flags] enum : ushort { BadOnion=0x8000, Perm=0x4000, Node=0x2000, Update=0x1000 }` + extension `IsBadOnion()` etc. | flag helpers |
| `Protocol/Onion/Models/FailureMessage.cs` | `(FailureCode Code, ReadOnlyMemory<byte> Data, TlvStream? Extension)` + typed factory methods per code | failuremsg |
| `Protocol/Onion/Models/DecryptedFailure.cs` | `(int ErringHopIndex, FailureMessage Message)` | origin-side result |
| `Protocol/Onion/Interfaces/ISphinxService.cs` | `OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> associatedData, int hopPayloadsLength = OnionConstants.HopPayloadsLength)`, `PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, CompactPubKey? pathKey = null)` (length taken from the packet) | Sphinx core. The builder, peeler and filler work on any hop_payloads length (stream = 2 × len), so M6 onion messages (1300 or 32768) reuse them unchanged. The 1366-byte check belongs at the `update_add_htlc` boundary (serializer), not in `OnionPacket`/Sphinx |
| `Protocol/Onion/Interfaces/IFailureOnionService.cs` | `byte[] CreateErrorPacket(Secret ss, FailureMessage msg)`, `byte[] WrapErrorPacket(Secret ss, ReadOnlySpan<byte> packet)`, `DecryptedFailure? DecryptErrorPacket(IReadOnlyList<Secret> hopSecrets, ReadOnlySpan<byte> packet)` | error onion |
| `Protocol/Onion/Interfaces/IOnionReplayCache.cs` | `bool TryAdd(ReadOnlySpan<byte> hmac)` | replay protection |
| `Crypto/Interfaces/ISecp256K1Math.cs` (or `Crypto/Functions/`) | `CompactPubKey MultiplyPubKey(CompactPubKey, ReadOnlySpan<byte> scalar)`, `PrivKey MultiplyPrivKey(PrivKey, ReadOnlySpan<byte> scalar)`, `AddPubKeys`, `AddPrivKeys` | EC math port |
| `Exceptions/OnionException.cs` | `OnionException : ErrorException` carrying `FailureCode`, `ReadOnlyMemory<byte>? Data` | peel/validation failure signal |

The Domain project has no NuGet refs (`src/NLightning.Domain/NLightning.Domain.csproj`). Keep all of the above BCL-only.

### 4.2 Infrastructure (crypto)
| File | Type |
|---|---|
| `src/NLightning.Infrastructure/Crypto/Functions/HmacSha256.cs` | public HMAC (any key length; keys > 64 B hashed first per RFC 2104), reusing one `Sha256` |
| `src/NLightning.Infrastructure/Crypto/Ciphers/ChaCha20Stream.cs` | public wrapper over `ICryptoProvider.ChaCha20IetfXor`: `GenerateStream(key, Span<byte> output)` and `Xor(key, in, out)` (nonce fixed at 12 zero bytes) |
| `src/NLightning.Infrastructure/Crypto/Interfaces/ICryptoProvider.cs` + 3 providers | new `ChaCha20IetfXor` |
| `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Secp256K1Math.cs` | `ISecp256K1Math` impl (extracted from `KeyDerivationService`) |
| `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxKeyGenerator.cs` | `DeriveKey(label, ss)`, `ComputeBlindingFactor(epk, ss)` |
| `src/NLightning.Infrastructure.Bitcoin/Onion/OnionBuilder.cs` | construction + filler (internal) |
| `src/NLightning.Infrastructure.Bitcoin/Onion/OnionPeeler.cs` | peel (internal) |
| `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxService.cs` | `ISphinxService` facade over builder and peeler |
| `src/NLightning.Infrastructure.Bitcoin/Onion/FailureOnionService.cs` | `IFailureOnionService` |
| `src/NLightning.Infrastructure.Bitcoin/Onion/RouteBlinding/*` | M5 |
| `src/NLightning.Infrastructure/Protocol/Onion/OnionReplayCache.cs` | in-memory `IOnionReplayCache` (bounded; persistent version later) |

Why Infrastructure.Bitcoin: Sphinx needs secp256k1 point math (NBitcoin), and `IEcdh` is implemented there. It also gets `ISha256`/`HmacSha256`/`ChaCha20Stream` from `NLightning.Infrastructure` through its project reference. Register the services in `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs` (`AddBitcoinInfrastructure`), next to `IEcdh`. Anything needing `ISecureKeyManager` (node key for peel) takes it by constructor injection, because it is registered in the Daemon (`src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`) **and** by hand in the Docker integration tests (`test/NLightning.Integration.Tests/Docker/AbcNetworkTests.cs`, `ChannelOpeningFlowTests.cs`). Keep both DI graphs in sync.

### 4.3 Serialization
| File | Purpose |
|---|---|
| `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs` | canonical decode fix |
| `src/NLightning.Infrastructure.Serialization/Onion/TruncatedIntSerializer.cs` (or `src/NLightning.Infrastructure/Converters/TruncatedInt.cs`) | tu16/tu32/tu64 encode and strict decode |
| `src/NLightning.Infrastructure.Serialization/Tlv/TlvStreamSerializer.cs` | open-ended serialize; optional strict mode (`DeserializeStrictAsync(stream, knownTypes)`) |
| `src/NLightning.Infrastructure.Serialization/ValueObjects/OnionPacketTypeSerializer.cs` | fixed-layout 1366; register in `Factories/ValueObjectSerializerFactory.cs` |
| `src/NLightning.Infrastructure.Serialization/Onion/HopPayloadSerializer.cs` | `bigsize len ‖ strict TLV stream` over a bounded sub-stream (the HMAC is handled by the peeler) |
| `src/NLightning.Infrastructure.Serialization/Onion/FailureMessageSerializer.cs` | `u16 code ‖ data ‖ tlv` plus error-packet framing (`u16 failure_len ‖ msg ‖ u16 pad_len ‖ pad`) |
| `src/NLightning.Infrastructure/Protocol/Tlv/Converters/Onion/*TlvConverter.cs` | one converter per onion TLV; register in `TlvConverterFactory.RegisterConverters`. **Registration alone is not enough:** `TlvStreamSerializer.SerializeAsync` (`src/NLightning.Infrastructure.Serialization/Tlv/TlvStreamSerializer.cs:27-58`) is a closed type switch that throws for any unlisted type. `RemoteAddressTlv` is registered (`TlvConverterFactory.cs:21-33`) but has no switch arm. M1-T7 replaces the switch with a registry lookup |
| `src/NLightning.Infrastructure.Serialization/Payloads/UpdateAddHtlcPayloadSerializer.cs` | always write/read exactly `OnionConstants.PacketLength` bytes |
| `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateAddHtlcMessageSerializer.cs:68` | use `TlvConstants.BlindedPath` |

Register new serializers in `src/NLightning.Infrastructure.Serialization/DependencyInjection.cs` if they are exposed as services.

### 4.4 Application (`src/NLightning.Application`)
| File | Purpose |
|---|---|
| `Channels/Handlers/UpdateAddHtlcMessageHandler.cs` | BOLT 2 add validation + store the HTLC. **Does not peel**, and needs a `case MessageTypes.UpdateAddHtlc` in `Channels/Managers/ChannelManager.cs` (switch at ~L88; the `default` branch throws `ChannelErrorException`, which makes `PeerManager` disconnect the peer) |
| `Channels/Handlers/UpdateFulfillHtlcMessageHandler.cs`, `UpdateFailHtlcMessageHandler.cs`, `UpdateFailMalformedHtlcMessageHandler.cs` | settle/fail downstream HTLCs; malformed: check BADONION, convert upstream |
| `Payments/Services/HtlcSwitch.cs` (+ `IHtlcSwitch` in Domain) | after lock-in (post revoke_and_ack): `ISphinxService.Peel`, then decide final / forward / fail; schedule outgoing messages via `ChannelManager.OnResponseMessageReady` (the existing cross-peer send pattern) |
| `Payments/Services/HtlcForwardingPolicy.cs` | fee/CLTV-delta checks → failure codes 0x100B/0x100C/0x100D/0x100E/0x0015/0x400A |
| `Payments/Services/FinalHopProcessor.cs` | invoice lookup, payment_secret/total_msat/MPP (0x400F, 0x0012, 0x0013, 0x0017) |
| `Payments/Managers/PaymentManager.cs` | send side: build route, `ISphinxService.Construct` with `payment_hash` as associated data, `IMessageFactory.CreateUpdateAddHtlcMessage(..., onion)`, keep per-hop shared secrets for error decryption |

`IChannelMessageHandler<T>` implementations are registered automatically by reflection (`src/NLightning.Application/DependencyInjection.cs`). Services such as HtlcSwitch must be added explicitly.

### 4.5 Persistence (M4)
New columns and tables need migrations in **all three** provider projects (`src/NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}`) via `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh <CamelCaseName>`:
- `HtlcEntity` (`src/NLightning.Infrastructure.Persistence/Entities/Channel/HtlcEntity.cs`): `OnionSharedSecret byte[]?`, `WasBlinded bool` (path_key or current_path_key set), and a failure reason blob.
- `HtlcForwardEntity`: in (channel, htlc_id) → out (channel, htlc_id), amounts, CLTVs.
- `PaymentAttemptEntity`: route, per-hop shared secrets or the session key, status.
- `InvoiceEntity`: payment_hash, preimage, payment_secret, amount, expiry, status.

Alternatively, the shared secret can be recomputed from `HtlcEntity.AddMessageBytes`, which already stores the full `UpdateAddHtlcMessage` including the onion, plus the node key. That avoids a column at the cost of an ECDH per failure.

---

## 5. Milestones

### M1: Primitives and serialization groundwork (no protocol behavior) — DONE

All tasks are done. For the as-built files, see §5 "M1/M2 as built".

| Task | Files | Acceptance / vectors |
|---|---|---|
| **M1-T1** HMAC-SHA256 | `src/NLightning.Infrastructure/Crypto/Functions/HmacSha256.cs`; test `test/NLightning.Infrastructure.Tests/Crypto/Functions/HmacSha256Tests.cs` | RFC 4231 test cases 1–4, 6 and 7 (key lengths 20, 4, 20, 25, 131; keys > 64 B hashed first). Also `HMAC("rho", ss)` equals `System.Security.Cryptography.HMACSHA256.HashData` (cross-check in the test only) |
| **M1-T2** ChaCha20 keystream in all three providers | `ICryptoProvider.cs`; `Providers/Libsodium/LibsodiumWrapper.cs`, `SodiumCryptoProvider.cs`; `Providers/Native/NativeCryptoProvider.cs`; `Providers/JS/LibsodiumJsWrapper.cs`, `SodiumJsCryptoProvider.cs`; wrapper `Crypto/Ciphers/ChaCha20Stream.cs`; tests `test/NLightning.Infrastructure.Tests/Crypto/Ciphers/ChaCha20StreamTests.cs` | RFC 8439 §2.4.2 vector **with counter 0 adapted**, or libsodium `crypto_stream_chacha20_ietf` known-answer; the keystream for key=0, nonce=0 matches RFC 8439 A.1 test vector #1 (counter 0). Passes under `-c Release` **and** `-c Release.Native`. JS path compiles in CI (`Release.Wasm`); add a Blazor test in `test/BlazorTests/NLightning.Blazor.Tests` if the page is extended (optional) |
| **M1-T3** EC math extraction | `src/NLightning.Domain/Crypto/Interfaces/ISecp256K1Math.cs` (or place under an existing Domain folder); `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Secp256K1Math.cs`; refactor `src/NLightning.Infrastructure.Bitcoin/Services/KeyDerivationService.cs` to call it; DI in `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs` | BOLT 3 Appendix E vectors in `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs` still pass (regression guard). New unit tests: `k*(bf*G) == (k*bf)*G`; invalid scalar (0 or ≥ n) throws |
| **M1-T4** ECDH vector test | `test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/Functions/EcdhTests.cs` | `onion-test.json` has no shared secrets or ephemeral keys (only `generate.{session_key, associated_data, hops[{pubkey,payload}]}`, `onion`, and `decode` = hop privkeys 0x41..0x45 ×32). Use `onion-error-test.json` `generate.hops[i].hop_shared_secret` (same keys, per its comment). In M1 assert hop 0 only: `ECDH(session_key 0x41×32, hops[0].pubkey) == 53eb63ea...` and `ECDH(decode[0] privkey, pub(session_key)) == 53eb63ea...`. Hops 1-4 need the epk blinding chain, so verify them in M2-T1 |
| **M1-T5** Canonical BigSize | `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs`; re-enable the 3 vectors in `test/NLightning.Infrastructure.Serialization.Tests/Vectors/BigSize.txt` | BOLT 1 Appendix A: all decode vectors including "not canonical" → exception |
| **M1-T6** Truncated ints | new `TruncatedInt` helper + tests `test/NLightning.Infrastructure.Serialization.Tests/ValueObjects/TruncatedIntTests.cs` | tu64 BOLT 1 Appendix B vectors: 0 → empty; leading zero byte → reject; length > 8/4/2 → reject |
| **M1-T7** Strict and open TLV stream | `src/NLightning.Infrastructure.Serialization/Tlv/TlvStreamSerializer.cs`, `TlvSerializer.cs` | BOLT 1 Appendix B "TLV decoding failures" (for the `n1` namespace, adapt it: known types {1,2,3,254}) all fail; successes round-trip. Existing message tests (`test/NLightning.Infrastructure.Serialization.Tests/Messages/*`) still pass. Required design: replace the closed switch with a runtime-type lookup (converter by `tlv.GetType()` through the non-generic `ITlvConverter.ConvertToBase(BaseTlv)`), falling back to raw `BaseTlv` when the runtime type is exactly `BaseTlv`. Raw `BaseTlv` serializes without throwing. Add a test that every converter registered in `TlvConverterFactory` (including `RemoteAddressTlv`) serializes through `TlvStreamSerializer` |
| **M1-T8** Domain onion types (no crypto) | §4.1 files: `OnionConstants`, TLV type classes, `OnionPacket`, onion TLVs + converters, `FailureCode` + flags; tests in `test/NLightning.Domain.Tests/Protocol/Onion/` and `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/Onion/` | `OnionPacket` rejects only a wrong total length. It accepts any version byte and any 33 pubkey bytes, including a bad prefix (see §4.1); `Peel` throws `OnionException(InvalidOnionVersion/InvalidOnionKey)` (M2-T3). Each converter round-trips and rejects the wrong type or length. `FailureCode.InvalidOnionHmac.IsBadOnion()` is true |
| **M1-T9** Fix update_add_htlc onion framing | `UpdateAddHtlcPayload.cs` (make the onion non-nullable or typed `OnionPacket`), `UpdateAddHtlcPayloadSerializer.cs`, `UpdateAddHtlcMessageSerializer.cs:68`, `IMessageFactory`/`MessageFactory.CreateUpdateAddHtlcMessage`; update `test/NLightning.Infrastructure.Serialization.Tests/Messages/UpdateAddHtlcMessageTests.cs` | Short buffer → exception, not a null onion. Round trip unchanged for the existing vector. Note: `HtlcDbRepository` stores serialized `UpdateAddHtlcMessage` bytes, so existing rows with no onion would fail to deserialize. There are none in practice because no HTLC path exists (unverified; check the DBs if any exist) |

Before starting M2, M1 must build and test green in Release and Release.Native.

### M2: Packet construction and peeling (bolt04 onion-test.json) — DONE

All tasks are done. For the as-built files, see §5 "M1/M2 as built".

| Task | Files | Acceptance / vectors |
|---|---|---|
| **M2-T1** Key generator | `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxKeyGenerator.cs` | Known answers from `onion-error-test.json` (same session key and hop keys as `onion-test.json`): every hop's `hop_shared_secret` via the epk chain (completes M1-T4 for hops 1-4), every hop's `ammag_key`, and hops[4] `um_key`. rho/mu have no published per-hop values; assert them via end-to-end M2-T2/T3 |
| **M2-T2** Construct | `OnionBuilder.cs`, `SphinxService.Construct` (raw payload bytes, see `OnionHop` in §4.1; do not add a Bitcoin→Serialization reference) | `Construct(hops from onion-test.json, session 0x41×32, assoc 0x42×32)` produces **exactly** the expected 1366-byte onion (hex compare). The JSON `payload` values **already include the bigsize length** ("All the payloads already have the variable length encoded"), so strip it before passing (or test the builder's framing against it). They contain unknown odd TLVs that must be kept verbatim: hop 1 type 513 (60 B), hop 4 type 301 (224 B). Must cover the 275-byte payload (bigsize 3-byte len) and variable filler. Separately, `HopPayloadSerializer` must round-trip these payloads, keeping the unknown odd TLVs |
| **M2-T3** Peel | `OnionPeeler.cs`, `SphinxService.Peel` | Using each hop's privkey from the JSON: payload bytes match; next packet equals what the next hop receives; the last hop reports `IsFinal` (next_hmac all zero). A flipped bit anywhere in hop_payloads → `OnionException(InvalidOnionHmac)`. Version 1 → `InvalidOnionVersion`. Invalid pubkey → `InvalidOnionKey`. Bad bigsize/len < 2/short → `InvalidOnionPayload` |
| **M2-T4** Hop payload semantic validation | `src/NLightning.Domain/Protocol/Onion/Validators/HopPayloadValidator.cs` (pure) | Rules from §1.4: non-final without scid → `InvalidOnionPayload(type=6)`; final with scid → accept ("MUST NOT include" is writer-only; the reader has no such rule); final non-blinded without `total_msat` → reject; path_key/current_path_key both present or both missing when blinded → reject; blinded non-final with any TLV other than 10/12 (unknown odd types included) → reject (M5 extends it with the payment_relay computation); unknown even type → `InvalidOnionPayload(type, offset)`. `current_path_key` (TLV 12) and the update_add_htlc `blinded_path` key are only length/prefix-checked by their converters (not curve-validated): the peeler/route-blinding code MUST validate them via `ISecp256K1Math` (which runs `ECPubKey.TryCreate`) and map failure to `invalid_onion_blinding`, with a test using `0x02 || ff×32` |
| **M2-T5** Replay cache | `IOnionReplayCache` + in-memory impl | Same HMAC twice → second `TryAdd` false. Callers record only HMAC-verified onions (§1.6 step 3). FIFO eviction is a stopgap: M4 must key or expire entries by `cltv_expiry` (and persist them) |

Test location: new folder `test/NLightning.Integration.Tests/BOLT4/` with `Vectors/onion-test.json`. Add `<Content Include="BOLT4/Vectors/*.json" CopyToOutputDirectory="PreserveNewest"/>` to `test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj`, which already does the same for `BOLT11/Vectors/ValidInvoices.txt` (L55). Parse the JSON with `System.Text.Json`. Unit-level tests go in `test/NLightning.Infrastructure.Bitcoin.Tests/Onion/`.

### M1/M2 as built (record)

Verification on `wip/fafo` HEAD (finalizer run):
- Builds: `-c Release` and `-c Release.Native` both have 0 errors and 592 warnings, of which 20 are `warning CS`. That matches the pre-M1 baseline (about 590 / about 20), and none of them are in onion code.
- `dotnet test --no-build --filter 'FullyQualifiedName!~Docker'` gives the same result under both configurations: 1451 tests, 1450 pass. The only failure is the known DNS-dependent `PeerAddressTests.Given_HttpAddress_...`.
- Application.Tests (24) and Daemon.Tests (23) pass via `dotnet run`.
- `dotnet format --verify-no-changes` is clean.

| Task | Production files | Tests |
|---|---|---|
| M1-T1 | `src/NLightning.Infrastructure/Crypto/Functions/HmacSha256.cs`, `Functions/Hkdf.cs` | `test/NLightning.Infrastructure.Tests/Crypto/Functions/HmacSha256Tests.cs` |
| M1-T2 | `src/NLightning.Infrastructure/Crypto/Interfaces/ICryptoProvider.cs` + 3 providers, `Crypto/Ciphers/ChaCha20Stream.cs` | `test/NLightning.Infrastructure.Tests/Crypto/Ciphers/ChaCha20StreamTests.cs`, `.../Providers/{Libsodium/SodiumCryptoProviderTests,Native/NativeCryptoProviderTests}.cs` |
| M1-T3 | `src/NLightning.Domain/Crypto/Interfaces/ISecp256K1Math.cs`, `src/NLightning.Infrastructure.Bitcoin/Crypto/Functions/Secp256K1Math.cs`, `Services/KeyDerivationService.cs`, `DependencyInjection.cs` | `test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/Functions/Secp256K1MathTests.cs`, `.../Services/KeyDerivationServiceTests.cs`, BOLT 3 regression in `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs` |
| M1-T4 | — | `test/NLightning.Infrastructure.Bitcoin.Tests/Crypto/Functions/EcdhTests.cs` (hop 0) |
| M1-T5 | `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs` | `.../ValueObjects/BigSizeTypeSerializerTests.cs`, `Vectors/BigSize.txt` |
| M1-T6 | `src/NLightning.Domain/Protocol/Tlv/TruncatedInt.cs` (moved from Infrastructure by NL-084) | `test/NLightning.Domain.Tests/Protocol/Tlv/TruncatedIntTests.cs` |
| M1-T7 | `src/NLightning.Infrastructure.Serialization/Tlv/{TlvStreamSerializer,TlvSerializer}.cs`, `Interfaces/ITlvStreamSerializer.cs`, `ITlvConverterFactory.GetConverter(Type)` | `test/NLightning.Infrastructure.Serialization.Tests/Tlv/{TlvStreamBolt1VectorTests,TlvStreamSerializerTests}.cs` |
| M1-T8 | `src/NLightning.Domain/Protocol/Onion/{Constants,Enums,Extensions,Tlv,ValueObjects}/*`, `src/NLightning.Domain/Exceptions/OnionException.cs`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/Onion/*` (registered in `TlvConverterFactory`) | `test/NLightning.Domain.Tests/Protocol/Onion/{OnionPacketTests,OnionTlvTests,FailureCodeTests}.cs`, `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/Onion/*` |
| M1-T9 | `UpdateAddHtlcPayload.cs`, `UpdateAddHtlcPayloadSerializer.cs`, `UpdateAddHtlcMessageSerializer.cs`, `IMessageFactory`/`MessageFactory` | `test/NLightning.Infrastructure.Serialization.Tests/Messages/UpdateAddHtlcMessageTests.cs`, `BlindedPathTlvConverterTests.cs` |
| Vectors | `test/NLightning.Integration.Tests/BOLT4/Vectors/*.json` (all five), `test/NLightning.Tests.Utils/Vectors/Bolt4Vectors.cs` | `test/NLightning.Integration.Tests/BOLT4/Bolt4VectorLoadingTests.cs` |
| M2-T1 | `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxKeyGenerator.cs` | `test/NLightning.Infrastructure.Bitcoin.Tests/Onion/SphinxKeyGeneratorTests.cs`, `test/NLightning.Integration.Tests/BOLT4/OnionVectorTests.cs` |
| M2-T2/T3 | `src/NLightning.Domain/Protocol/Onion/Interfaces/ISphinxService.cs`, `Models/{OnionHop,ConstructedOnion,PeeledOnion}.cs`; `src/NLightning.Infrastructure.Bitcoin/Onion/{SphinxService,OnionBuilder,OnionPeeler,SphinxBigSize}.cs` (singleton in `AddBitcoinInfrastructure`) | `test/NLightning.Infrastructure.Bitcoin.Tests/Onion/{SphinxServiceTests,SphinxBigSizeTests,SphinxServiceRegistrationTests}.cs`, `test/NLightning.Integration.Tests/BOLT4/{OnionVectorTests,OnionServiceRegistrationTests}.cs` |
| M2 payloads | `src/NLightning.Domain/Protocol/Onion/Models/HopPayload.cs`, `src/NLightning.Infrastructure.Serialization/{Interfaces/IHopPayloadSerializer,Onion/HopPayloadSerializer}.cs` (singleton), `src/NLightning.Domain/Protocol/Onion/Factories/InvalidOnionPayloadFailureFactory.cs` | `test/NLightning.Domain.Tests/Protocol/Onion/{HopPayloadTests,InvalidOnionPayloadFailureFactoryTests}.cs`, `test/NLightning.Infrastructure.Serialization.Tests/Onion/HopPayloadSerializerTests.cs`, `test/NLightning.Integration.Tests/BOLT4/HopPayloadVectorTests.cs` |
| M2-T4 | `src/NLightning.Domain/Protocol/Onion/Validators/HopPayloadValidator.cs` | `test/NLightning.Domain.Tests/Protocol/Onion/HopPayloadValidatorTests.cs` |
| M2-T5 | `src/NLightning.Domain/Protocol/Onion/Interfaces/IOnionReplayCache.cs`, `src/NLightning.Infrastructure/Protocol/Onion/OnionReplayCache.cs` (singleton in `AddInfrastructureServices`) | `test/NLightning.Infrastructure.Tests/Protocol/Onion/OnionReplayCacheTests.cs` |

**Deviations from §4.** They were intentional, and the spec is followed in each case.
- **`ISphinxService`:**
  - `Peel` takes an explicit `PrivKey nodeKey`. A separate `PeelAsLocalNode` reads the key from `ISecureKeyManager` and zeroes the copy afterwards. The two names are distinct because a `byte[]` converts implicitly to both `PrivKey` and `CompactPubKey`, so overloads would bind a raw key to the wrong parameter.
  - An `OnionPacketKind` parameter sets the minimum payload length: 2 for payments, 0 for onion messages.
  - `ConstructWithSharedSecrets` returns `ConstructedOnion` (packet + per-hop secrets). `ComputeSharedSecrets` rebuilds the secrets from the session key.
- **`PeeledOnion`:**
  - `Payload` holds the raw TLV bytes (no length prefix), not a parsed `HopPayload`. Infrastructure.Bitcoin cannot reference Serialization, so callers parse the bytes with `IHopPayloadSerializer.DeserializeAsync`.
  - `IsFinal` is computed as `NextPacket is null`.
  - `PathKeySharedSecret` was added for M5.
- **Peel failures:**
  - With a path_key on a payment, every failure is remapped to `invalid_onion_blinding` (BOLT 4 "Returning Errors").
  - The update_add path_key is curve-validated by the peeler.
  - Framing failures are `invalid_onion_payload` (type 0, offset 0) with `OnionException.SharedSecret` set.
  - `OnionException` exposes the failure bytes as `FailureData` (the plan said `Data`).
- **`HopPayloadSerializer`:**
  - It reads records itself instead of calling `DeserializeStrictAsync`, so that every failure carries the offending type and offset.
  - Offsets count the stripped bigsize length prefix, because BOLT 4 measures offsets in the decrypted stream.
  - The interface lives in `Infrastructure.Serialization/Interfaces`, not Domain.
- **Bigsize in Infrastructure.Bitcoin:** `SphinxBigSize` is a small internal bigsize reader/writer for hop framing, since that project cannot reference Serialization.
- **`OnionPacket`:**
  - It is a `readonly struct` over one byte array, and its constructors take any hop_payloads length.
  - There is no `OnionPacketTypeSerializer`: update_add_htlc keeps raw bytes and the fixed length is enforced at the payload.
  - `FailureMessage`, `DecryptedFailure` and `IFailureOnionService` came with M3 (see "M3 as built"); `FailureTlvTypes` was not needed.
- **`HopPayloadValidator`:** it is a static class, `Validate`/`TryValidate(payload, isFinalHop, hasUpdateAddPathKey)`.
  - Blinded hops reject every TLV outside the allowed set, including unknown odd TLVs.
  - A non-blinded final hop that carries `short_channel_id` is accepted, because that is a writer-only rule.
  - It always reports `invalid_onion_payload`. The caller maps failures to `invalid_onion_blinding` inside blinded routes.
- **Replay cache:** it is a bounded FIFO with 100k entries by default. `TryAdd` checks and records in one call, and callers make it only after a successful peel (HMAC verified). The spec lists the replay check before HMAC verification; §1.6 step 3 gives the reason for the change.
- **Plan corrections versus the spec**, already folded into §1.6 and §4.1:
  - `path_key` for the peel tweak is only the key received alongside the onion, never the payload's `current_path_key`. An earlier draft said otherwise.
  - Onion-message payload length 0 is valid.

**Open follow-ups going into M3/M4** (not done in M1/M2):
- **Feature defaults:** done. `OptionRouteBlinding` and `OptionAttributionData` default to No and are experimental-gated (NL-074, NL-206); take each out of `FeatureOptions.ExperimentalFeatures` when M5/M3b lands.
- **M3:** done (see "M3 as built").
- **`current_path_key` (TLV 12):** it is still only length/prefix-checked. M5 must curve-validate it and map failures to `invalid_onion_blinding`.
- **M4 prerequisites:**
  - The replay cache must become cltv-keyed and persistent.
  - The per-HTLC shared secret must be persisted.
  - Done: the `ShortChannelId(ulong)` masks (NL-101) and the SCID tx index (NL-102) are fixed; scid aliases are persisted (NL-103, NL-209). The real SCID is persisted (NL-225, 2d1fca6).
  - Done: Application.Tests runs under `dotnet test` (NL-167).
  - Done: `IHopPayloadSerializer` lives in Domain and the serialization DI is self-sufficient (NL-075, NL-076).
- **Unused vectors:** only `route-blinding-test.json` (M5), apart from the loader test. `blinded-onion-message-onion-test.json` is used by `OnionVectorTests.Given_BlindedOnionMessageVector_When_PeelingChain_Then_EachNextPacketMatches` (NL-183).
- **CI checks:** the JS/WASM ChaCha20 path is verified only in CI `Release.Wasm`. The Docker DI (`AbcNetworkTests`, `ChannelOpeningFlowTests`) calls `AddInfrastructureServices`, `AddSerializationInfrastructureServices` and `AddBitcoinInfrastructure`, so it already gets the new singletons. Mirror any M4 hand registrations there.

### M3: Failure messages (legacy error onion) — DONE

All of M3-T1..T3 are done; M3b is not started. For the as-built files and deviations, see "M3 as built" below.

| Task | Files | Acceptance / vectors |
|---|---|---|
| **M3-T1** FailureMessage model + serializer | `FailureMessage.cs`, `FailureMessageSerializer.cs` | Round-trip every code in §1.7, including the optional TLV extension. Parsing ignores trailing bytes |
| **M3-T2** Create / wrap / decrypt | `FailureOnionService.cs` (the origin gets the per-hop secrets from `ISphinxService.ConstructWithSharedSecrets`; `ComputeSharedSecrets` is only for rebuilding them after a restart) | `onion-error-test.json` has **no** intermediate streams or packets: top-level `generate`/`errorpacket`, and per hop only `version`, `pubkey`, `hop_shared_secret`, `ammag_key` (`um_key` and the plaintext `payload` only on hops[4]). From it assert: each hop's shared secret and `ammag_key` (and hops[4] `um_key`); the erring node's plaintext framing equals `hops[4].payload` (`0002 2002 00fe 00..`, without HMAC); the final origin-received 292-byte `errorpacket` matches byte-for-byte; origin decrypt yields hop 4 / 0x2002. Take intermediate checks (per-hop `stream` and `error packet for node N`) from the inline BOLT 4 "Test Vector > Returning Errors" trace (`04-onion-routing.md` ~L1892-1963). That trace uses a different failure (0x400F incorrect_or_unknown_payment_details + TLV 34001, `failure_len` 0x0140, not padded to 256); its legacy (non-attribution) error-packet lines can be used in M3 without doing M3b. The origin decrypt runs a constant 27 iterations |
| **M3-T3** Malformed conversion | `FailureOnionService` helper `CreateFromMalformed(Secret ss, FailureCode code, ReadOnlySpan<byte> sha256OfOnion)` | Non-BADONION code → throws/validation failure (BOLT 2 MUST reject); valid code → erring-node packet with data = sha256_of_onion |
| **M3b (optional)** attribution_data | `UpdateFailHtlcMessage` TLV 1 (`AttributionDataTlv`, 920 bytes) + converter; extend `FailureOnionService` | Inline BOLT 4 "Returning Errors" trace (incorrect_or_unknown_payment_details, htlc_msat=100, height=800000, TLV 34001). Gate on `Feature.OptionAttributionData`. Until done, **set its default to No** in `FeatureOptions` |

### M3 as built (record)

Commits on `wip/fafo` (lane `l4-onion-m3`, cherry-picked with `-x`): ded60a1 (M3-T1 model + serializer), ce3cfeb (M3-T2 create/wrap/decrypt), 9b2e294 (M3-T3 malformed conversion + channel_update embedding), 37df603 (origin interpretation), a657719 (vector transcription fix), 3c1d68a (all-zero sha256_of_onion), a42c33b (legacy codes). Ledger: NL-070 and NL-071 fixed; NL-072 (M3b) and NL-022 stay open.

- **Domain** (`src/NLightning.Domain/Protocol/Onion/`): `Models/FailureMessage` (code, exact code-specific `Data` validated against the BOLT 4 layout of every defined code, raw `Extension` TLVs; one static factory per code; typed accessors; `FromMalformed`; `WithChannelUpdate`), `Models/DecryptedFailure`, `Models/FailureInterpretation` + `Interpreters/FailureInterpreter`, `Validators/MalformedHtlcValidator` + `Enums/MalformedHtlcCheckResult`, `Factories/FailureChannelUpdateFactory`, `Interfaces/IFailureOnionService`; `Serialization/Interfaces/IFailureMessageSerializer`; `Protocol/Tlv/BigSizeCodec` (internal span-based canonical bigsize shared with `InvalidOnionPayloadFailureFactory`).
- **Infrastructure.Bitcoin:** `Onion/FailureOnionService` (singleton in `AddBitcoinInfrastructure`; resolves `IFailureMessageSerializer`, which `AddSerializationInfrastructureServices` registers, so both layers must be added, as the daemon and Docker DI do).
- **Infrastructure.Serialization:** `Onion/FailureMessageSerializer` (failuremsg <-> `FailureMessage`, trailing non-TLV bytes ignored; body framing `u16 failure_len || failuremsg || u16 pad_len || pad`, min 256, max packet 32768).
- **Tests:** `test/NLightning.Integration.Tests/BOLT4/FailureOnionVectorTests.cs` (onion-error-test.json and the inline trace, every hop, both directions), `BOLT4/MalformedFailureConversionTests.cs`, `BOLT4/Vectors/returning-errors-trace.json` (transcribed from the inline BOLT 4 trace), unit tests in `test/NLightning.Domain.Tests/Protocol/Onion/`, `test/NLightning.Infrastructure.Bitcoin.Tests/Onion/FailureOnionServiceTests.cs`, `test/NLightning.Infrastructure.Serialization.Tests/Onion/FailureMessageSerializerTests.cs`.

Deviations from the M3 plan:
- No `FailureTlvTypes`: BOLT 4 defines no failure TLV types, so `FailureMessage.Extension` keeps raw `BaseTlv` records.
- The serializer interface is synchronous and lives in Domain (`Serialization/Interfaces`), not in Serialization.
- The malformed helper is `IFailureOnionService.CreateErrorPacketFromMalformed(incomingSharedSecret, code, sha256OfOnion)` (plan: `CreateFromMalformed`); it goes through `FailureMessage.FromMalformed`, which throws unless the BADONION bit is set. `MalformedHtlcValidator` treats an all-zero `sha256_of_onion` as valid (e.g. `invalid_onion_blinding` from a blinded path), which the plan did not mention.
- Origin decrypt runs **max(27, hops)** iterations (plan: a constant 27) with an all-zero dummy secret past the route end, a constant-time HMAC compare, and never attributes a dummy iteration; it returns `null` when no hop matches.
- Added beyond the plan: `FailureInterpreter` (origin-side interpretation: attribution, final node, permanent/node failure, retry, which hop/channel to exclude; a readable-code-less intermediate failure blames the erring node, an unattributable one blames nobody), `FailureChannelUpdateFactory` (writes `u16 258 || payload` as LND/CLN/Eclair/LDK do; BOLT 4 is silent on the prefix; reads with or without it), and recognition of the legacy codes 17 `final_expiry_too_soon` and PERM|16 `incorrect_payment_amount` on receipt (never sent).

Remaining:
- **M3b** attribution_data + `fulfillment_payload` (NL-072, NL-022); `ammagext` label still unconfirmed. Keep `OptionAttributionData` experimental-gated until then.
- **Wiring** (BOLT 2 plan): N6-T2 fails back with `CreateErrorPacket` (done, a02afa7: final hop `incorrect_or_unknown_payment_details`, else `temporary_node_failure`); N8-T2 final hop; N8-T3 origin decrypt + `FailureInterpreter`; M4-T5 upstream wrap/convert. `channel_update` in UPDATE failures stays empty (len 0) until the typed, signed update (e7b5269) is wired in (ABCD W3-C).

### M4: Integration with the HTLC flow — DONE (ABCD wave 2)

| Task | Files | Acceptance |
|---|---|---|
| **M4-T1** Receive update_add_htlc. **Done** by BOLT2 N6-T1 (a604dff) | `UpdateAddHtlcMessageHandler.cs`, `ChannelManager` switch case | First step: add `xunit.runner.visualstudio` 3.1.5 to `test/NLightning.Application.Tests/NLightning.Application.Tests.csproj` (matching the other test csprojs) so the tests are discovered (§0.3), and put M4 Application unit tests there. Unit tests with mocks: BOLT 2 limits enforced; HTLC persisted; the peer is **not** disconnected |
| **M4-T2** Peel after lock-in. **Processor done** (`IncomingOnionProcessor`, 6156173); `LocalOnlyHtlcSwitch` peels and fails back (a02afa7); the real switch is W2-B. **Done** (`HtlcSwitch`, ca87313) | `HtlcSwitch` invoked from the revoke_and_ack handler | Peel with `ISphinxService.PeelAsLocalNode(packet, payment_hash, updateAddPathKey)` (never the payload's current_path_key). Malformed onion (BADONION code) → `update_fail_malformed_htlc` with SHA256(onion). Framing `invalid_onion_payload` → `update_fail_htlc` encrypted with `OnionException.SharedSecret`. With a path_key the peeler already reports `invalid_onion_blinding`; the switch MUST also map its own later failures (validator, forwarding) to `invalid_onion_blinding` when update_add_htlc carried a path_key. Record the HMAC in the replay cache only after the peel succeeds. Parse the payload with `IHopPayloadSerializer.DeserializeAsync(peeled.Payload)`: its offsets already count the stripped length prefix |
| **M4-T3** Final hop. **Processor done** (`FinalHopProcessor`, 6156173, 234607e; no MPP; 0x0013/0x0012 are reported before 0x400F, as LND and CLN do); accept must be atomic in the switch (NL-253). **Done** (ca87313, d1476a4: accept and settle staged in the fulfill's save) | `FinalHopProcessor` + invoice store (uses `src/NLightning.Bolt11`; requires a ProjectReference that does not exist today) | Unknown hash, bad secret or low amount → 0x400F with (htlc_msat, height); mismatched cltv/amount → 0x0012/0x0013; MPP timeout 60 s → 0x0017 |
| **M4-T4** Forward (Domain contracts `IForwardingPolicy`/`ForwardingFee`/`RoutingOptions` done, 2ede2ee; **policy done**, `HtlcForwardingPolicy` 6156173; **switch done**, ca87313; signed `channel_update` in UPDATE failures when the scid matches, NL-266) | `HtlcSwitch` + `HtlcForwardingPolicy`; outgoing `update_add_htlc` on the channel resolved by `ShortChannelId`/aliases via `IChannelMemoryRepository` | Fee/CLTV failures map to the correct codes; `channel_update` len = 0 until BOLT 7 exists; forward link persisted |
| **M4-T5** Upstream failure/fulfill propagation. **Done** (ca87313, d1476a4) | fail/fulfill handlers | Downstream fail → wrap with the stored shared secret → upstream `update_fail_htlc`; malformed → convert (M3-T3); fulfill → propagate the preimage |
| **M4-T6** Send (origin). **Route and onion build done** (`HintRouteBuilder` with a mandatory fee limit, `PaymentOnionFactory`, 6156173, 234607e); IPC/CLI done (W1-D 6cfbcd1, c10a78e); **`PaymentService` done** (6cb279f, 083a726, f2f1ef6; proven in Docker N8 and ABCD c-send) | `PaymentManager`, new IPC command (`ClientCommand` in `src/NLightning.Domain/Client/Enums/ClientCommand.cs`, DTOs in `src/NLightning.Transport.Ipc`, handler in `src/NLightning.Daemon/Ipc/Handlers`) | Direct-channel and route-hint payment to LND in the Docker regtest fixture (`test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs`: alice↔bob, carol→alice, carol→bob). Keep these tests in the `Docker` namespace so CI skips them |
| **M4-T7** Persistence (**tables done**: `HtlcEntity.OnionSharedSecret` 4472a8b; `AddInvoicesPaymentsAndCircuits` with circuits, invoices, payments + hop shared secrets and HTLC origins, 899e36b; origin saved with the add 4ca9b56 (NL-250); circuit replay **done** ca87313, d1476a4; cltv-expiring replay store `IOnionReplayStore` eb597d7, not wired or persisted: NL-078 remains) | §4.5 entities + 3 migrations | Restart mid-forward: fail/fulfill can still be propagated |

### M5: Route blinding
- **Files:** `src/NLightning.Infrastructure.Bitcoin/Onion/RouteBlinding/{BlindedPathBuilder,BlindedHopProcessor}.cs`, the encrypted_data TLVs (§4.1), and the fee formula helper `amt_to_forward = ((amount_msat - fee_base) * 1e6 + 1e6 + fee_prop - 1) / (1e6 + fee_prop)`.
- **Mechanics:**
  - `encrypted_recipient_data = ChaCha20Poly1305(rho_i, nonce 0, no AD)` using the existing `ChaCha20Poly1305.Encrypt(key, 0, …)`.
  - `B_i = HMAC("blinded_node_id", ss_i) * N_i`.
  - The next path_key is `next_path_key_override` or `E_{i+1} = SHA256(E_i ‖ ss_i) * E_i`.
- **Vectors:** `route-blinding-test.json` (every per-hop field) and `blinded-payment-onion-test.json` (full route plus per-hop decrypt).
- **Integration:**
  - Read `BlindedPathTlv` from `update_add_htlc`.
  - `MessageFactory.CreateUpdateAddHtlcMessage` needs a `BlindedPathTlv?` parameter; it has none today.
  - The error rules: `invalid_onion_blinding`; the introduction point converts via `update_fail_htlc`; nodes inside the path use malformed.
  - `PeeledOnion.PathKeySharedSecret` is `ECDH(path_key, node_key)`: use it for `rho` (decrypting `encrypted_recipient_data`) and the next path_key, instead of redoing the ECDH. At the introduction point (current_path_key in the payload, no update_add_htlc path_key) compute that ECDH from current_path_key separately.
- **Until M5 ships:** keep `OptionRouteBlinding` at No and in `FeatureOptions.ExperimentalFeatures` (done: NL-074, NL-206).

### M6 (optional): Onion messages
- **Wire message:**
  - `MessageTypes.OnionMessage = 513` in `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`;
  - `OnionMessagePayload`/`OnionMessageMessage` deriving from **BaseMessage**, because this is not a channel message;
  - serializers registered in **both** dictionaries of `MessageTypeSerializerFactory` and `PayloadSerializerFactory`.
- **Dispatch:** add a branch in `src/NLightning.Infrastructure/Node/Services/PeerService.cs` `HandleMessage` (~L100) and a new event on `IPeerService`. `HandleMessage` handles init (before initialization), `IChannelMessage`, `ErrorMessage` (~L116) and `WarningMessage` (~L142); any other type (e.g. onion_message) is silently dropped. Add an `else if (message is OnionMessageMessage ...)` branch to that chain. The send path needs to bypass `IPeerService.SendMessageAsync`, which only accepts `IChannelMessage`.
- **Onion format:** variable packet size (1300/32768; the Sphinx core is already length-parameterized per §4.1, and `OnionMessagePayload` validates its own length), empty associated data, onionmsg_tlv (2, 4, 64, 66, 68), no error replies.
- **Vectors:** `blinded-onion-message-onion-test.json`.

---

## 6. Porting legacy code

> The user has older onion code **outside this repository**. It is not in git history (no onion or sphinx match in any branch). Treat it as a reference implementation to mine, not as code to paste in.

### 6.0 Known legacy source: LNBolt (nbd-wtf/LNUnit)

The legacy code is **LNBolt** (https://github.com/nbd-wtf/LNUnit/tree/master/LNBolt, MIT). The full review is in [LNBOLT_REVIEW.md](LNBOLT_REVIEW.md). **Verdict: re-implement from the spec, and use LNBolt only as a readable outline. Do not copy it verbatim.**

- **Correct in LNBolt:** the sender shared-secret chain, ECDH (33-byte keys), the blinding factor `SHA256(E||ss)`, the rho/mu/um keys, the ChaCha20 stream (zero nonce), `GenerateFiller`, and the single-hop peel (mu HMAC over `hop_payloads||associated_data`). Against `onion-test.json`, all 5 shared secrets match and hop 0 decodes.
- **Why it worked for the HTLC interceptor:** the virtual node was always the final hop, and only `payment_data`/keysend was read. The forwarding path was never exercised.
- **Broken in LNBolt. These are the traps to avoid in our implementation:**
  - The next ephemeral key uses scalar `ss*b` where it should use a point `b*E`.
  - The next-HMAC offset comes from lossy re-serialization. It should come from the parsed BigSize prefix.
  - Final-hop detection fails when the payload carries extra TLVs.
  - Construction has no length prefix and no `pad`-key initial stream.
  - `Peel` mutates its input.
  - Signed `BigInteger` encoding gives 31- or 33-byte scalars.
  - The HMAC compare isn't constant-time.
  - Version, pubkey and length are never validated.
  - Error onions (`um`/`ammag`, failure HMAC, attribution) are missing entirely.
  - Legacy realm-0 payloads are still accepted.
- **Oracle:** `reference/sphinx-harness/Program.cs` is a spec-direct construction that reproduces `onion-test.json` byte-for-byte. Use it to cross-check intermediate values (shared secrets, ephemeral keys, filler) while debugging M2. It is not production code.
- **Dependencies:** do not bring in LNBolt's `ServiceStack.Text` (AGPL/commercial dual license) or BouncyCastle. Map everything onto the primitives listed in item 4 below.

**Evaluation checklist.** Run this before porting anything.
1. **Provenance and license:** confirm the author and license are compatible with this repo's LICENSE.
2. **Spec version:** check that it uses TLV hop payloads (variable-length), not the legacy fixed 65-byte `hop_data` realm-0 format. If it only supports the legacy format, reuse only the key-derivation, stream and filler ideas.
3. **Vector check before port:** make the legacy code run against `onion-test.json` and `onion-error-test.json` in a throwaway harness outside the solution (see `reference/sphinx-harness/`). **If it doesn't reproduce the vectors byte-for-byte, do not port it.** Port only the parts that are proven correct.
4. **Primitive mapping:** replace its crypto with this repo's primitives. Its HMAC becomes `HmacSha256` (M1-T1), its ChaCha becomes `ChaCha20Stream` (M1-T2), its ECDH becomes `IEcdh.SecP256K1Dh`, and its EC tweak becomes `ISecp256K1Math`. Legacy code MUST NOT bring in a new crypto dependency: no new NuGet packages in Domain, and nothing that breaks the three `CRYPTO_*` builds.
5. **Layering:** pure models go to Domain (§4.1, BCL only), crypto and algorithms to `Infrastructure.Bitcoin/Onion`, byte framing to `Infrastructure.Serialization`, and orchestration to Application. Split any monolithic legacy classes along these lines.
6. **Style:** file-scoped namespaces with relative usings after the namespace line, `_camelCase` fields, no `this.`, `var`, and no unused usings or parameters. `dotnet format --verify-no-changes` must pass. Rename types to match §4 so later milestones line up.
7. **Safety review:**
   - constant-time HMAC compare (`FixedTimeEquals`);
   - every length checked before slicing;
   - no `Debug.Assert`-only validation, because Release strips it;
   - fixed nonces used only with single-use keys;
   - session keys from a CSPRNG (`IEcdh.GenerateKeyPair()` or `RandomNumberGenerator`, **not** `SodiumJsCryptoProvider.RandomBytes`, which swallows exceptions);
   - key material zeroed where practical.
8. **Tests first:** land the vector tests (M2/M3) before or together with the ported code. Port tests from the legacy code only if they are spec vectors or clearly correct property tests.
9. **Commit shape:** one milestone task per commit/PR, with lowercase imperative subjects in the repo's style (e.g. `add sphinx onion builder with bolt04 vectors`).

---

## 7. Dependencies on missing HTLC / commitment / graph work

M1–M3 are done; M3b and M5 (crypto and vectors) can proceed now. M4 is blocked on the following (status after ABCD wave 2: all done; M4 is complete):

| Prerequisite | Evidence | Needed for |
|---|---|---|
| Handlers for update_add/fulfill/fail/fail_malformed, commitment_signed, revoke_and_ack, update_fee, channel_reestablish | `src/NLightning.Application/Channels/Managers/ChannelManager.cs` switch has only the open flow; `default` sends a channel-scoped warning. Engine exists (BOLT2 N4). **Done**: handlers BOLT2 N6 (a604dff); reestablish BOLT2 N7 (4ec83d3) | any HTLC |
| ChannelModel HTLC mutators (add/settle/fail, next id, balances, revocation numbers) | Partial: the pure BOLT 2 engine `src/NLightning.Domain/Channels/Commitments/ChannelCommitments` holds HTLCs, msat balances, next ids and commitment numbers (BOLT2 N4); `ChannelModel` holds the persisted engine snapshot (`Commitments`, `UpdateCommitments`, BOLT2 N5, 4472a8b); the handlers that drive it are **done** (BOLT2 N6, a604dff) | M4-T1..T5 |
| Richer `HtlcState` (commitment-dance stages, forwarded) | **Done** (BOLT2 N4-T1): core-lightning `htlc_state` values 10-19/30-39 in `HtlcState`, `HtlcStateTable.IsAddIrrevocablyCommitted`; lock-in and irrevocable-removal events **done** (N4-T4, b166ea0) | peel-after-lock-in |
| HTLC signatures (htlc_signatures in commitment_signed; SIGHASH_SINGLE\|ANYONECANPAY for anchors) | **Done** (BOLT2 N2-T4, N3-T1: NL-056, NL-057, NL-058): `HtlcTransactionBuilder`, signer HTLC APIs, `CommitmentSigningService`; Appendix C/F vectors byte-exact | commitment_signed |
| HTLC reload bug | **Fixed** (NL-125, NL-126, NL-127, NL-128: fd41d73, 1e8c803); HTLC rows are written only by `ChannelStateDbRepository` since N5 (4472a8b; `ChannelDbRepository.UpdateAsync` no longer touches them) | HTLC persistence |
| ShortChannelId(ulong) masks | **Fixed** (NL-101, 83d529d) | hop payload scid (u64) → channel lookup |
| SCID tx index | **Fixed** (NL-102, 7df8f2d): block position, 24-bit, migration `WidenWatchedTransactionIndex` | correct scid |
| Per-channel ordering / concurrency | **Done** (NL-033, NL-193): `IChannelLockProvider` per channel, one ordered inbound loop and one `PeerOutbox` per peer. Cross-channel forwarding must never hold two channel locks (enqueue the outgoing add under the outgoing channel's lock only) | cross-peer forwarding |
| Transport partial reads | **Fixed** (NL-104 d0e0bbb, NL-105 d865a7e): `ReadExactlyAsync`, lock held across encrypt + write | 1366-byte onions |
| BOLT 7 (channel_update, graph) | 256, 257, 259 parse as raw `GossipMessage` and are dropped (NL-100); 258 is a typed `ChannelUpdateMessage`, signed/verified with the node key (e7b5269, 3ba2e4f), sent to and received from the channel peer since W1-E (b93dd05); queries get empty replies (NL-205); no graph (NL-099) | channel_update in failures (optional; len 0 allowed), pathfinding beyond direct channels / route hints |
| BOLT 11 in the node | `src/NLightning.Bolt11` is referenced by Application (W1-B, 6156173); the library fixes (default `c` = 18, every `r` field kept: NL-115, NL-116) and encode validation with LND 0.20 fixtures (NL-120, 2d8a9fe, f93d059) are done | final hop and send |
| Invoice / preimage / payment stores | **Done** (W1-C, 899e36b; repositories resolvable from the scope, 4ae2eb3) | M4-T3, M4-T6 |

---

## 8. Test-vector wiring summary

| Vector | Test project/folder | csproj entry |
|---|---|---|
| RFC 4231, RFC 8439 | `test/NLightning.Infrastructure.Tests/Crypto/…` (inline constants) | – |
| BOLT 1 BigSize/TLV | `test/NLightning.Infrastructure.Serialization.Tests/Vectors/` | already globbed (`Vectors\*.*`) |
| onion-test.json, onion-error-test.json, route-blinding-test.json, blinded-payment-onion-test.json, blinded-onion-message-onion-test.json | `test/NLightning.Integration.Tests/BOLT4/Vectors/` | add `<Content Include="BOLT4/Vectors/*.json" CopyToOutputDirectory="PreserveNewest"/>` |
| Shared C# constants (session key, assoc data) | `test/NLightning.Tests.Utils/Vectors/Bolt4Vectors.cs` | – |

Namespaces must not contain `Docker` unless the test really needs Docker, because the CI filter is a substring match.

---

## 9. Risks

1. **Three-provider drift:** a new `ICryptoProvider` method must compile and behave identically under `CRYPTO_LIBSODIUM`, `CRYPTO_NATIVE` and `CRYPTO_JS`. WASM can only be built on linux-x64 CI. Mitigation: shared known-answer tests in both `Release` and `Release.Native`, and CI for Wasm.
2. **ChaCha20 counter mismatch:** AEAD wrappers start at counter 1 and BOLT 4 needs counter 0. The M2 vectors will catch this.
3. **Filler bugs with variable payloads:** copying the spec's legacy fixed-hop Go sample gives wrong output. The 275-byte payload in `onion-test.json` covers this case.
4. **Non-canonical encodings accepted:** until M1-T5, M1-T6 and M1-T7 land, peeling could accept malleable payloads.
5. **Timing leaks:** use `FixedTimeEquals` for HMACs, and keep the origin error decrypt at a constant 27 iterations.
6. **Performance:** each `new Sha256()` allocates provider memory through `ICryptoProvider.MemoryAlloc` (`src/NLightning.Infrastructure/Crypto/Hashes/Sha256.cs:21`): `sodium_malloc` (guarded, mlocked pages) under `CRYPTO_LIBSODIUM` (`Providers/Libsodium/SodiumCryptoProvider.cs:102-105`), `Marshal.AllocHGlobal` under `CRYPTO_NATIVE` (`Providers/Native/NativeCryptoProvider.cs:80-83`). Reuse instances per packet and avoid per-hop allocations in hot paths.
7. **Feature advertisement:** resolved. RouteBlinding and AttributionData default to No and cannot be enabled without `Features:AllowExperimentalFeatures=true` (NL-074, NL-206). Remove each from `ExperimentalFeatures` only when M5/M3b ships.
8. **DI divergence:** registrations in `NodeServiceExtensions` are not picked up by the hand-built DI in the Docker integration tests, and vice versa.
9. **Schema churn:** every persistence change needs 3 migrations and running DB containers. `HtlcEntity.AddMessageBytes` stores the wire format, so changing the `update_add_htlc` encoding affects stored rows.
10. **Blocked integration:** M4 depends on the large BOLT 2 normal-operation work (§7). Don't wire half an HTLC path that disconnects peers or loses funds. Keep the Sphinx library standalone until then.
11. **Legacy code risk:** unported assumptions such as the legacy hop format, a different ECDH hashing, or missing length checks. Only port what reproduces the vectors (§6).
12. **`LightningMoney` implicit conversions:** `long`/`ulong` convert implicitly in both directions and the unit is **msat** (`src/NLightning.Domain/Money/LightningMoney.cs:377-398`). Use explicit `MilliSatoshi` accessors/factories when encoding or decoding tu64 `amt_to_forward`/`total_msat`, so no sat/msat mixups slip through.
