# LNBolt (nbd-wtf/LNUnit) BOLT 4 Onion Review, for porting to NLightning

LNBolt paths are relative to a clone of https://github.com/nbd-wtf/LNUnit (reviewed at `master`, 2026-09-25). The authority is BOLT 4 (`04-onion-routing.md` upstream).

## Summary

- **What works.** The single-hop "happy path" peel works: the mu HMAC check over `hop_payloads || associated_data`, the XOR with a 2x1300-byte ChaCha20(rho) stream, and TLV parsing of the payload. The sender shared-secret chain, ECDH for compressed keys, blinding factor `SHA256(E||ss)`, the rho/mu/um keys, the ChaCha20 stream and `GenerateFiller` all match the spec. Against `bolt04/onion-test.json`, all 5 shared secrets match and hop 0 decodes (amt 15000, cltv 1500).
- **Why the user's interceptor worked.** The virtual node was always the **final** hop. The shared secret came from LND `DeriveSharedKey` or from `LNTools.DeriveSharedSecret` with a virtual key, and the callers only read `PaymentData.PaymentSecret` or keysend TLV 5482373484. They never used `nextSphinx`.
- **What is broken.** Forwarding (the next ephemeral key uses a scalar instead of a point), the next-HMAC offset (it comes from a lossy re-serialization), final-hop detection when the payload has extra TLVs, construction (no length prefix and no `pad` stream), `Peel` mutating `this`, signed `BigInteger` encoding, an HMAC compare that is not constant-time, a missing version/pubkey/length validation, and the complete absence of error onions (`ammag`, failure HMAC, `attribution_data`). Two probes confirmed these on the current spec vector: `docs/agents/reference/sphinx-harness/Program.cs` and a second throwaway LNBolt probe (not committed).
- **Recommendation.** Re-implement from the spec. Use LNBolt only as a readable outline. Do not copy any of it verbatim. `docs/agents/reference/sphinx-harness/Program.cs` contains a spec-direct Sphinx reference (NBitcoin + BouncyCastle ChaCha7539) that reproduces `onion-test.json` byte-for-byte. It is a better cross-check than LNBolt.

## Component table

| Component | LNBolt file | Conformance | Bugs (short) | Port recommendation |
|---|---|---|---|---|
| ECDH shared secret `DeriveSharedSecret` | `LNBolt/LNTools.cs:31-38` | Correct for 33-byte keys; buggy otherwise | Hashes the point with the compression flag of the input (`GetEncoded()`, L36). One static SHA256 instance shared across threads (L18). No pubkey validation. | Discard. Use NLightning `IEcdh.SecP256K1Dh`. |
| Sender secret chain `CalculatedSharedSecrets` + `GenerateBlindingFactor` | `LNBolt/LNTools.cs:40-68` | Correct (all 5 vector secrets match) | Dead `if (i >= hopPubKeys.Count) break` (L50-51) and one extra unused blinding. Relies on the variable-length scalar encoding. Shared SHA256. | Reference-only. Re-implement with NBitcoin.Secp256k1. |
| Scalar blinding `GenerateBlindedSessionKey` | `LNBolt/LNTools.cs:70-75` | Buggy | Signed `ToByteArray()` gives 31 or 33 bytes (about 51% of random inputs). Curve order n is a hard-coded decimal string (L73). Misused for point blinding in Peel. | Reference-only (the math `e*b mod n` is right). |
| rho/mu/um keys | `LNBolt/LNTools.cs:13-15, 106-127` | Incomplete | No `pad` key and no `ammag` key. | Trivial, so write fresh. |
| ChaCha20 stream `GenerateCipherStream` | `LNBolt/LNTools.cs:78-92` | Correct | Takes a zero buffer instead of a length. Callers pass their own 12-byte nonce even though `LNTools.Nonce` exists. | Port the idea (ChaCha7539, zero nonce, counter 0) into NLightning `ICryptoProvider`. |
| Filler `GenerateFiller` | `LNBolt/BOLT4/OnionBlob.cs:88-125` | Algorithm correct | Hop sizes come from the lossy `SphinxSize`. No check that the total is at most 1300. | Port-with-fixes, or write from the spec. |
| Construction `ConstructOnion` | `LNBolt/BOLT4/OnionBlob.cs:43-86` | Buggy | Mix header starts at zero instead of the `pad` stream (L49). Writes `ToDataBuffer()` with no length prefix but shifts by `SphinxSize + 32` (L59, L63). Lossy payloads. E0 not tied to the session key. | Discard. Re-implement. |
| Peel `Peel` / `CalculateNextEphemeralPublicKey` | `LNBolt/BOLT4/OnionBlob.cs:127-176` | Buggy (the single-hop happy path is correct) | Next E is scalar `ss*b` (L161, L172-176). next_hmac offset from `SphinxSize` (L153). Mutates `this` (L162-165). Hex-string HMAC compare that is not constant-time (L143). No version, pubkey or length checks. Null deref when neither secret nor key is given (L130-132). No path_key. | Reference-only for step order. Re-implement. |
| Packet framing | `LNBolt/BOLT4/OnionBlob.cs:8-40` | Correct layout (1+33+1300+32) | No 1366-byte length check and no version check. | Write fresh as a Domain value object. |
| Hop payload | `LNBolt/BOLT4/HopPayload.cs:5-160` | Buggy | `SphinxSize` re-encodes only types 2/4/6 (L25-34). Lossy `ToDataBuffer` (L50-81). Still accepts legacy 0x00 (L87-103). No ordering, duplicate, unknown-even or required-field checks. No minimal tu64/tu32 check. `PaymentData` length not checked (L149-154). No types 10/12/18. | Reference-only (type map). Re-implement on NLightning TLV. |
| BigSize / TLV | `LNBolt/BOLT1/BigSize.cs:5-64`, `LNBolt/BOLT1/TLV.cs:33-42`, `LNBolt/BOLT1/TLVTypes.cs` | BigSize correct. TLV.Parse unsafe. | `TLV.Parse` has no bounds check and casts ulong to int. Parsing is O(n^2) because it re-slices. | Discard. NLightning already has these. |
| Right-shift `CopyWithin` | `LNBolt/BigEndinessExtentions.cs:93-102` | Correct | Allocates a full copy of the array on each call. | Discard. Use `Span.CopyTo`. |
| tu64/tu32 decode | `LNBolt/BigEndinessExtentions.cs:68-85` | Buggy | Not minimal-checked. Oversize values throw `ArgumentException`. | Discard. |
| Error onion | `LNBolt/LNTools.cs:124-127` (um key only) | Missing | No failure codes, no failuremsg/pad, no HMAC, no ammag, no attribution_data, no origin decode. | Write from the spec. |
| ECKeyPair (+ commented ChaCha20) | `LNBolt/ECKeyPair.cs:628-701` (1-627 commented out) | Functionally correct | BouncyCastle EC scalar multiplication with secret keys is not constant-time. | Discard. NLightning uses NBitcoin.Secp256k1. |
| Interceptor / virtual node callers | `LNUnit.LND/LNDSimpleHtlcInterceptorHandler.cs:10-60`; `LNBolt.Tests/InterceptTests.cs:139-189`; `LNUnit.Tests/Abstract/AbcLightningAbstractTests.cs:252-345` | Usage is correct (AD = payment_hash) | All of it is commented out. The LNBolt ProjectReference is commented out (`LNUnit.Tests.csproj:70`). | Reference-only for the use-case and API shape. |
| Tests | `LNBolt.Tests/OnionTests.cs:1-91` | Not active | All commented out. They use old legacy vectors. L62 asserts `peel4` is null and then dereferences it. | Discard. Use the spec JSON vectors. |

## Confirmed bugs vs spec (with correct behavior)

These were checked against the source for this report and exercised by the reviewers' probes on `onion-test.json`.

1. **Next ephemeral key uses scalar math instead of a point.** In `OnionBlob.cs:161,172-176`, `CalculateNextEphemeralPublicKey` returns `GenerateBlindedSessionKey(sharedSecret, blindingFactor)`, which is `ss * b mod n` encoded as a signed BigInteger. On hop 1 this throws "Incorrect length for infinity encoding".
   **Spec:** `blinding_factor = SHA256(E_i || ss_i)`, then `E_{i+1} = blinding_factor * E_i`. This is point multiplication with a 33-byte compressed result.
2. **next_hmac offset comes from re-serialization.** `OnionBlob.cs:153` uses `hopPayload.SphinxSize`, and `HopPayload.cs:25-34` computes that from `ToDataBuffer()`, which emits only types 2, 4 and 6. On the vector, hop 1 is 83 bytes on the wire but gets sized as 19, so hop 2 fails the HMAC. The final hop is 275 bytes but gets sized as 9, so the final hop is **not** detected and a garbage `nextSphinx` is returned.
   **Spec:** the offset is `len(bigsize(length)) + length`, taken from the parsed prefix. The node is final only when `next_hmac` is all zeros at that true offset.
3. **Peel mutates `this`.** At `OnionBlob.cs:162-165`, `new OnionBlob(Version, EphemeralPublicKey = ..., HopPayloads = ..., NextHmac = ...)` uses C# assignment expressions, not named arguments. The fix is to use locals or real named arguments and keep the packet immutable.
4. **ConstructOnion drops the length prefix.** `OnionBlob.cs:63` writes `ToDataBuffer()`, but the shift at L59 is `SphinxSize + 32`. A round trip fails at hop 0 with `ArgumentOutOfRangeException`.
   **Spec:** each hop writes `bigsize(length) || payload || hmac`.
5. **No `pad` stream in construction.** At `OnionBlob.cs:49`, `new byte[1300]` starts from zeros.
   **Spec:** initialize the mix header with ChaCha20 keyed by `pad = HMAC-SHA256("pad", session_key)`. The zeros make it impossible to reproduce `onion-test.json`, and they leak route length.
6. **Signed scalar encoding.** `LNTools.cs:74` uses `BigInteger.ToByteArray()`, which produces 31 or 33 bytes.
   **Spec:** scalars are 32 bytes. Use `ToByteArrayUnsigned()` left-padded to 32, or NBitcoin.Secp256k1 `ECPrivKey.TweakMul`.
7. **ECDH hashes the point in its original encoding.** `LNTools.cs:36` calls `GetEncoded()`, which reuses the input's compression flag. An uncompressed input gives SHA256 of 65 bytes.
   **Spec:** SHA256 of the **compressed** point.
8. **Shared static `SHA256`.** `LNTools.cs:18` shares one `SHA256.Create()` instance, which is not thread-safe, and HTLC interceptors run concurrently. Use `SHA256.HashData`.
9. **HMAC compare is not constant-time.** `OnionBlob.cs:143` compares with `ToHex() != ToHex()` and throws a generic `Exception`.
   **Spec:** compare in constant time. On mismatch send `update_fail_malformed_htlc` with `invalid_onion_hmac` (BADONION|PERM|5) and `sha256_of_onion`.
10. **No packet validation.** There is no check of the version (`invalid_onion_version`, BADONION|PERM|4), the pubkey (`invalid_onion_key`, BADONION|PERM|6), the 1366-byte length, or that the length prefix plus HMAC fit in the decrypted buffer. `OnionBlob.cs:130-132` hits a null dereference if neither a secret nor a key is supplied.
11. **Legacy payload still accepted.** At `HopPayload.cs:87-103`, a leading 0x00 is treated as realm-0.
    **Spec:** legacy hop_data has been removed. A zero-length payload is invalid, so reject it with `invalid_onion_payload`. Separately, `Slice(8,16)` at L93 is a 16-byte slice, which only works by accident.
12. **No TLV stream validation.** `HopPayload.cs:110-141` silently puts everything else in `OtherTLVs`.
    **Spec (BOLT 1/4):** types must be strictly increasing with no duplicates. Unknown even types, and missing `amt_to_forward` (2) or `outgoing_cltv_value` (4), are rejected with `invalid_onion_payload` plus a failure offset. `short_channel_id` (6) is required for a non-final hop without `encrypted_recipient_data`. `payment_data` (8) must be present at a non-blinded final hop. tu64/tu32 must be minimal (`BigEndinessExtentions.cs:75-85` does not check this).
13. **Lossy serialization.** `HopPayload.cs:50-81` never writes 8, 16 or `OtherTLVs`, and `PaymentData` has a private setter. Final-hop, MPP and keysend payloads cannot be built.
14. **No route blinding.** TLV types 10 (`encrypted_recipient_data`), 12 (`current_path_key`) and 18 (`total_amount_msat`) are missing, and there is no path_key tweak of the node key.
15. **No error onion.** Only `GenerateUmKey` exists (`LNTools.cs:124-127`).
    **Spec:** `failuremsg` + pad, HMAC with `um`, an XOR stream with `ammag`, re-wrapping at each hop, `attribution_data` (HMACs + hold times), and origin-side decode and attribution.
16. **`TLV.Parse` is unsafe.** `BOLT1/TLV.cs:33-42` has no bounds check and casts a ulong length to int.

### Reviewer disagreements, resolved against source

- **Peel port value.** One reviewer said port-with-fixes, one said reference-only. The fixes touch the key step, the offset, the immutability, the compare and the validation, which is most of the method. **Resolved: reference-only.**
- **ConstructOnion.** Reviewers said discard, reference-only and port-with-fixes. It has no pad and no length prefix, and it depends on the lossy `SphinxSize`. **Resolved: discard and re-implement.** Only the step order is useful, and the spec gives that directly.
- **CalculatedSharedSecrets.** One reviewer said port-with-fixes, another said reference-only. It is correct, but it depends on BouncyCastle `ECKeyPair` and the signed encoding. **Resolved: reference-only.** Re-implement on NBitcoin.Secp256k1.
- **TLV.Parse line range.** One reviewer gave 33-42, another 29-40. The method starts at `BOLT1/TLV.cs:33`, so **33-42** is correct.
- **Legacy `Slice(8,16)`.** Reviewers described it slightly differently. `Span.Slice(start, length)` takes 16 bytes from offset 8, and only the first 8 are consumed. It is harmless but wrong, and the whole branch is off-spec anyway.
- **ECKeyPair "correct".** It is functionally correct but not constant-time with secrets. Discard it either way.

## Porting guidance mapped to NLightning

Paths are under `src/`.

| Need | NLightning location / action |
|---|---|
| Shared secret `ss = SHA256(compressed(k*E))` | Reuse `IEcdh.SecP256K1Dh` (`NLightning.Infrastructure/Crypto/Interfaces/IEcdh.cs:61`; implementation in `NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs:20-32`) with the node key from `ISecureKeyManager.GetNodeKeyPair()` (`NLightning.Domain/Protocol/Interfaces/ISecureKeyManager.cs:15`). |
| Point blinding `E*b` and scalar blinding `e*b mod n` | Add a Sphinx key-ops interface in Infrastructure.Bitcoin (or extend `IEcdh`) on NBitcoin.Secp256k1 3.2.0, which is already referenced (`Infrastructure.Bitcoin.csproj:29`). Use `ECPubKey` tweak-multiply for points and `ECPrivKey.TweakMul` for scalars. Always produce fixed 33- and 32-byte encodings. |
| Raw ChaCha20 IETF keystream (rho, pad, ammag) | Missing from `ICryptoProvider` (`NLightning.Infrastructure/Crypto/Interfaces/ICryptoProvider.cs:3-48`), which only has AEAD. Add a stream/xor method (12-byte zero nonce, counter 0) to each provider: libsodium `crypto_stream_chacha20_ietf_xor` via `LibsodiumWrapper`; Native/AOT via BouncyCastle `ChaCha7539Engine`, which is already referenced under `CRYPTO_NATIVE` (the same approach as `LNTools.cs:78-92`); and JS/WASM, where libsodium.js export availability is unverified and must be checked. |
| HMAC-SHA256 with short keys (`rho`, `mu`, `um`, `pad`, `ammag`) | Do not reuse `Hkdf.HmacHash` (`Crypto/Functions/Hkdf.cs:72-77` asserts a 32-byte key). Use `HMACSHA256.HashData` or a new helper, and verify it works on WASM. Compare with `CryptographicOperations.FixedTimeEquals`. |
| Onion packet | New Domain value object `OnionPacket` (version, E, 1300-byte payloads, hmac) with a strict 1366-byte and version-0 check. The raw bytes already arrive in `UpdateAddHtlcPayload.OnionRoutingPacket` (`NLightning.Domain/Protocol/Payloads/UpdateAddHtlcPayload.cs:54`; serializer `UpdateAddHtlcPayloadSerializer.cs:67-69`). |
| Hop payload TLVs | New Domain `HopPayload` model and TLV constants 2/4/6/8/10/12/16/18 in `TlvConstants`. Reuse `BigSize` (`NLightning.Domain/Protocol/ValueObjects/BigSize.cs`), `BaseTlv`, `TlvSerializer` and `TlvStreamSerializer` in Infrastructure.Serialization, with two caveats. First, `TlvStreamSerializer.DeserializeAsync` (`NLightning.Infrastructure.Serialization/Tlv/TlvStreamSerializer.cs:61-83`) reads to the end of the stream, so bound it with a MemoryStream sized from the BigSize prefix. Second, `TlvStream` uses a `SortedDictionary` (`NLightning.Domain/Protocol/Models/TLVStream.cs:11`), which hides out-of-order and duplicate records, so add wire-order, duplicate and unknown-even checks. Enforce minimal tu64/tu32 encoding. |
| Sphinx processor | Domain `IOnionProcessor` with its implementation in Infrastructure.Bitcoin. `Peel` should take a pluggable shared-secret source (private key, precomputed secret, or a remote-signer delegate) to keep the user's interceptor / virtual-node use case. It should return an immutable result: the payload, `isFinal`, and the next packet. |
| Error handling | Application layer: map peel and payload failures to `UpdateFailMalformedHtlcPayload` (BADONION codes + `sha256_of_onion`) or `UpdateFailHtlcPayload` (the um/ammag failure onion + `attribution_data`). Final-hop checks: `incorrect_or_unknown_payment_details`, `final_incorrect_cltv_expiry`, `final_incorrect_htlc_amount`, and MPP `total_msat`. |
| Route blinding | Parse 10/12/18, apply the path_key tweak to the node key, and decrypt `encrypted_recipient_data`. This is a later phase. |

Suggested phasing:
1. Crypto primitives: stream, HMAC, and blinding.
2. Packet and payload model with strict TLV.
3. Peel and construct, validated against `onion-test.json`.
4. Error onion and attribution.
5. Route blinding.

## License note

LNUnit and LNBolt are MIT (`LNUnit/LICENSE`, "Copyright (c) 2024-2025 nbd"; package authors field: Richard Safier). NLightning is also MIT (`LICENSE`), so the two are compatible. If any code is copied, keep the MIT notice. A clean re-implementation from the spec is recommended regardless.

Do **not** carry over LNBolt's `ServiceStack.Text` dependency. It is dual-licensed AGPL/commercial with FLOSS exceptions and is used only for hex/dump helpers. Portable.BouncyCastle is MIT-style and NBitcoin is MIT, but NLightning already has equivalents for both.

## BOLT 4 test vectors to validate the port

Upstream they are in `bolts/bolt04/` (https://github.com/lightning/bolts/tree/master/bolt04).

1. **`onion-test.json`** (5 hops, session key `0x41`x32, AD = payment_hash). This is the primary test:
   - all 5 shared secrets
   - the rho/mu/pad keys
   - the final 1366-byte packet byte-for-byte for construction
   - peeling each hop in turn, checking every payload, including the 83-byte hop 1 and the 275-byte final hop, which exercise bug 2
   - final-hop detection by an all-zero `next_hmac`
2. **`onion-error-test.json`**, together with the "Returning Errors" test vector in `bolt04.md` (from line 1892: failure message, um/ammag keys, per-hop wrapping, `attribution_data`, and origin decode). This validates the error onion that LNBolt lacks.
3. **`route-blinding-test.json`** and **`blinded-payment-onion-test.json`**. These cover the path_key tweak, `encrypted_recipient_data`, and blinded-payment peel. Use them for the route-blinding phase.
4. **`blinded-onion-message-onion-test.json`**, only if onion messages are in scope.
5. **Negative tests**, written by hand against the spec requirements: bad version, invalid pubkey, HMAC mismatch (checking that the compare is constant-time and that `invalid_onion_hmac` with `sha256_of_onion` is returned), a zero-length or legacy 0x00 payload, out-of-order, duplicate or unknown-even TLVs, non-minimal tu64, and a length prefix that overruns 1300 bytes. BOLT 1 BigSize/TLV vectors should already be covered by NLightning's existing tests.

Put the tests in `test/NLightning.Infrastructure.Bitcoin.Tests` (Sphinx) and `test/NLightning.Infrastructure.Serialization.Tests` (payload TLV). Do not port any of LNBolt's onion tests.
