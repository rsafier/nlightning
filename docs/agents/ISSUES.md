# NLightning Issue Ledger

The single durable issue ledger for this repo. GitHub issues are disabled on the fork, so this file replaces them. Every known bug, gap, spec violation, missing feature, test/CI hygiene problem and tech-debt item lives here, so nothing is lost between agent sessions.

Snapshot: 2026-09-25, `wip/fafo`. Sources: `docs/agents/{BOLT_COVERAGE,REPO_MAP,ONION_ROUTING_PLAN,LNBOLT_REVIEW}.md`, every `CLAUDE.md`, the onion M1/M2 workflow reports (open items, review fixes, final follow-ups), a `TODO`/`FIXME`/`NotImplementedException`/commented-out-file sweep, and a Release build. Bug claims were re-checked against the code at that snapshot; items still marked "unverified" in the evidence were not reproduced. Line numbers drift, so re-check the cited line before editing.

Updated 2026-09-25 after the fix swarm and its follow-ups were integrated into `wip/fafo` (at `1a38360`): statuses carry the `wip/fafo` SHAs (the swarm commits were cherry-picked with `-x`), and NL-203..NL-225 record the cross-batch review findings and the follow-ups the batches reported.

Updated 2026-09-25 after the four-lane work (l1 runtime, l2 BOLT 3/signer, l3 state machine, l4 onion M3) was integrated into `wip/fafo` (at `3c625e1`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `integrate:` commits have no lane counterpart), and NL-226..NL-238 record the lanes' new findings and open items.

Updated 2026-09-25 after ABCD wave 0 (W0-A engine seam + events, W0-B persistence, W0-C contracts, W0-D Bolt11, W0-E channel_update, W0-F multi-node harness) was integrated into `wip/fafo` (at `0b7e617`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`), and NL-239..NL-244 record the lanes' new findings (NL-239/NL-240 were already cited by ID in the W0-F harness).

Updated 2026-09-25 after ABCD wave 1 (W1-A channel wiring, W1-B payment core, W1-C payment schema, W1-D IPC/CLI, W1-E channel_update exchange and connect fixes) was integrated into `wip/fafo` (at `342d22e`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `4ae2eb3` and `342d22e` are `integrate:` commits), and NL-245..NL-255 record the lanes' new findings and seams.

## How to use this file

- **Fixing something:** in the **same commit** as the fix, set `Status: fixed (<short SHA>)` (or `fixed (partial, <SHA>)` and say what remains in Evidence). Do not delete the entry.
- **Finding something new:** append a new entry in the right area with the next free ID (`NL-###`, one higher than the current maximum anywhere in the file). Never renumber. Never reuse an ID.
- **Never delete an entry.** Mark it `wontfix` (say why) or `duplicate of NL-###`.
- Keep entries tight: one line of evidence per claim, file:line where possible.
- Update the summary table below when you add an entry or change a status or severity.
- Plan milestones (e.g. `ONION M3`) refer to `docs/agents/ONION_ROUTING_PLAN.md`; `BOLT2 N#-T#` refers to `docs/agents/BOLT2_NORMAL_OPERATION_PLAN.md`. `BOLT_COVERAGE.md` remains the per-BOLT status matrix; this file is the status source for individual bugs.

### Status legend

| Status | Meaning |
|---|---|
| open | Known, not being worked on. |
| in-progress | Someone is actively on it (name the branch in Evidence). |
| fixed | Done; the commit SHA is recorded next to the status. |
| wontfix | Deliberately not fixing; reason recorded. |
| duplicate | Covered by another NL ID (named next to the status). |

### Severity legend

| Severity | Meaning |
|---|---|
| critical | Funds loss, security, or consensus failure. |
| high | Breaks interop with peers or kills connections/channels. |
| medium | Spec deviation or incorrect behaviour without immediate funds/interop impact. |
| low | Hygiene, tech debt, docs, tooling. |

Kinds: `bug`, `gap` (missing feature; `[EPIC]` in the title marks a large one), `spec-violation`, `test`, `tech-debt`.

## Summary

| Status | critical | high | medium | low | Total |
|---|---|---|---|---|---|
| open | 5 | 9 | 29 | 45 | 88 |
| in-progress | 0 | 0 | 0 | 0 | 0 |
| fixed | 9 | 36 | 71 | 48 | 164 |
| wontfix | 0 | 0 | 1 | 2 | 3 |
| duplicate | 0 | 0 | 0 | 0 | 0 |
| **Total** | **14** | **45** | **101** | **95** | **255** |

### Epics

- NL-031: HTLC normal operation (add / fulfill / fail / malformed / commitment_signed / revoke_and_ack / update_fee) (open, critical; partial: N6 done in ABCD wave 1, local-only fail-back proven against LND; forwarding W2-B, reestablish NL-035)
- NL-034: Channel close (shutdown / closing_signed / option_simple_close) (open, critical)
- NL-035: channel_reestablish / option_data_loss_protect (open, critical)
- NL-037: Dual funding / interactive-tx (v2 open) (open, medium)
- NL-070: Error onions: failure messages, create / wrap / decrypt (ONION M3) (fixed, high; attribution_data NL-072 open)
- NL-073: Onion integration with HTLC flow: peel after lock-in, forward, final hop, send (ONION M4) (open, high; partial: M4-T2/T3/T4/T6 components exist, switch and send are W2-B/W2-C)
- NL-079: Route blinding payload handling (ONION M5) (open, medium)
- NL-094: On-chain handling: unilateral close sweeps, HTLC resolution, penalty/justice (open, critical)
- NL-099: BOLT 7 gossip: announcements, channel_update, queries, graph (open, high; typed and signed channel_update, direct exchange with the channel peer done)
- NL-114: Invoices not wired into the node: invoice store, create/pay commands, final-hop checks (open, high; invoice service, store, final-hop checks and CreateInvoice/ListInvoices done; pay is W2-C)
- NL-137: Payment/forwarding persistence: shared secrets, circuits, invoices, attempts, replay set, SCID map (open, high; partial: shared secrets, circuits, invoices, payments and HTLC origins persisted; replay set and SCID map remain)

---

## BOLT 1: Base protocol

### NL-001 Message TLV extensions accept unknown even TLV types
- **Status:** fixed (cd49ad9, 90a1905, 1d9bec5, 9e65cf8)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/*` (every serializer except `UpdateAddHtlcMessageSerializer.cs:74`)
- **Evidence:** Extensions are read with the open `TlvStreamSerializer.DeserializeAsync`, which cannot reject unknown even types. Only update_add_htlc uses `DeserializeStrictAsync`. Every message extension now uses `DeserializeStrictAsync` with its known-type set; a strict-TLV rejection answers with a connection `warning` (see NL-207).
- **Fix sketch:** Give each message serializer its known-type set and call `DeserializeStrictAsync`; add the BOLT 1 Appendix C init case (0xca) as a test.
- **Blocks/Blocked-by:** —
- **Plan ref:** ONION_ROUTING_PLAN §5 "M1/M2 as built"; BOLT_COVERAGE roadmap step 4; BOLT2 N0-T6 (touched messages)

### NL-002 init rejects a peer if any of its chains is unknown
- **Status:** fixed (e3d2ac3, 71d0944)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs:207-210`
- **Evidence:** `networkChainHashes.Any(h => !Features.ChainHashes.Contains(h))` disconnects; BOLT 1 only requires disconnecting when no chain is shared. Disconnects only when no chain is shared.
- **Fix sketch:** Disconnect only if the intersection is empty.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-003 init failures disconnect without sending error/warning
- **Status:** fixed (e3d2ac3, a6f1f9a)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (init validation)
- **Evidence:** Feature/network mismatches close the socket without an `error`/`warning`, so the peer gets no reason. Feature/chain failures send a `warning` after the peer's init; a first message that is not init disconnects silently (BOLT 1: send nothing before init).
- **Fix sketch:** Send `warning` (or `error` with all-zero channel id) before disconnecting.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-004 Pong is sent even when num_pong_bytes >= 65532
- **Status:** fixed (ad90605)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Protocol/Factories/MessageFactory.cs:145-153`, `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs:221-234`
- **Evidence:** Every ping is answered with `new PongMessage(ping.Payload.NumPongBytes)`; BOLT 1 says do not respond when `num_pong_bytes >= 65532`.
- **Fix sketch:** Skip the pong for that range; add a test.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-005 No ping rate limiting
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs:221-224`
- **Evidence:** Every incoming ping triggers a pong with no rate check; BOLT 1 allows failing peers that ping too often.
- **Fix sketch:** Track the last ping time per peer and ignore or disconnect on floods.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-006 Pong-timeout disconnect path is dead code
- **Status:** fixed (1433ea7, 3226906)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/PingPongService.cs:60-67`
- **Evidence:** On timeout `Task.Delay` ends Canceled, the loop takes `IsCanceled -> continue` and re-pings; `DisconnectEvent` never fires for an unresponsive peer. Pinging starts only after both inits; disconnect is idempotent.
- **Fix sketch:** Distinguish timeout from shutdown cancellation (separate CTS) and raise `DisconnectEvent` on timeout; unit test it.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-007 Ping/Pong payload serializers don't consume the ignored bytes
- **Status:** fixed (8f048be)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/Payloads/PingPayloadSerializer.cs`, `PongPayloadSerializer.cs`
- **Evidence:** The `ignored` byte count is checked but the bytes are left in the stream.
- **Fix sketch:** Read (skip) exactly `byteslen` bytes.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-008 remote_addr / address descriptor conversion broken for Tor v3 and DNS
- **Status:** open
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Tlv/Converters/RemoteAddressTlvConverter.cs:51-54,95-103`, `src/NLightning.Domain/Protocol/Tlv/RemoteAddressTlv.cs:32`
- **Evidence:** Tor v3 decode reads `Value[1..37]` (36 bytes, spec 35). DNS (type 5): Domain length `3 + len` (spec `4 + len`) and encode overwrites `customAddressBytes[1]`. Only IPv4 is tested.
- **Fix sketch:** Fix offsets/lengths per BOLT 7 address descriptors; add tests for all 5 types.
- **Blocks/Blocked-by:** Blocks NL-099 (node_announcement addresses)
- **Plan ref:** —

### NL-009 Our init never sends remote_addr
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:241`
- **Evidence:** Commented out with `TODO: Review this when implementing BOLT7`.
- **Fix sketch:** Send the peer's observed address once NL-008 is fixed.
- **Blocks/Blocked-by:** Blocked-by NL-008
- **Plan ref:** —

### NL-010 peer_storage / peer_storage_retrieval messages missing
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`
- **Evidence:** No message types, yet `option_provide_storage` is advertised Optional (see NL-109). Update: `option_provide_storage` now defaults to No and is in `FeatureOptions.ExperimentalFeatures` (not advertised without `AllowExperimentalFeatures`, e93eb41).
- **Fix sketch:** Stop advertising, or implement types 7/9 with storage limits.
- **Blocks/Blocked-by:** Related NL-109
- **Plan ref:** —

### NL-011 DeserializeMessageAsync&lt;T&gt; ignores the wire type
- **Status:** fixed (0d80672)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs`
- **Evidence:** The generic overload reads the u16 type and then uses T's serializer regardless.
- **Fix sketch:** Assert the wire type matches T.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-012 No signet / testnet4 chain hashes
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/ChainConstants.cs:20-40`
- **Evidence:** Only Main, Testnet, Regtest exist.
- **Fix sketch:** Add Signet and Testnet4 genesis hashes and network mappings.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-013 TlvStream is a SortedDictionary that silently re-sorts records
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Models/TLVStream.cs:11`
- **Evidence:** Wire order is lost on construction; deserialization now enforces order (NL-017), so the remaining risk is only in hand-built streams.
- **Fix sketch:** Keep as is, or preserve insertion order and validate on write.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-014 BigSize decode throws ArgumentException and needs a seekable stream
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs`
- **Evidence:** Non-canonical input throws `ArgumentException` (kept for compat); short reads are detected via `stream.Position/Length`.
- **Fix sketch:** Map to a serialization exception; use `ReadExactlyAsync`.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T5 open item

### NL-015 EndianBitConverter trim/pad semantics are non-compliant
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Converters/EndianBitConverter.cs`
- **Evidence:** LE trim/pad helpers don't implement BOLT truncated ints; onion code already uses `TruncatedInt`.
- **Fix sketch:** Migrate callers to `BinaryPrimitives` / `TruncatedInt`, then delete the helpers.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-016 BigSize decoding accepted non-canonical encodings
- **Status:** fixed (cd2d906)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/ValueObjects/BigSizeTypeSerializer.cs`
- **Evidence:** Non-minimal encodings were accepted and 3 spec vectors in `Vectors/BigSize.txt` were commented out. Now rejected; all 18 vectors active.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T5

### NL-017 TlvStreamSerializer: closed type switch (RemoteAddressTlv missing), no ordering checks
- **Status:** fixed (dbd7438, 9b9fdbd)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Tlv/TlvStreamSerializer.cs`
- **Evidence:** Serialize threw for unlisted TLV types; deserialize accepted out-of-order/duplicate types and read to end of stream. Now converter lookup by runtime type, raw `BaseTlv` passthrough, strictly-increasing check, `DeserializeStrictAsync`; BOLT 1 Appendix B vectors active (skip removed in 9b9fdbd).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T7

### NL-018 No strict tu16/tu32/tu64 codec
- **Status:** fixed (b7d0139, 5dd8370)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Converters/TruncatedInt.cs`
- **Evidence:** No minimal-encoding truncated ints existed; onion converters later switched from a private `OnionTruncatedInt` to the shared helper (5dd8370).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T6

### NL-203 Channel failures without a channel id went out as an all-zero `error`
- **Status:** fixed (699c67b, 00095cb, 4961ba5)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`HandleChannelMessageResponseAsync`), `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`default` branch), `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs` (`SendExceptionMessage`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 1. After NL-027, a missing channel_type threw `ChannelErrorException` with no id, sent as `ErrorPayload(null)`; the `default` branch did the same for every LND `channel_reestablish`, so an LND peer would fail all its channels with us on reconnect (BOLT 1). Now: ids attached per channel, unimplemented messages get a channel-scoped `warning`, unknown channels an `error` for that id; `Disconnect` disposes `MessageService` off the read loop (was a 5 s stall).
- **Fix sketch:** Done; the failed-channel state itself is NL-200.
- **Blocks/Blocked-by:** Related NL-200, NL-027, NL-023
- **Plan ref:** BOLT2 G1, G21

### NL-207 Malformed messages got a `warning` but the connection stayed open
- **Status:** fixed (963c04b, 00095cb)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/MessageService.cs` (`ReceiveMessage`), `src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs` (`RaiseException`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 5. The exception was wrapped in `ConnectionException`, and `RaiseException` only disconnected on `ErrorException`, so unknown-even TLVs, wire-type mismatches and malformed-without-BADONION only warned and dropped the message. Now warn and close; malformed-without-BADONION uses a channel-scoped warning + close.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-001, NL-011, NL-023, NL-024
- **Plan ref:** —

---

## BOLT 2: Wire layer

### NL-019 stfu is not a channel message and is silently dropped
- **Status:** fixed (76f8f8c, a6f1f9a, e93eb41)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Protocol/Messages/StfuMessage.cs:15`, `src/NLightning.Infrastructure/Node/Services/PeerService.cs:100-160`
- **Evidence:** `StfuMessage : BaseMessage`, and `PeerService.HandleMessage` only dispatches `IChannelMessage`/error/warning. `option_quiesce` is advertised Optional, so a peer that starts quiescence waits forever. stfu now gets a channel-scoped `warning` and the connection is closed (quiescence only ends on disconnect), and `option_quiesce` defaults to No and is experimental-gated. Real quiescence is NL-042.
- **Fix sketch:** Make stfu channel-scoped (or add a dispatch branch), and stop advertising `option_quiesce` until NL-042 is done.
- **Blocks/Blocked-by:** Blocks NL-042
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T4 (stop advertising quiesce)

### NL-020 closing_complete / closing_sig (option_simple_close) missing
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`
- **Evidence:** Types 40/41 absent; `Feature.OptionSimpleClose` exists in the enum only.
- **Fix sketch:** Add messages and serializers per the recipe in root CLAUDE.md.
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N11-T1

### NL-021 Splicing and start_batch messages missing
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`
- **Evidence:** No splice_init/ack/locked or start_batch types.
- **Fix sketch:** Later; after NL-042 and NL-037.
- **Blocks/Blocked-by:** Blocked-by NL-042, NL-037
- **Plan ref:** —

### NL-022 update_fail_htlc / update_fulfill_htlc lack attribution_data and fulfillment TLVs
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Messages/UpdateFailHtlcMessage.cs`, `UpdateFulfillHtlcMessage.cs`
- **Evidence:** No `attribution_data` (TLV 1) or fulfillment payload TLV (3) while `option_attribution_data` is advertised (NL-074).
- **Fix sketch:** Add `AttributionDataTlv` (920 bytes) + converter; wire into NL-072.
- **Blocks/Blocked-by:** Blocks NL-072
- **Plan ref:** ONION M3b

### NL-023 update_fail_malformed_htlc failure_code has no BADONION check
- **Status:** fixed (5adb882, 90a1905, 963c04b, 00095cb)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Payloads/UpdateFailMalformedHtlcPayload.cs`
- **Evidence:** `FailureCode` is a raw ushort; BOLT 2 says the receiver MUST fail the channel if the BADONION bit is not set. A missing BADONION bit gets a channel-scoped `warning` and the connection is closed (`ChannelWarningException { CloseConnection = true }`), BOLT 2's alternative to failing the channel until NL-200.
- **Fix sketch:** Validate in the malformed handler; conversion lives in NL-071.
- **Blocks/Blocked-by:** Blocked-by NL-031
- **Plan ref:** ONION M3-T3; BOLT2 N4-T2

### NL-024 Witness deserializer max-length check is commented out
- **Status:** fixed (532d979)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/ValueObjects/WitnessTypeSerializer.cs:46-48`
- **Evidence:** The `length > MAX_SIGNATURE_SIZE` guard is commented out, so an attacker-sized length is trusted.
- **Fix sketch:** Restore a bound (and bound by remaining stream length).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-025 HTLC rows stored under the old optional-onion framing no longer deserialize
- **Status:** fixed (4472a8b, a8d1381)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/HtlcDbRepository.cs` (`AddMessageBytes`)
- **Evidence:** Since e7b21f3 the onion is mandatory; rows saved without it throw on reload. Only matters for pre-existing dev databases. Update (ABCD wave 0, `0b7e617`): migration `AddCommitmentState` cuts the onion out of `AddMessageBytes` so legacy rows keep their data; `ChannelStateDbRepository.LoadAsync` and `ChannelDbRepository.GetByIdAsync` refuse such a channel with `LegacyHtlcStateException` (names NL-025), and multi-channel loads (`GetAllAsync`, `GetReadyChannelsAsync`, `GetByPeerIdAsync`, startup) log and skip only that channel (`Given_LegacyHtlcRow_When_TheChannelIsLoaded_Then_ItIsRefused`, `Given_LegacyChannelAndGoodChannelOfOnePeer_...`).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T9 open item; BOLT2 N5-T1

### NL-026 MessageFactory.CreateUpdateAddHtlcMessage cannot attach a BlindedPathTlv
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` (`CreateUpdateAddHtlcMessage`)
- **Evidence:** No path_key parameter.
- **Fix sketch:** Add `BlindedPathTlv?` parameter.
- **Blocks/Blocked-by:** Part of NL-079
- **Plan ref:** ONION M5

### NL-027 open_channel deserializer requires the channel_type TLV
- **Status:** fixed (7d834c0, 9e65cf8, 3c4af42, 699c67b)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/OpenChannel1MessageTypeSerializer.cs`
- **Evidence:** A missing channel_type throws at deserialization instead of being handled as a negotiation failure. channel_type is nullable on the wire; `ChannelOpenValidator`/`AcceptChannel1MessageHandler` reject a missing one with a channel-scoped error (see NL-203).
- **Fix sketch:** Deserialize as optional; enforce the requirement in the handler with a proper error.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-028 update_add_htlc onion was optional; truncated messages accepted with a null onion
- **Status:** fixed (e7b21f3, ffebaa1)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Payloads/UpdateAddHtlcPayload.cs`, `src/NLightning.Infrastructure.Serialization/Payloads/UpdateAddHtlcPayloadSerializer.cs`
- **Evidence:** Onion is now a mandatory 1366-byte `ReadOnlyMemory<byte>` read with `ReadExactlyAsync`; the local length const was replaced by `OnionConstants.PacketLength` in ffebaa1.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T9

### NL-029 update_add_htlc blinded path looked up with TlvConstants.UpfrontShutdownScript
- **Status:** fixed (e7b21f3)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateAddHtlcMessageSerializer.cs`
- **Evidence:** Same numeric value (0), wrong constant. Now `TlvConstants.BlindedPath`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T9

### NL-030 update_add_htlc accepted unknown even TLVs; malformed blinded_path leaked ArgumentException
- **Status:** fixed (37f6c30)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/UpdateAddHtlcMessageSerializer.cs:74`, `src/NLightning.Infrastructure/Protocol/Tlv/Converters/BlindedPathTlvConverter.cs`
- **Evidence:** Now `DeserializeStrictAsync` with `{BlindedPath}`; converter requires exactly 33 bytes and errors map to `MessageSerializationException`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issues 1-2

### NL-197 channel_reestablish next_funding TLV has the wrong type and shape; TLV 5 missing
- **Status:** fixed (1d9bec5)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Constants/TlvConstants.cs:93`, `src/NLightning.Domain/Protocol/Tlv/NextFundingTlv.cs`
- **Evidence:** `NextFunding = 0` and the TLV holds only a 32-byte txid; bolts master defines type 1 `next_funding` = `next_funding_txid ‖ retransmit_flags` and type 5 `my_current_funding_locked`. The serializer test fixture encodes type 0. `NextFunding = 1`, 33-byte value with `retransmit_flags`. TLV 5 (odd) is ignored, which BOLT 1 allows; model it with splicing.
- **Fix sketch:** Type 1 with the flags byte, parse-and-ignore TLV 5, strict known set {1,5}; fix `TxChannelReestablishMessageTests`.
- **Blocks/Blocked-by:** Part of NL-035
- **Plan ref:** BOLT2 N0-T6

### NL-198 closing_signed deserializer requires the optional fee_range TLV
- **Status:** fixed (1b68d6c)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/Types/ClosingSignedMessageTypeSerializer.cs:61-65`, `src/NLightning.Domain/Protocol/Messages/ClosingSignedMessage.cs`
- **Evidence:** Throws "Required extension is missing" when `fee_range` is absent; the spec makes it optional, so legacy peers' `closing_signed` would be rejected. Fixed: nullable `FeeRangeTlv`, strict known set {1}. N10 must negotiate with and without it (NL-034).
- **Fix sketch:** Nullable `FeeRangeTlv`, strict known set {1}; round-trip tests with and without it.
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N0-T6

### NL-199 commitment_signed has no funding_txid TLV
- **Status:** fixed (1b68d6c)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Messages/CommitmentSignedMessage.cs`, `src/NLightning.Infrastructure.Serialization/Messages/Types/CommitmentSignedMessageTypeSerializer.cs`
- **Evidence:** No TLV stream at all; bolts master says the sender MUST set TLV 1 `funding_txid` (receiver ignores a CS whose `funding_txid` does not match, outside splicing). Fixed: `FundingTxIdTlv` (type 1) + converter, strict known set {1}; `CreateCommitmentSignedMessage` always sets it. The receiver rule (ignore a CS whose funding_txid doesn't match, outside splicing) belongs to the N6 handler (NL-031). Update (ABCD wave 1, `342d22e`): the receiver needs no code: BOLT 2 applies the funding_txid ignore rule only inside `start_batch` (splicing); documented in `CommitmentSignedMessageHandler` (a604dff).
- **Fix sketch:** `FundingTxIdTlv` + converter, strict known set {1}, set it in `MessageFactory`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N0-T6

---

## BOLT 2: Behaviour layer

### NL-031 [EPIC] HTLC normal operation (add / fulfill / fail / malformed / commitment_signed / revoke_and_ack / update_fee)
- **Status:** open (partial: a604dff, f5315c0, a02afa7, a388fc4, aac5f60, e5f7312)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:88-133`
- **Evidence:** The switch handles only OpenChannel/AcceptChannel/FundingCreated/ChannelReady/FundingSigned; `default` (L133) throws `ChannelErrorException`, so any update_add_htlc, commitment_signed, update_fee etc. **disconnects the peer**. No handlers exist. Update: `default` now throws a channel-scoped `ChannelWarningException` (the peer stays connected), an unknown channel_id gets an `error` for that id, and malformed-without-BADONION warns and closes (699c67b, 00095cb). Update (four-lane integration, `3c625e1`): the building blocks now exist but nothing is wired: ordered per-peer processing and per-channel lock (NL-033, NL-193), per-side params (NL-194), separate commitment numbers (NL-188), HTLC txs and signatures (NL-056, NL-057), revocation guard (NL-189), remote shachain storage (NL-136), and the pure commitment engine with its two-engine invariant simulator (`Channels/Commitments/`, N4). Still missing: persistence of the engine state (N5), the handlers and `ChannelManager` cases (N6), reestablish (N7). The engine/builder seam is NL-230. Update (ABCD wave 0, `0b7e617`): the engine is now connected to the real signer (NL-230, 2fa8cf4), uses one fee calculator (NL-231, 192e212), raises lock-in/fulfill/irrevocable-fail/settle events with `IHtlcSwitch` as the consumer port (N4-T4, b166ea0), and is persisted with one save per transition (`IChannelStateDbRepository`, N5-T1..T3, 4472a8b, bb2731a); `IChannelOperations`, `ChannelState.Failed` and `ChannelFailedException` contracts exist (2ede2ee). Still missing: the handlers, `ChannelOperationsService`/`CommitScheduler` and `ChannelManager` cases (N6, ABCD W1-A), reestablish (N7, W2-A). Update (ABCD wave 1, `342d22e`): N6 is done. The seven receive handlers (`Application/Channels/Handlers/`, all through the scoped `ChannelStateTransitionService`: `ApplyAsync` + one save, then `UpdateCommitments`, then send; the RAA secret is revealed only after the save) and their `ChannelManager` cases (a604dff); the send side `ChannelOperationsService : IChannelOperations`, the debounced `CommitScheduler` (never signs while `RemoteNextCommit` exists, persists `SentCommitDiff` first) and `LocalOnlyHtlcSwitch`, gated by `NodeOptions.EnableHtlcs`, plus startup replay of `DerivePending` (a02afa7); each channel's link is pinned to the connection it turned Open on and every send-side update and signature needs that link (e5f7312). Proofs: in-process `TwoNodeHarness` (30 HTLCs each way, fulfills, fails, fee round, anchors and not, txids identical at every step, I7; f5315c0) and Docker N6-T5 against LND 0.20 (`NormalOperationFlowTests.Given_LndPaysUs_When_LockedIn_Then_FailedBackAndChannelActive`: fail-back decoded by LND at source index 1, commitment numbers 2/2, channel Active; a388fc4). Remaining: forwarding and final-hop receive (NL-073, ABCD W2-B), reestablish (NL-035, W2-A; until then a channel loaded at startup never sends updates, NL-252), fail-the-channel broadcast (NL-200). Deviation: a normal-operation message on a channel that is not Open gets warning + close rather than an error.
- **Fix sketch:** Handlers + ChannelManager cases for all 7 messages, commitment dance state machine, per-channel locking, persistence of every state transition. Sub-issues: NL-032, NL-033, NL-057, NL-056, NL-125, NL-051, NL-187, NL-188, NL-190, NL-193, NL-194, NL-200.
- **Blocks/Blocked-by:** Blocks NL-073, NL-034, NL-035, NL-094
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT_COVERAGE roadmap step 5; BOLT2 N4-N6

### NL-032 ChannelModel has no HTLC/balance/next-id mutators; HtlcState has 4 values
- **Status:** open (partial: c1f215f, 0edfa14)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs:40-61`, `src/NLightning.Domain/Channels/Enums/HtlcState.cs:3-8`
- **Evidence:** HTLC collections, balances, next ids and revocation numbers are get-only. `HtlcState` = Offered/Fulfilled/Failed/Expired, no commitment-dance stages. Update: the pure engine `Channels/Commitments/ChannelCommitments` holds HTLC records, msat balances, next ids and commitment numbers, and `HtlcState` now has core-lightning's 20 `htlc_state` values (10-19/30-39; legacy 0-3 kept decodable and rejected by the machine) driven by `HtlcStateTable` (c1f215f, 0edfa14). `ChannelModel` itself still has no mutators; N5 persists the engine state and N6 wires it.
- **Fix sketch:** Add add/settle/fail mutators and per-side commitment states (pending/committed/revoked, lock-in).
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT2 N4-T1

### NL-033 No per-channel ordering lock; PeerManager peer table not thread-safe; unobserved reply continuations
- **Status:** fixed (d60a891, 9c057f2)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs:38,300,339,379`, `ChannelManager.cs`
- **Evidence:** Two messages for one channel can race on the shared `ChannelModel`; `_peers` is a plain `Dictionary`; replies attached with `ContinueWith` are never awaited, so exceptions are lost. Fixed: `IChannelLockProvider` per-channel lock around every channel mutation (peer messages, funding confirmation, stale/backfill, startup registration), `ConcurrentDictionary` of peer sessions, one ordered inbound loop per peer and a `PeerOutbox` as the single send path (d60a891); a closed connection's inbound loop stops, a reconnecting peer replaces its old session, and simultaneous connects keep the lower pubkey's connection (9c057f2).
- **Fix sketch:** Per-channel async lock/queue; `ConcurrentDictionary`; await or log continuations.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** ONION_ROUTING_PLAN §7 "Per-channel ordering"; BOLT2 N0-T3

### NL-034 [EPIC] Channel close (shutdown / closing_signed / option_simple_close)
- **Status:** open
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Enums/ChannelState.cs` (Closing/Closed enum only), `src/NLightning.Infrastructure.Bitcoin/Transactions/ClosingTransaction.cs` (commented out)
- **Evidence:** Nothing moves a channel to Closing; an incoming shutdown disconnects the peer (see NL-031). Funds can only leave a channel via the peer's force close.
- **Fix sketch:** shutdown/closing_signed handlers with fee_range, closing tx builder (NL-065), then option_simple_close (NL-020). Needs a close IPC command (NL-152).
- **Blocks/Blocked-by:** Blocked-by NL-031 (must wait for HTLCs to clear)
- **Plan ref:** BOLT_COVERAGE roadmap step 11; BOLT2 N10 (legacy), N11 (simple close)

### NL-035 [EPIC] channel_reestablish / option_data_loss_protect
- **Status:** open
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:62`
- **Evidence:** TODO only; an incoming channel_reestablish disconnects the peer. `option_data_loss_protect` is advertised **Compulsory** (`FeatureOptions.cs:14`) with no implementation, and state needed for it (NL-136) is not persisted. Update: an incoming channel_reestablish now gets a channel-scoped `warning` instead of a disconnect (699c67b); `option_data_loss_protect` is advertised Optional (ASSUMED bit), not Compulsory. Update (ABCD wave 1, `342d22e`): W1-A left the reestablish hooks for N7 (W2-A): `ChannelStateTransitionService.LoadRemoteShachainAsync`, `SentCommitDiffCodec`, `CreateRevokeAndAck`, and `IPeerLivenessProbe.MarkLinkUp(channelId, peer)` plus the pending-event replay after reestablish (NL-252). `listchannels` reports `IsReestablished`, always false until N7 (e30a845).
- **Fix sketch:** Reestablish on reconnect with commitment/revocation number sync, retransmission, data-loss detection.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-136, NL-125, NL-126, NL-127
- **Plan ref:** BOLT_COVERAGE roadmap step 6; BOLT2 N7

### NL-036 Closing/Stale channels are not handled on startup
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:70`
- **Evidence:** `TODO: Deal with channels that are Closing, Stale, or any other state`.
- **Fix sketch:** Resume close / watch on-chain for these states.
- **Blocks/Blocked-by:** Part of NL-034, NL-094
- **Plan ref:** BOLT2 N7-T5, N10-T3 (partial)

### NL-037 [EPIC] Dual funding / interactive-tx (v2 open)
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs`, `src/NLightning.Infrastructure/Protocol/Validators/Tx*Validator.cs`
- **Evidence:** Messages and serializers exist; service not in DI, no handlers. `option_dual_fund` is advertised Optional but `ChannelFactory.cs:52,147` only rejects a Compulsory DualFund, so peers may attempt v2 opens we disconnect on.
- **Fix sketch:** Stop advertising (NL-109) until implemented; then handlers + validators. Sub-issues: NL-038, NL-039, NL-040, NL-041.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-038 Interactive-tx serial_id parity check semantics unclear
- **Status:** fixed (59c6f6b, a404c37)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/Tx{AddInput,AddOutput,RemoveInput,RemoveOutput}Validator.cs:9-13`
- **Evidence:** Rejects odd ids only when `isInitiator`; whether that means the local node or the sender is undocumented (unverified against BOLT 2 semantics).
- **Fix sketch:** Define `isInitiator` as "sender is initiator", check parity for both sides, add tests.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-039 TxAddInputValidator.Validate is async void
- **Status:** fixed (41af05c, a404c37)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs:8`
- **Evidence:** Exceptions from the awaited prevTx check escape the caller and can crash the process.
- **Fix sketch:** Return `Task` and await it.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-040 InteractiveTransactionService checks output serial ids against inputs
- **Status:** fixed (0bb9f76)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs:54-65`
- **Evidence:** `IsSerialIdUnique`/`IsSerialIdPresent` only look in `_inputs`.
- **Fix sketch:** Check the union of inputs and outputs (serial ids are shared across both).
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-041 Interactive-tx prevTx and script validation are TODOs
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs:49,57`, `InteractiveTransactionService.cs:48,69`
- **Evidence:** Output count and scriptPubKey of prevTx are not parsed.
- **Fix sketch:** Parse prevTx with NBitcoin; require segwit spend.
- **Blocks/Blocked-by:** Part of NL-037
- **Plan ref:** —

### NL-042 Quiescence (stfu) behaviour missing
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/`
- **Evidence:** No handler or state; `option_quiesce` advertised Optional. Update: `option_quiesce` now defaults to No and is experimental-gated (e93eb41); stfu gets warning + disconnect (NL-019).
- **Fix sketch:** Stop advertising until implemented; then stfu handling per BOLT 2.
- **Blocks/Blocked-by:** Blocked-by NL-019, NL-031
- **Plan ref:** BOLT2 N0-T4 (advertising only)

### NL-043 open_channel push_msat check is 1000x too lenient
- **Status:** fixed (f73a634, 1153f13)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:104`
- **Evidence:** `PushAmount > 1_000 * FundingAmount` where both are `LightningMoney` (msat); spec bound is `push_msat <= funding_satoshis * 1000`, i.e. `PushAmount > FundingAmount`. Also requires funding - push to cover the initial commitment fee (+ anchors); initiator rejects push > funding.
- **Fix sketch:** Compare `PushAmount > FundingAmount`; add a boundary test.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 3

### NL-044 Anchor/no-anchor commitment weight selection is inverted
- **Status:** fixed (f73a634, 1153f13)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:108-110`, `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:183-185`
- **Evidence:** `OptionAnchors > No ? ...WeightNoAnchor : ...WeightWithAnchor`. Fee check uses the peer's feerate_per_kw.
- **Fix sketch:** Swap the branches; test both.
- **Blocks/Blocked-by:** Related NL-061
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N2-T1

### NL-045 Local upfront_shutdown_script is never generated
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:101,235`
- **Evidence:** `TODO: Generate a script from the local key set`; the feature is advertised Optional. Update: `upfront_shutdown_script` now defaults to No (3c2b673). `ChannelFactory` still throws when the peer requires it; BOLT 2 allows sending a zero-length script instead.
- **Fix sketch:** Derive a wallet script (or send zero-length) and persist it for close.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T1

### NL-046 accept_channel rejected when channel_type present and upfront_shutdown_script absent
- **Status:** fixed (8ddae57, b08c494)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs:118-120`
- **Evidence:** Requirement triggers on `UpfrontShutdownScript > No || ChannelTypeTlv is not null`; only negotiation of `option_upfront_shutdown_script` should require it. Can reject valid peers. Incoming open half was NL-204.
- **Fix sketch:** Drop the channel_type condition.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-047 AcceptChannel1 error cleanup is inverted and leaks locked UTXOs
- **Status:** fixed (eb59385)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs:226-242`
- **Evidence:** Cleanup only runs when the channel id changed and then removes it from the temporary map; locked UTXOs are never released.
- **Fix sketch:** Remove from the correct map for both cases and unlock UTXOs.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-048 Initiator channel is not persisted before funding_signed
- **Status:** wontfix
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs`, `FundingSignedMessageHandler.cs`
- **Evidence:** A crash between funding_created and funding_signed loses the channel state (funding tx is not yet broadcast, so no direct loss). Spec-wrong: BOLT 2 "Message Retransmission" says a funder that has not broadcast the funding tx SHOULD NOT remember the channel on disconnect. Persisting was tried in eb59385 and reverted in d855f0f; `FundingSignedMessageHandler` persists before publishing.
- **Fix sketch:** Persist after funding_created is sent.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N7-T6

### NL-049 ForgetStaleChannels has no state filter and can mark open channels Stale
- **Status:** fixed (afbb108, ca64c66)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:189-205`, `src/NLightning.Domain/Channels/Models/ChannelModel.cs:20`
- **Evidence:** Selects `FundingCreatedAtBlockHeight <= height - 2016` for every channel. The field is 0 until confirmation, and old confirmed Open channels also match, so on any chain taller than 2016 blocks live channels are forgotten. Legacy fundee rows with height 0 are backfilled with the current height.
- **Fix sketch:** Filter to unconfirmed opening states and track the creation height explicitly.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N0-T7

### NL-050 ConfirmUnconfirmedChannels re-fires every block (commitment number drift, repeated channel_ready)
- **Status:** fixed (afbb108)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:231-267`, `src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs:43-47`
- **Evidence:** A ReadyForUs channel stays ReadyForUs; the handler only logs the wrong state, so every block increments CommitmentNumber and re-sends channel_ready.
- **Fix sketch:** Return early on wrong state; make confirmation idempotent.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N0-T7, N1-T1

### NL-051 channel_ready never stores the peer's second per-commitment point
- **Status:** fixed (4568921)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs:62`
- **Evidence:** Guard `CurrentPerCommitmentIndex == 0`, but the index counts down from 2^48-1, so the point is likely never updated and the first commitment update would use the wrong point.
- **Fix sketch:** Compare against the initial index (2^48-1); test.
- **Blocks/Blocked-by:** Blocks NL-031
- **Plan ref:** BOLT2 N1-T2

### NL-052 Failed startup reconnect skips channel registration
- **Status:** fixed (753cbd9)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs:69-72`
- **Evidence:** `TODO: Handle this case, maybe retry or log more details`; channels for that peer are never loaded/retried. Channels are registered before the connect attempt; registration is still fire-and-forget (NL-201).
- **Fix sketch:** Register channels regardless and retry connection with backoff.
- **Blocks/Blocked-by:** Related NL-035
- **Plan ref:** BOLT2 N1-T6

### NL-053 FundingCreatedMessageHandler flagged "REVIEW FULL FLOW"
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Handlers/FundingCreatedMessageHandler.cs:132`
- **Evidence:** Author TODO; no specific defect recorded.
- **Fix sketch:** Review the non-initiator flow against BOLT 2 and add tests.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-054 channel_ready: no application notification or routing-table update
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs:106-107`, `FundingConfirmedMessageHandler.cs:79-80`
- **Evidence:** TODOs only.
- **Fix sketch:** Raise a domain event; feed the graph once NL-099 exists.
- **Blocks/Blocked-by:** Blocked-by NL-099
- **Plan ref:** —

### NL-055 Handler discovery uses reflection, fragile under trimming/AOT
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/DependencyInjection.cs`
- **Evidence:** `Assembly.GetTypes()` scan registers `IChannelMessageHandler<>`; the Native/AOT configs may trim them.
- **Fix sketch:** Explicit registrations or a source generator.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-187 Second local per-commitment point is derived from index 1 instead of 2^48-2
- **Status:** fixed (b74e526)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs:52-54`, `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs:127-139`
- **Evidence:** The handler passes `CommitmentNumber.Value` (1) to `GetPerCommitmentPoint`, whose `commitmentNumber` parameter is passed unchanged to `GeneratePerCommitmentSecret` as the BOLT 3 index. The first point uses `FirstPerCommitmentIndex`, so the second RAA would break the peer's shachain. Fixed: `PerCommitmentIndex.From(n) = 2^48-1-n`, every signer per-commitment API takes a commitment number, and channel_ready's second point is at index 2^48-2.
- **Fix sketch:** Signer APIs take commitment numbers and convert with `index = 2^48-1-n`; add a `PerCommitmentIndex` helper; test the point sent in channel_ready.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N1-T1

### NL-188 One CommitmentNumber is shared by the local and remote commitments
- **Status:** fixed (b74e526, 0b8dd33, 72a4ac6, 3c625e1)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (`CommitmentNumber`), `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:225`
- **Evidence:** The factory uses `channel.CommitmentNumber` for both sides; local and remote numbers diverge during the commitment dance, so one of the two commitments gets the wrong obscured number. No local/remote commitment numbers are persisted. Fixed: `ChannelModel.LocalCommitmentNumber`/`RemoteCommitmentNumber`, `CommitmentNumber` is an immutable opener/accepter obscuring helper, both numbers are persisted (migration `PersistCommitmentNumbers`), the factory refuses a stored remote point that belongs to another number (72a4ac6; the missing second remote point is NL-232), and listchannels reports both (3c625e1).
- **Fix sketch:** Separate `LocalCommitmentNumber`/`RemoteCommitmentNumber` (persisted); make `CommitmentNumber` an immutable obscuring helper.
- **Blocks/Blocked-by:** Part of NL-031; related NL-069, NL-127
- **Plan ref:** BOLT2 N1-T1

### NL-190 Next HTLC ids start at 1 instead of 0
- **Status:** fixed (efd8a2f, 2d1fca6)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:133,253`
- **Evidence:** `ChannelModel` is created with `localNextHtlcId = 1` and `remoteNextHtlcId = 1`; BOLT 2 requires the first id to be 0. `Bolt3IntegrationTests.GetTestChannelModel` also passes 1. Fixed: new channels start both ids at 0 (efd8a2f); the `StoreMsatBalancesAndShortChannelId` data step resets 1/1 to 0 on channels without HTLC rows (2d1fca6).
- **Fix sketch:** Start at 0; unit test.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N1-T3

### NL-193 Channel handlers return one message; out-of-band sends are unordered
- **Status:** fixed (d60a891, a38b571)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/Interfaces/IChannelMessageHandler.cs`, `src/NLightning.Application/Node/Managers/PeerManager.cs:299-301,364-384`
- **Evidence:** `HandleAsync` returns `Task<IChannelMessage?>`; receiving `commitment_signed` must emit `revoke_and_ack` then possibly `commitment_signed`, and reestablish needs several ordered messages. `OnResponseMessageReady` sends fire-and-forget, with no ordering relative to replies. Fixed: handlers return `IReadOnlyList<IChannelMessage>`, raised in order under the channel lock; every send goes through the per-peer `PeerOutbox` (d60a891); the IPC open-channel path raises open_channel through `IChannelManager.StartOpeningChannelAsync` under the temporary channel's lock (a38b571). Remaining hazard: NL-234.
- **Fix sketch:** Return a list; route replies and events through one per-peer ordered outbox.
- **Blocks/Blocked-by:** Part of NL-031; related NL-033
- **Plan ref:** BOLT2 N0-T3

### NL-194 ChannelConfig is one-sided: one set of limits and one to_self_delay for both directions
- **Status:** fixed (2aba47d, de4c93d, 90a83d0, 49db838)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/ValueObjects/ChannelConfig.cs`, `ChannelFactory.cs:115-120`, `src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs:88-97`, `AcceptChannel1MessageHandler.cs:137-150`, `CommitmentTransactionModelFactory.cs:200`
- **Evidence:** Non-initiator copies the opener's reserve/htlc_minimum/max_accepted/max_in_flight/to_self_delay and echoes them back in accept_channel; initiator keeps its own limits but takes the peer's to_self_delay; the factory uses that one delay for both commitments. BOLT 2 add/fee limits can't be evaluated per direction. The Docker open test pushes 0, so the peer's commitment has no to_local output and the mismatch stays invisible (inferred). Fixed: `ChannelParams { Local, Remote }` of `ChannelParty`; accept_channel carries our values; the factory uses the counterparty's to_self_delay and the holder's dust per side; migration `SplitChannelParams` (+ `FlagInferredChannelParams`: rows from before the split carry `HasInferredParams` and must not be failed over those limits). Update (ABCD wave 0, `0b7e617`): the engine carries `HasInferredParams` as `CommitmentParams.HasInferredLimits`, and `UpdateValidator.ValidateReceiveAdd` then skips htlc_minimum, max_accepted/max_in_flight and the reserve part of B2-ADD-R02 (7ba115f).
- **Fix sketch:** Split into `ChannelParams { Local, Remote }` with documented direction rules; accept_channel sends our NodeOptions values; the factory uses the holder's delay per side; migration with a data step.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N1-T4

### NL-200 No failed-channel state; ChannelErrorException always just disconnects
- **Status:** open (partial: 699c67b, 00095cb, 4961ba5, 2ede2ee, 1390027, a604dff, a02afa7)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`HandleChannelMessageResponseAsync`), `src/NLightning.Domain/Channels/Enums/ChannelState.cs`
- **Evidence:** The spec's "send error and fail the channel" can't be expressed: nothing persists a failed state, refuses later updates or re-sends the error on reconnect. Partial: errors are now scoped to their channel id (699c67b); `ChannelWarningException.CloseConnection` gives "warning + close" where BOLT allows it (00095cb); `MessageService` dispose moved off the read loop (4961ba5). Remaining: no failed state; other `ChannelErrorException`s on Open channels (e.g. `ChannelReadyMessageHandler`) still make the peer force-close while we keep the channel (BOLT2 plan G21). Update (ABCD wave 0, `0b7e617`): contracts in place: `ChannelState.Failed = 35` (between Closing and Closed), `ChannelFailedException` (FailedChannelId, MustBroadcast, RequirementId; PeerMessage defaults to null so no local text leaks), `Channels.ErrorSent`/`DataLossDetected` columns (4472a8b). Persisting Failed + ErrorSent, sending the error, refusing updates and re-sending on reconnect remain (N6-T3, ABCD W1-A). Update (ABCD wave 1, `342d22e`): a handler's `ChannelFailedException` makes `ChannelManager` persist `ChannelState.Failed` and the serialized error (`MarkErrorSent`) under the lock before `PeerManager` sends it and disconnects; every later message on the channel is answered with the error again, Failed channels stay in memory at startup (a604dff), and every `IChannelOperations` call on a Failed channel is refused with nothing persisted (a02afa7). Remaining: re-send `ErrorSent` when the peer reconnects (B2-RE-05) and error without disconnect (PeerManager, W2-A); the broadcast / fail-the-channel service (N9-T4).
- **Fix sketch:** `ChannelFailedException`, `ChannelState.Failed = 35`, persisted error bytes, error retransmission; broadcast later via a single ChannelFailureService.
- **Blocks/Blocked-by:** Part of NL-031; related NL-094
- **Plan ref:** BOLT2 N6-T3, N9-T4

### NL-201 Startup connects before registering channels, and registration is fire-and-forget
- **Status:** fixed (424ae84, d39ca18, 9c057f2)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs:63-84`
- **Evidence:** `StartAsync` connects to each peer first and only then calls `_ = _channelManager.RegisterExistingChannelAsync(channel)` without awaiting; a peer's immediate `channel_reestablish` (LND sends it after init) can arrive for a channel that isn't registered yet. Update: since NL-052 (753cbd9) registration happens before the connect attempt, but `RegisterExistingChannelAsync` is still not awaited. Fixed: startup awaits registration (memory + signer) of every non-Closed/Stale channel before connecting; unreachable peers with active channels are retried with backoff (5 s doubling to 10 min) (424ae84), and so are peers with active channels that drop at runtime (9c057f2). Marking channels as awaiting reestablish is N7 (NL-035).
- **Fix sketch:** Load and await registration (incl. signer) for every non-Closed channel before connecting.
- **Blocks/Blocked-by:** Related NL-052, NL-035
- **Plan ref:** BOLT2 N1-T6

### NL-204 Incoming open_channel still required upfront_shutdown_script whenever channel_type was present
- **Status:** fixed (b08c494)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs` (`CreateChannelV1AsNonInitiatorAsync`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 2. NL-046 fixed only the accept_channel side; a peer sending channel_type without the TLV (allowed when the option is not negotiated) was rejected with an all-zero error. LND always sends the TLV, so LND opens were unaffected.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-046
- **Plan ref:** —

### NL-217 Outgoing open_channel sets announce_channel when scid_alias is negotiated Compulsory
- **Status:** fixed (2aba47d)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs:127-128`
- **Evidence:** `if (peer.NegotiatedFeatures.ScidAlias == FeatureSupport.Compulsory) channelFlags = AnnounceChannel`; BOLT 2 requires announce_channel = 0 when option_scid_alias is in channel_type. Unreachable by default (ScidAlias defaults to No). Reported by the features batch, verified in code. Fixed: `OpenChannelClientHandler` never sets announce_channel together with option_scid_alias. Public-channel policy: NL-236.
- **Fix sketch:** Never announce when scid_alias is in channel_type; decide announce from config.
- **Blocks/Blocked-by:** Related NL-103
- **Plan ref:** —

### NL-218 accept_channel builds its own channel_type instead of echoing the opener's
- **Status:** fixed (2aba47d)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Handlers/OpenChannel1MessageHandler.cs:70-84`
- **Evidence:** Uses `FeatureSet.NewBasicChannelType()`; BOLT 2 says an acceptor that sets channel_type MUST set it to the open_channel value (or fail). Harmless while we only accept the basic type. Reported by the features batch, verified in code. Fixed: accept_channel echoes the opener's channel_type bytes; `ChannelOpenValidator` refuses bits other than 12/22/46/50 and scid_alias when not negotiated; `AcceptChannel1MessageHandler` fails a channel_type different from ours.
- **Fix sketch:** Validate the opener's channel_type against what we support and echo it.
- **Blocks/Blocked-by:** Related NL-112
- **Plan ref:** BOLT2 N11 (anchors)

### NL-219 Interactive-tx input/output caps can be bypassed; input uniqueness compares raw prevtx bytes
- **Status:** open
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Protocol/Validators/TxAddInputValidator.cs`, `TxAddOutputValidator.cs`, `src/NLightning.Infrastructure.Bitcoin/Services/InteractiveTransactionService.cs` (`IsUniqueInput`)
- **Evidence:** BOLT 2 caps tx_add_input/tx_add_output messages received per negotiation (4096), but the validators count the inputs/outputs currently held, so remove + re-add gets around it; uniqueness compares prevtx bytes, not txid. Reported by the interactive-tx batch (unverified).
- **Fix sketch:** Count received messages per negotiation; compare by (txid, vout).
- **Blocks/Blocked-by:** Part of NL-037; related NL-041
- **Plan ref:** —

### NL-220 open_channel receiver has no rule for "both initial outputs <= channel_reserve"
- **Status:** fixed (e053fb8)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs`
- **Evidence:** BOLT 2 open_channel receiver MUST fail if both to_local and to_remote of the initial commitment are <= channel_reserve_satoshis. Today it is only rejected by accident, through the commitment factory throw that NL-196 removes. Reported by the open-validation batch. Fixed: `ChannelOpenValidator` rejects an open_channel whose initial to_local and to_remote are both <= channel_reserve_satoshis.
- **Fix sketch:** Add the check to the validator before fixing NL-196.
- **Blocks/Blocked-by:** Related NL-196
- **Plan ref:** —

### NL-221 A failed accept_channel leaves the channel registered with the signer
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/AcceptChannel1MessageHandler.cs` (catch block), `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs`
- **Evidence:** The NL-047 cleanup removes the channel and releases UTXOs but not `RegisterChannel` state (pre-existing; noted by the channel-lifecycle batch).
- **Fix sketch:** Unregister from the signer in the cleanup path.
- **Blocks/Blocked-by:** Related NL-047, NL-067
- **Plan ref:** —

### NL-227 open_channel carried funding minus push as funding_satoshis
- **Status:** fixed (0958f18)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs`
- **Evidence:** `funding_satoshis` was set from the opener's balance (funding − push), so for any channel with `push_msat` LND built a smaller funding output and rejected our commitment signature ("counterparty's commitment signature is invalid"). Found by the Proof-N1 Docker test. Fixed: open_channel sends `request.FundingAmount`; Daemon regression test `Given_PushAmount_When_HandleAsync_Then_OpenChannelCarriesTheWholeFundingAmount`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 Proof N1

### NL-234 IChannelManager.HandleChannelMessageAsync both sends and returns the replies
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Interfaces/IChannelManager.cs`, `src/NLightning.Application/Channels/Managers/ChannelManager.cs`
- **Evidence:** It raises every reply through `OnResponseMessageReady` under the channel lock and also returns them as `Task<IReadOnlyList<IChannelMessage>>`. Documented, but a new caller that sends the returned list would send each reply twice (reported by the N0-T3 lane). Update (ABCD wave 1, `342d22e`): not changed. Replies reach the peer only through `OnResponseMessageReady`; `PeerManager.cs:670` ignores the returned list, so nothing is sent twice today. The signature change belongs to the owner of `IChannelManager` (W2-A); `PeerManager.cs` was owned by W1-A in wave 1, not W1-E.
- **Fix sketch:** Return only a status/count, or stop raising and let the single caller enqueue.
- **Blocks/Blocked-by:** Related NL-193
- **Plan ref:** BOLT2 N6-T1

### NL-235 Block events keyed by the real channel id are not serialized against the handler that creates the channel
- **Status:** fixed (a604dff)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs`
- **Evidence:** A channel is locked by its temporary id until funding_created/funding_signed and by its real id afterwards, so a block event for the real id can run while the handler that is creating the channel still holds only the temporary-id lock. Harmless today because the channel is not in memory until that handler adds it (reported by the N0-T3 lane). Update (ABCD wave 1, `342d22e`): funding_created now runs under both the temporary-id lock and the real channel id lock (computed with `IChannelIdFactory`, temporary first); this is the only place two channel locks are held, and it is documented (`ChannelManagerNormalOperationTests.Given_FundingCreated_When_Handled_Then_TheRealChannelIdIsLockedToo`).
- **Fix sketch:** Take both locks in a fixed order during the id switch, or add the channel to memory only after releasing under the real-id lock.
- **Blocks/Blocked-by:** Related NL-033
- **Plan ref:** BOLT2 N5-T2, N6-T1

### NL-236 No public-channel policy: scid_alias is put in channel_type whenever negotiated and announce_channel is never set
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/ValueObjects/ChannelParams.cs` (`ToChannelType`), `src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs`
- **Evidence:** Since NL-217/NL-218 the initiator uses option_scid_alias in the channel_type whenever the peer negotiated it (`UseScidAlias` Compulsory) and never announces. There is no way to request a public (announced) channel without scid_alias (reported by the N1-T4 lane).
- **Fix sketch:** Add an open-channel option (public/private) that drops scid_alias from the type and sets announce_channel; design with BOLT 7 announcements.
- **Blocks/Blocked-by:** Related NL-099, NL-217
- **Plan ref:** —

### NL-246 Channels that got channel_ready before ABCD wave 1 have no commitment snapshot and can never carry HTLCs
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs`, `Channels/Managers/ChannelManager.cs` (`RegisterExistingChannelAsync`)
- **Evidence:** The first snapshot is built at the first channel_ready (NL-232, a604dff). Channels that received channel_ready earlier had the peer's commitment-0 per-commitment point overwritten, so no snapshot can be built: they are logged at startup and HTLC messages on them get a warning (reported by W1-A).
- **Fix sketch:** Close and reopen such channels (no automated path); or recover commitment 0's point via reestablish (`my_current_per_commitment_point` is not sent for commitment 0), so closing is the practical answer once N10 exists.
- **Blocks/Blocked-by:** Related NL-232, NL-034
- **Plan ref:** BOLT2 N6-T1

### NL-251 Ping-before-commit only checks that the peer is connected
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ConnectedPeerLivenessProbe.cs`, `CommitScheduler.cs`
- **Evidence:** BOLT 2 says to send a ping before commitment_signed when the peer has been quiet (B2-CS-S05). `IPeerLivenessProbe`'s default implementation only checks that the channel's pinned connection is still the peer's current one (a02afa7, e5f7312); there is no last-message timestamp or ping API on `IPeerService` (reported by W1-A).
- **Fix sketch:** Expose `IPeerService.LastMessageReceivedAt` (or a ping-and-wait API) and ping when it is older than a threshold; swap the probe with `services.Replace` or register it before `AddApplicationServices` (TryAdd).
- **Blocks/Blocked-by:** Related NL-031
- **Plan ref:** BOLT2 N6-T2

### NL-252 Reestablish seam: channel links are marked up only at Open, so channels loaded at startup never send updates and pending events are not replayed after reconnect
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ConnectedPeerLivenessProbe.cs` (`MarkLinkUp`), `Channels/Managers/ChannelManager.cs` (`QueuePendingDomainEventsAsync`, `RaiseDomainEventsAsync`), `ChannelOperationsService.cs`
- **Evidence:** Since e5f7312 a channel's link is pinned to the connection it turned Open on, and every `IChannelOperations` call and every commitment_signed needs that link, so no signature can cover an update the peer never received. A channel loaded at startup or after a reconnect is never marked, so its startup replay is refused (nothing persisted) and locked-in HTLCs stay unresolved until N7. An unsigned update enqueued just as its connection closes stays `SentRemoveHtlc` (the link stays down for good). A `ReadyForThem` channel loaded from the DB that turns Open on funding confirmation after a reconnect is pinned without reestablish (reported by W1-A).
- **Fix sketch:** In N7: after channel_reestablish call `IPeerLivenessProbe.MarkLinkUp(channelId, peer)`, retransmit or forget our unsigned updates and the stored `SentCommitDiff` per BOLT 2, then replay pending domain events (`QueuePendingDomainEventsAsync` + `RaiseDomainEventsAsync`).
- **Blocks/Blocked-by:** Part of NL-035; related NL-031
- **Plan ref:** BOLT2 N7-T3; ABCD W2-A

### NL-254 No node option for the dust-exposure policy
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/NodeOptions.cs`, `CommitmentParams.FromChannel` callers
- **Evidence:** The snapshot stores and reloads `MaxDustHtlcExposureMsat` (NL-242), but no option sets it, so the first snapshot is created with none and the dust-exposure check (BOLT 2 `max_dust_htlc_exposure_msat`) never runs (reported by W1-A).
- **Fix sketch:** Add a node option (e.g. `Node:MaxDustHtlcExposureMsat`, or feerate-scaled like LND) and pass it to `CommitmentParams.FromChannel` when creating the first snapshot.
- **Blocks/Blocked-by:** Related NL-242
- **Plan ref:** BOLT2 N9-T3

---

## BOLT 3: Transactions and scripts

### NL-056 HTLC-success / HTLC-timeout second-stage transactions not implemented
- **Status:** fixed (dfe8866, b222896)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/HtlcTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/HtlcTransactionBuilder.cs`
- **Evidence:** No builder, no Appendix C HTLC-tx vector tests. Fixed: Domain `HtlcTransactionModelFactory` + `HtlcTransactionBuilder` (unsigned tx, witness script, amount; `AddWitness`); all 33 Appendix C and 15 Appendix F HTLC txs byte-exact (`Bolt3HtlcTxVectorTests`, `Bolt3AnchorVectorTests`); the commented-out classes are deleted and the NL-056 test skip is gone.
- **Fix sketch:** Builders + Appendix C/F HTLC vectors.
- **Blocks/Blocked-by:** Part of NL-031; blocks NL-094
- **Plan ref:** BOLT_COVERAGE roadmap step 5; BOLT2 N2-T4

### NL-057 ILightningSigner has no HTLC-signature API; channel signing hardcodes input 0 + SIGHASH_ALL
- **Status:** fixed (615395c, 908bd32)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs`, `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs`
- **Evidence:** `htlc_signatures` in commitment_signed cannot be produced or verified; anchors need SIGHASH_SINGLE|ANYONECANPAY. Fixed: `SignRemoteHtlcTransactions`, `ValidateLocalHtlcSignatures` and `SignLocalHtlcTransaction` over `HtlcSigningContext` (remote sighash SINGLE|ANYONECANPAY with anchors, else ALL); `CommitmentSigningService` signs/verifies a commitment with its HTLC signatures in output order; Appendix C/F signer vectors byte-exact. `SignChannelTransaction` still assumes input 0 + ALL, which is right for commitment and closing txs.
- **Fix sketch:** Add HTLC tx sign/verify with sighash parameter.
- **Blocks/Blocked-by:** Part of NL-031; blocked-by NL-056
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT2 N3-T1

### NL-058 HtlcResolutionOutput swaps revocation and delayed keys
- **Status:** fixed (ddf5e8e)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Outputs/HtlcResolutionOutput.cs:14-16` vs `:23`
- **Evidence:** Ctor passes `(revocationPubKey, localDelayedPubKey)` into `GenerateHtlcOutputScript(localDelayedPubKey, revocationPubKey, …)`. Not used yet, but any second-stage output built with it would pay the wrong keys.
- **Fix sketch:** Fix argument order; add Appendix C script test.
- **Blocks/Blocked-by:** Blocks NL-056
- **Plan ref:** BOLT2 N2-T4

### NL-059 BaseOutput.Amount setter is a no-op
- **Status:** fixed (5582ca1)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Outputs/BaseOutput.cs:24`
- **Evidence:** `set => Money.Satoshis(value.Satoshi);` discards the result.
- **Fix sketch:** Assign the backing field (or remove the setter).
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N2-T4

### NL-060 BaseOutput ctor calls virtual ScriptType before subclass init
- **Status:** fixed (8995085, 017050a)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Outputs/BaseOutput.cs`, `ToRemoteOutput.cs`, `OfferedHtlcOutput.cs`
- **Evidence:** `ToRemoteOutput._hasAnchorOutputs` is still false when read; harmless today only because P2WPKH/P2WSH share a branch.
- **Fix sketch:** Pass script type into the base ctor.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-061 option_anchors commitment fee: weight 1116 vs 1124, only one anchor deducted
- **Status:** fixed (4f9f550, b222896)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:173,243-255`, `src/NLightning.Domain/Bitcoin/Transactions/Constants/TransactionConstants.cs:21-27`
- **Evidence:** `AdjustForAnchorOutputs` subtracts `AnchorOutputAmount` once; spec deducts two 330-sat anchors from the funder and uses base weight 1124. Appendix F vectors (`test/NLightning.Tests.Utils/Vectors/Bolt3AppendixFVectors.cs`) are unused. Anchors default No. Byte-exact against all 9 Appendix F vectors. Appendix F HTLC txs and every Appendix F commitment are also checked from the verbatim spec vectors (b222896).
- **Fix sketch:** Fix weights/deduction; wire Appendix F tests.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N2-T1, N2-T5

### NL-062 Commitment tx: suspect HTLC subtraction and to_remote dust limit
- **Status:** fixed (4f9f550, c0113c9)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs`
- **Evidence:** Every HTLC is subtracted from to_local; to_remote trimming uses the remote dust limit (spec: the commitment holder's). Unverified; Appendix C only has no-HTLC-from-remote cases. Balances are gross (include the owner's pending offered HTLCs); documented on `ChannelModel`.
- **Fix sketch:** Verify against BOLT 3 and Appendix C; fix and add vectors.
- **Blocks/Blocked-by:** Blocks NL-031
- **Plan ref:** BOLT2 N2-T1

### NL-063 Funding tx: no change-dust check; insufficient inputs throw ArithmeticException
- **Status:** fixed (7a1a728, 830fc92)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/FundingTransactionModelFactory.cs`, `src/NLightning.Infrastructure.Bitcoin/Builders/FundingTransactionBuilder.cs`
- **Evidence:** Dust change outputs can be created; failure surfaces as a generic arithmetic error.
- **Fix sketch:** Drop dust change into fee; throw a typed error.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-064 FundingTransactionBuilder mutates the model; funding output fixed at index 0
- **Status:** fixed (7a1a728, e89957c, 47e6a56)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Builders/FundingTransactionBuilder.cs`
- **Evidence:** Sets `FundingOutput.TransactionId` and `Index = 0` on the input model. Inputs and outputs BIP 69-sorted; builder returns the funding output index.
- **Fix sketch:** Return the result; compute the real output index (BIP69 ordering with change).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-065 Closing transaction builder not implemented
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Transactions/ClosingTransaction.cs` (commented out, `TODO: Find out correct lockTime`)
- **Evidence:** No closing tx; `BaseTransaction.cs`/`FundingTransaction.cs` in the same folder are also dead commented code.
- **Fix sketch:** New builder for closing_signed and closing_complete; delete the dead files.
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N10-T2

### NL-066 SecretStorageService: GetBasepointPrivateKey and LoadFromIndex throw NotImplementedException
- **Status:** fixed (e3145a5)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs:160,166`
- **Evidence:** The shachain can't be reloaded or used to derive basepoint secrets. Fixed: `Export()`/`Load(entries)` replace `LoadFromIndex` (Load validates bucket placement and the cross-bucket derivations), `GetBasepointPrivateKey` returns the stored key, and out-of-order secrets are rejected.
- **Fix sketch:** Implement both, backed by NL-136.
- **Blocks/Blocked-by:** Blocks NL-035, NL-094
- **Plan ref:** BOLT2 N3-T4

### NL-067 LocalLightningSigner: channel info memory-only; SignWalletTransaction not implemented
- **Status:** open
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs:52,189`
- **Evidence:** `TODO: Load channel key data from database`; after a restart channels must be re-registered by hand. `SignWalletTransaction` throws. Update: registration now carries `LocalCommitmentNumber` and `RemoteHtlcBasepoint` (615395c), and startup awaits it for every active channel before connecting (NL-201). Key data is still memory-only and `SignWalletTransaction` still throws.
- **Fix sketch:** Load channel key data from the DB on demand; implement wallet signing.
- **Blocks/Blocked-by:** Blocks NL-035, NL-094
- **Plan ref:** BOLT2 N1-T6 (re-registration only)

### NL-068 DustService is not registered in DI
- **Status:** fixed (3805db9)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Services/DustService.cs`
- **Evidence:** Implemented and unused.
- **Fix sketch:** Register in `AddBitcoinInfrastructure` when HTLC trimming needs it.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N10-T1

### NL-069 CommitmentNumber ctor names (local, remote) but needs (opener, accepter); Increment mutates
- **Status:** fixed (b3d2881)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Models/CommitmentNumber.cs`
- **Evidence:** Misleading names caused NL-127.
- **Fix sketch:** Rename parameters to opener/accepter; consider immutability.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N1-T1

### NL-189 Local commitment key derivation computes the current per-commitment secret; no revocation guard
- **Status:** fixed (615395c)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Services/CommitmentKeyDerivationService.cs:27`, `src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs`
- **Evidence:** `DeriveLocalCommitmentKeys` calls `ReleasePerCommitmentSecret` for the current commitment and returns it in `CommitmentKeys.PerCommitmentSecret`. It is not sent today, but nothing stops a caller from revealing the secret of an unrevoked commitment, which would let the peer take all channel funds. Fixed: `RevealPerCommitmentSecret(channelId, n)` throws unless n < the signer's local commitment number, which only `AdvanceLocalCommitment` (after persistence) moves; local keys are derived from the point and `CommitmentKeys` carries no secret.
- **Fix sketch:** Derive local keys from the point; make secret release internal behind a guard `n < LocalCommitted` advanced only after persistence; remove the secret from `CommitmentKeys`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N3-T2

### NL-195 option_anchors HTLC trim fee uses 666/706 weights instead of zero
- **Status:** fixed (e053fb8)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:105-115`, `src/NLightning.Domain/Bitcoin/Transactions/Constants/WeightConstants.cs:30-33`
- **Evidence:** With anchors the HTLC-timeout/success fee is 0 (zero-fee HTLC txs), so trimming uses the dust limit alone; the factory uses weights 666/706. Anchors default No. Fixed: `CommitmentFeeCalculator` uses HTLC tx fee 0 with anchors; Appendix F byte-exact. The duplicate fee code is NL-231.
- **Fix sketch:** Fee 0 with anchors in a `CommitmentFeeCalculator`; Appendix F vectors.
- **Blocks/Blocked-by:** Related NL-061
- **Plan ref:** BOLT2 N2-T1

### NL-196 Commitment factory throws when both outputs are below the channel reserve
- **Status:** fixed (e053fb8)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentTransactionModelFactory.cs:189-193`
- **Evidence:** The reserve is an update-validation rule, not a tx-building rule; Appendix C's "fee greater than funder amount" case needs the tx to build. Fixed: the factory no longer checks the reserve and the Appendix C 'fee greater than funder amount' case builds; the reserve is enforced by the N4 `UpdateValidator` and at open by `ChannelOpenValidator` (NL-220).
- **Fix sketch:** Remove the check from the factory; enforce the reserve in the update validator.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N2-T1

### NL-202 LightningMoney is a mutable reference type
- **Status:** open (partial: c1f215f, 0edfa14)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Money/LightningMoney.cs:7,19-25`
- **Evidence:** A class with a public `MilliSatoshi` setter; shared instances in commitment math can be changed through aliasing. Update: the N4 engine does its arithmetic in checked `ulong` msat and never uses `LightningMoney`; the type itself is still mutable.
- **Fix sketch:** Keep commitment-engine arithmetic in `ulong` msat with `checked`; consider making `LightningMoney` immutable.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N4-T1

### NL-230 Engine and builder use separate spec, signature and signer-port types; nothing adapts them
- **Status:** fixed (2fa8cf4)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Commitments/{CommitmentSpec,CommitmentSignatures,Interfaces/ICommitmentSigner,Interfaces/ICommitmentVerifier}.cs`, `src/NLightning.Domain/Channels/{Commitments/CommitmentTxSpec,Interfaces/ICommitmentSigner,Interfaces/ICommitmentVerifier}.cs`, `src/NLightning.Application/Channels/Services/CommitmentSigningService.cs`
- **Evidence:** The N4 engine's ports take a channel id + `CommitmentSpec` (holder view, `SpecHtlc`) and return `CommitmentSignatures`; `CommitmentSigningService` implements the other pair (channel + `CommitmentTxSpec`, returns `CommitmentTxSignatures`). The lanes were integrated by renaming (f4bf350); no code converts a `CommitmentSpec` into a `CommitmentTxSpec` and nothing implements the engine ports, so the engine cannot sign or verify a real commitment yet. Update (ABCD wave 0, `0b7e617`): one port family: the duplicates in `Domain/Channels/Interfaces/ICommitment{Signer,Verifier}.cs` are deleted; `EngineCommitmentSignerPort`/`EngineCommitmentVerifierPort`/`EngineRevocationVerifierPort` (Application, registered by `AddCommitmentEngineServices`) adapt via `CommitmentTxSpec.FromCommitmentSpec`, `CommitmentParams.FromChannel` and `CommitmentTxSignatures.ToCommitmentSignatures` over `CommitmentSigningService` (now a concrete class) and `IPerCommitmentSecretVerifier`. Proof: `EngineCommitmentPortsTwoNodeTests` (real `LocalLightningSigner` on both sides, add/CS/RAA/fulfill/fail/update_fee with and without anchors, txid signed == txid verified at every commitment; tampered/swapped sigs fail B2-CS-R01). Follow-up: NL-244.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Blocks NL-031 (N5/N6)
- **Plan ref:** BOLT2 N5 prerequisite, N6-T1

### NL-231 Two BOLT 3 commitment fee calculators
- **Status:** fixed (192e212, c68a34d)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Commitments/CommitmentFees.cs`, `src/NLightning.Domain/Bitcoin/Transactions/Factories/CommitmentFeeCalculator.cs`
- **Evidence:** The N4 engine (`CommitmentFees`) and the N2 factory (`CommitmentFeeCalculator`) implement the same weights and trimming separately; both are vector-tested today, but a fix to one can silently diverge from the other, and a divergence means our signature over the peer's commitment is invalid. Update (ABCD wave 0, `0b7e617`): `CommitmentFees` is deleted; `CommitmentFeeCalculator` holds the formulas once (`…Satoshis` members) and the engine, `UpdateValidator`, the test kits and the simulator call it; `CommitmentFeeCalculatorSpecTests` has a parity theory between the factory and spec overloads.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-230
- **Plan ref:** BOLT2 N5/N6 seam

### NL-244 CommitmentTxSpec.FromCommitmentSpec builds Htlc values with a null AddMessage
- **Status:** fixed (1de15f9)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Commitments/CommitmentTxSpec.cs:90`, `src/NLightning.Domain/Channels/Models/Htlc.cs`
- **Evidence:** The engine-to-builder adapter passes `null!` for the non-nullable `Htlc.AddMessage` because no builder reads it (documented in `src/NLightning.Domain/CLAUDE.md`); any future reader gets a NullReferenceException (reported by the W0-A lane, finding 3). Update (ABCD wave 1, `342d22e`): `Htlc.AddMessage` is nullable and `CommitmentTxSpec.FromCommitmentSpec` passes null without suppression; regression test in `EnginePortTests`.
- **Fix sketch:** Give the builders a slim HTLC input type or make `AddMessage` nullable.
- **Blocks/Blocked-by:** Related NL-230
- **Plan ref:** —

---

## BOLT 4: Onion routing

### NL-070 [EPIC] Error onions: failure messages, create / wrap / decrypt (ONION M3)
- **Status:** fixed (ded60a1, ce3cfeb, 9b2e294, 37df603, a657719, 3c1d68a, a42c33b)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Onion/{Models/FailureMessage,Interfaces/IFailureOnionService,Interpreters/FailureInterpreter}.cs`, `src/NLightning.Infrastructure.Bitcoin/Onion/FailureOnionService.cs`, `src/NLightning.Infrastructure.Serialization/Onion/FailureMessageSerializer.cs`
- **Evidence:** Only failure codes exist; `onion-error-test.json` packets are unused (only per-hop keys are checked). `um`/`ammag` keys are derivable via `SphinxKeyGenerator`. `ammagext` label unconfirmed. Fixed (ONION M3): `FailureMessage` + `FailureMessageSerializer` for every BOLT 4 code (with legacy 17 and PERM|16 recognised on receipt), `IFailureOnionService`/`FailureOnionService` create/wrap/decrypt (constant max(27, hops) iterations, constant-time HMAC) byte-exact against onion-error-test.json and the inline Returning Errors trace at every hop, malformed conversion (NL-071), `FailureChannelUpdateFactory` and the origin-side `FailureInterpreter`. attribution_data stays open (NL-072, M3b); nothing calls the error onion until N6/N8.
- **Fix sketch:** M3-T1..T3 per plan: model + serializer, create/wrap/constant-27-iteration decrypt, byte-exact against `onion-error-test.json` and the inline BOLT 4 trace. Sub-issues: NL-071, NL-072.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M3

### NL-071 update_fail_malformed_htlc → update_fail_htlc conversion missing
- **Status:** fixed (9b2e294, 3c1d68a)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Onion/Validators/MalformedHtlcValidator.cs`, `src/NLightning.Infrastructure.Bitcoin/Onion/FailureOnionService.cs`
- **Evidence:** No code path. Fixed: `FailureMessage.FromMalformed` (BADONION required), `MalformedHtlcValidator` (an all-zero sha256_of_onion is valid, e.g. `invalid_onion_blinding`) and `IFailureOnionService.CreateErrorPacketFromMalformed`; `MalformedFailureConversionTests` against BOLT 4 vectors.
- **Fix sketch:** M3-T3; reject non-BADONION codes (NL-023).
- **Blocks/Blocked-by:** Part of NL-070
- **Plan ref:** ONION M3-T3

### NL-072 attribution_data (error attribution) not implemented
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:69`
- **Evidence:** Advertised Optional; no TLV, no HMAC chain, no hold times.
- **Fix sketch:** M3b after M3; until then default to No (NL-074).
- **Blocks/Blocked-by:** Blocked-by NL-070, NL-022
- **Plan ref:** ONION M3b

### NL-073 [EPIC] Onion integration with HTLC flow: peel after lock-in, forward, final hop, send (ONION M4)
- **Status:** open (partial: 6156173, 234607e, a02afa7)
- **Severity:** high
- **Kind:** gap
- **Location:** planned `src/NLightning.Application/Payments/` (`HtlcSwitch`, `HtlcForwardingPolicy`, `FinalHopProcessor`, `PaymentManager`)
- **Evidence:** Nothing calls peel → replay → deserialize → validate. No forwarding, no final-hop checks, no sending. Update (ABCD wave 0, `0b7e617`): the engine-side gates exist: `IncomingHtlcLockedIn` fires once at lock-in, `OutgoingHtlcFailed` only when the removal is irrevocable, `OutgoingHtlcFulfilled` at once, all re-derivable at startup with `ChannelDomainEvents.DerivePending` (b166ea0); `IForwardingPolicy`/`ForwardingFee` (BOLT 7 fee formula), `ForwardCircuitModel` and `HtlcOrigin` contracts are in Domain (2ede2ee, 1390027). No processor, policy implementation or switch yet (ABCD W1-B, W2-B). Update (ABCD wave 1, `342d22e`): the payment core exists in `Application/Payments/` (6156173, 234607e): `IncomingOnionProcessor` (peel, replay record after a good peel, payload parse/validate, forward/final/malformed/failed results; route blinding refused), `FinalHopProcessor` (0x0013/0x0012 before the 0x400F invoice checks, read-only), `HtlcForwardingPolicy : IForwardingPolicy` (reads `RoutingOptions` on every call), `HintRouteBuilder` (mandatory fee limit) and `PaymentOnionFactory`. `LocalOnlyHtlcSwitch` peels every locked-in HTLC and fails it back (a02afa7). Nothing in `src/` calls the processor, policy or route builder yet: the forwarding switch (M4-T2 wiring, T4, T5, replay) is W2-B and send is W2-C. The final-hop accept must be atomic (NL-253) and the offered HTLC's origin persisted (NL-250).
- **Fix sketch:** M4-T1..T7 per plan. Prereqs: NL-031, NL-075, NL-078, NL-101, NL-102, NL-167, NL-137, NL-114.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-070
- **Plan ref:** ONION M4; BOLT2 N6-T1, N8 (direct-channel slice)

### NL-074 route_blinding and attribution_data advertised Optional without implementation
- **Status:** fixed (0dd030e)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:55,69`
- **Evidence:** Peers may send blinded HTLCs or attribution TLVs we cannot process.
- **Fix sketch:** Default both to No until M5/M3b.
- **Blocks/Blocked-by:** Related NL-109
- **Plan ref:** ONION_ROUTING_PLAN §9 risk 7; BOLT2 N0-T4

### NL-075 IHopPayloadSerializer declared in Infrastructure.Serialization (Application can't use it)
- **Status:** fixed (2abecac)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/Interfaces/IHopPayloadSerializer.cs`
- **Evidence:** Application must not reference Serialization; M4 HtlcSwitch needs the interface.
- **Fix sketch:** Move to `src/NLightning.Domain/Serialization/Interfaces/`.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M4 prerequisites; BOLT2 N8-T1

### NL-076 IHopPayloadSerializer resolves only if AddBitcoinInfrastructure registered ITlvConverterFactory
- **Status:** fixed (2abecac, d36bc41)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Serialization/DependencyInjection.cs`, `src/NLightning.Infrastructure.Bitcoin/DependencyInjection.cs`
- **Evidence:** Hidden cross-layer DI dependency (same for `TlvStreamSerializer`).
- **Fix sketch:** Register `ITlvConverterFactory` in the Infrastructure layer that owns it.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 payload open item

### NL-077 current_path_key (TLV 12) is not curve-validated
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Tlv/Converters/Onion/CurrentPathKeyTlvConverter.cs`
- **Evidence:** Length/prefix only; `0x02||ff×32` passes (test documents it). The update_add path_key is validated by the peeler.
- **Fix sketch:** Validate via `ISecp256K1Math` in M5 and map to `invalid_onion_blinding`.
- **Blocks/Blocked-by:** Part of NL-079
- **Plan ref:** ONION M5

### NL-078 Onion replay cache is in-memory FIFO, not cltv-keyed or persistent
- **Status:** open
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Onion/OnionReplayCache.cs`
- **Evidence:** 100k-entry FIFO; replays older than capacity or across restarts are accepted.
- **Fix sketch:** Key by HMAC with cltv_expiry eviction; persist (see NL-137).
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M4-T2/T7; M2 review issue 9 (partial)

### NL-079 [EPIC] Route blinding payload handling (ONION M5)
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** planned `src/NLightning.Infrastructure.Bitcoin/Onion/RouteBlinding/`
- **Evidence:** Peel applies the path_key tweak and exposes `PathKeySharedSecret`, but no `encrypted_recipient_data` decrypt, no blinded path builder, no `invalid_onion_blinding` remap in the validator. `route-blinding-test.json` is only loaded.
- **Fix sketch:** M5 per plan. Sub-issues: NL-077, NL-026.
- **Blocks/Blocked-by:** Blocked-by NL-073
- **Plan ref:** ONION M5

### NL-080 Onion messages (type 513) not implemented (ONION M6)
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`, `src/NLightning.Domain/Node/Interfaces/IPeerService.cs`
- **Evidence:** No type; `PeerService.HandleMessage` drops non-channel messages; `IPeerService.SendMessageAsync` only accepts `IChannelMessage`. Sphinx core already supports `OnionPacketKind.OnionMessage`.
- **Fix sketch:** M6 per plan.
- **Blocks/Blocked-by:** Blocked-by NL-079
- **Plan ref:** ONION M6

### NL-081 Basic MPP (final-hop HTLC sets) missing while basic_mpp is advertised
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:40`
- **Evidence:** `BasicMpp` default Optional; no HTLC set handling, no `total_msat` / MPP timeout (0x0017). Update: `basic_mpp` now defaults to No and is experimental-gated (e93eb41).
- **Fix sketch:** Implement in FinalHopProcessor (M4-T3); stop advertising until then (NL-109).
- **Blocks/Blocked-by:** Blocked-by NL-073
- **Plan ref:** ONION M4-T3; BOLT2 N0-T4 (stop advertising)

### NL-082 PeelAsLocalNode does a node-key EC multiplication per call and copies the key
- **Status:** fixed (896e3e6, 0b96267)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxService.cs`, `src/NLightning.Domain/Protocol/Interfaces/ISecureKeyManager.cs`
- **Evidence:** Copy is now zeroed (ca141a6), but each peel still materializes the private key.
- **Fix sketch:** Add an ECDH-with-node-key method to the key manager (touches daemon + Docker DI).
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issue 16 (partial)

### NL-083 Sphinx hot path allocates per hop (SphinxKeyGenerator / Sha256)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/SphinxKeyGenerator.cs`, `src/NLightning.Infrastructure/Crypto/Hashes/Sha256.cs:21`
- **Evidence:** One generator fewer per build after M2 review; no pooling, no benchmark. Partial (cfc9219, 831712e): node ECDH now 376 B/call instead of 952 (allocation test <= 512). Remaining: no BenchmarkDotNet benchmark, no `SphinxKeyGenerator` pooling, one managed key copy per call.
- **Fix sketch:** Benchmark, then pool generators.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issue 17 (partial); ONION_ROUTING_PLAN §9 risk 6

### NL-084 Truncated-int encoder duplicated in Domain and Infrastructure
- **Status:** fixed (8ba8530, d36bc41)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Onion/Tlv/TruncatedIntEncoder.cs`, `src/NLightning.Infrastructure/Converters/TruncatedInt.cs`
- **Evidence:** Domain can't reference Infrastructure, so an encode-only copy exists.
- **Fix sketch:** Keep one BCL-only implementation in Domain and use it from both.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T8 open item

### NL-085 Missing onion crypto primitives: HMAC (any key), raw ChaCha20 keystream, public EC tweak math
- **Status:** fixed (af0e16f, 6562629, 45df180)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Crypto/Functions/HmacSha256.cs`, `ICryptoProvider.StreamChaCha20IetfXor` (3 providers), `src/NLightning.Domain/Crypto/Interfaces/ISecp256K1Math.cs`
- **Evidence:** Previously only the private 32-byte-key `Hkdf.HmacHash`, AEAD-only ChaCha20, and private EC helpers in `KeyDerivationService`.
- **Fix sketch:** Done (RFC 4231 / RFC 8439 vectors, BOLT 4 hop-0 ECDH).
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T1..T4

### NL-086 No Sphinx construct/peel, onion packet or hop payload model
- **Status:** fixed (65d6c02, 737df73, a30aa49, 224e543, c0e1cb0, 2638a1d, 2f73f45)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Onion/`, `src/NLightning.Infrastructure.Bitcoin/Onion/`, `src/NLightning.Infrastructure.Serialization/Onion/HopPayloadSerializer.cs`
- **Evidence:** Re-implemented from spec (LNBolt not ported, see `LNBOLT_REVIEW.md`); byte-exact against `onion-test.json`, peel chain against `blinded-payment-onion-test.json` and `blinded-onion-message-onion-test.json`.
- **Fix sketch:** Done. Follow-ups tracked in NL-070, NL-073, NL-079.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T8, M2

### NL-087 default(OnionPacket) threw NullReferenceException
- **Status:** fixed (dc287a6)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Protocol/Onion/ValueObjects/OnionPacket.cs`
- **Evidence:** Accessors now throw `InvalidOperationException`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issue 7

### NL-088 JS ChaCha20 provider and KeyDerivationService left secrets on the heap
- **Status:** fixed (8683169)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/SodiumJsCryptoProvider.cs`, `src/NLightning.Infrastructure.Bitcoin/Services/KeyDerivationService.cs`
- **Evidence:** Buffers now zeroed in `finally`; the JS change was not compiled locally (Wasm is linux-only, see NL-169).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issues 5-6

### NL-089 FailureCode UPDATE-flag docs outdated; path keys undocumented as unvalidated
- **Status:** fixed (8b541ca)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Protocol/Onion/Enums/FailureCodeFlags.cs`, `FailureCode.cs`, `CurrentPathKeyTlv.cs`, `BlindedPathTlv.cs`
- **Evidence:** Docs now match BOLT 4 (channel_update may be empty).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1 review issues 3-4

### NL-090 Sphinx peel/construct review defects (blinded failure codes, framing secret, onion-message lengths, lost secrets)
- **Status:** fixed (ca141a6)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Onion/{OnionPeeler,OnionBuilder,SphinxService}.cs`, `src/NLightning.Domain/Protocol/Onion/Models/{ConstructedOnion,PeeledOnion}.cs`
- **Evidence:** With a path_key failures now map to `invalid_onion_blinding`; framing failures carry `OnionException.SharedSecret`; `OnionPacketKind` allows 0/1-byte onion-message payloads; `ConstructWithSharedSecrets` and `PathKeySharedSecret` keep secrets; blinded vector construct/peel tests added.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issues 3, 5, 12-15

### NL-091 HopPayloadValidator: unknown odd TLVs accepted in blinded hops; scid rejected at non-blinded final hop
- **Status:** fixed (993f24e)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Protocol/Onion/Validators/HopPayloadValidator.cs`
- **Evidence:** Blinded hops now use a strict allowlist; the final-hop scid rule is writer-only, so the reader ignores it.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issues 2, 8

### NL-092 invalid_onion_payload offsets differed between the two deserialize paths
- **Status:** fixed (896b531)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure.Serialization/Onion/HopPayloadSerializer.cs`
- **Evidence:** Offsets now count the stripped bigsize length prefix in both paths.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issue 4

### NL-093 Plan/ISphinxService said to peel with the payload's current_path_key; no DI resolution test
- **Status:** fixed (fba7dfe)
- **Severity:** medium
- **Kind:** bug
- **Location:** `docs/agents/ONION_ROUTING_PLAN.md`, `src/NLightning.Domain/Protocol/Onion/Interfaces/ISphinxService.cs`, `test/NLightning.Integration.Tests/BOLT4/OnionServiceRegistrationTests.cs`
- **Evidence:** Only the path_key received with the onion is used; replay recording moved after HMAC verification; DI resolution test added.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2 review issues 1, 9, 19

### NL-250 OfferHtlcAsync does not persist the HtlcOrigin with the add
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ChannelOperationsService.cs` (`OfferHtlcAsync`)
- **Evidence:** The `IChannelOperations` contract says the origin (Local payment hash or Forwarded incoming HTLC) is saved atomically with the add, so a restart can tie the outgoing HTLC back to its payment or circuit. `OfferHtlcAsync` validates the origin but does not store it (a02afa7); `IChannelStateDbRepository.SetHtlcOriginAsync` exists since 899e36b (reported by W1-A, integrator).
- **Fix sketch:** Call `SetHtlcOriginAsync(channelId, key, origin)` after `ApplyAsync` in the same unit of work, together with the payment/circuit rows (W2-B/W2-C).
- **Blocks/Blocked-by:** Blocks NL-073 (restart mid-forward); related NL-137
- **Plan ref:** ONION M4-T7; ABCD W2-B, W2-C

### NL-253 Final-hop invoice accept must be an atomic check-and-mark
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/FinalHop/FinalHopProcessor.cs` (read-only by design), future `Application/Payments/Switch/HtlcSwitch`
- **Evidence:** `FinalHopProcessor` never mutates the invoice, so two concurrent HTLCs for the same payment hash can both pass `Evaluate` and both be fulfilled; a second run for an already Accepted invoice fails with 0x400F, so a restart must act on the HTLC's persisted state instead of re-running the final hop (`IncomingOnionProcessor.ProcessAsync(checkReplay: false)` exists for that path) (reported by W1-B).
- **Fix sketch:** In the switch, under a per-payment-hash lock and in the same unit of work as the staged fulfill: re-read the invoice, `Evaluate`, `Accept` + `UpdateAsync`, save (or compare-and-set the status); fail the loser with PERM|15.
- **Blocks/Blocked-by:** Part of NL-073; related NL-114
- **Plan ref:** ONION M4-T3; ABCD W2-B

---

## BOLT 5: On-chain handling

### NL-094 [EPIC] On-chain handling: unilateral close sweeps, HTLC resolution, penalty/justice
- **Status:** open
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`, `src/NLightning.Domain/Bitcoin/Transactions/Models/PenaltyTransactionModel.cs` (empty)
- **Evidence:** No detection of commitment broadcasts, no sweeps, no HTLC on-chain resolution, no penalty tx. A revoked-state broadcast by a peer goes unpunished.
- **Fix sketch:** Watch funding outpoints, classify spends, sweep to_local/to_remote/HTLC outputs, justice txs. Sub-issues: NL-095, NL-096, NL-097, NL-098.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-056, NL-066, NL-136, NL-067
- **Plan ref:** BOLT_COVERAGE roadmap step 11; BOLT2 N9-T4 (broadcast only)

### NL-095 Revocation watch / penalty is a stub
- **Status:** open
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Interfaces/IRevocationWatchDbRepository.cs` (empty), `src/NLightning.Infrastructure.Persistence/Entities/Bitcoin/RevocationWatchEntity.cs` (not mapped), `BlockchainMonitorService.cs:189-198,400-411` (commented out), `src/NLightning.Infrastructure.Bitcoin/Transactions/PenaltyTransaction.cs`
- **Evidence:** Nothing is watched for revoked commitments.
- **Fix sketch:** Map the entity (key + config + 3 migrations), implement the repo and the watcher.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** BOLT2 N5-T1 (entity mapping)

### NL-096 BlockchainMonitorService has no reorg handling
- **Status:** open
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`
- **Evidence:** ZMQ rawblock only; confirmations and SCIDs are never rolled back.
- **Fix sketch:** Track block hashes; roll back watched-tx heights on disconnect.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** —

### NL-097 A block whose processing throws is never removed from the queue
- **Status:** fixed (cbf3184, a56185c)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`
- **Evidence:** The failing block is retried forever, stalling all later chain processing. Replayed blocks skip known deposits; the queue is capped at 144 and refilled from bitcoind. Follow-ups: NL-214, NL-215, NL-216.
- **Fix sketch:** Dequeue with bounded retry and alerting.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** —

### NL-098 No mempool (rawtx) monitoring
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs:248,400`
- **Evidence:** `TODO: Check for new transactions`, `TODO: Check for revocation transactions in mempool`.
- **Fix sketch:** Subscribe to ZMQ rawtx.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** —

### NL-214 A block that fails part-way can be partly persisted
- **Status:** open
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`ProcessBlock`)
- **Evidence:** Exceptions are caught per block but `SaveChangesAsync` still runs for the whole scope afterwards. Reported by the persist-misc batch (unverified).
- **Fix sketch:** One unit of work per block; save only when the block fully succeeds, otherwise discard the scope.
- **Blocks/Blocked-by:** Related NL-097, NL-133
- **Plan ref:** —

### NL-215 The tip block is not processed at startup
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`StartAsync`, `AddMissingBlocksToProcessAsync`)
- **Evidence:** Catch-up fetches only below the current height, so the tip waits for the next ZMQ block. Reported by the scid-chain batch (unverified).
- **Fix sketch:** Include the current height in the catch-up range.
- **Blocks/Blocked-by:** Related NL-097
- **Plan ref:** —

### NL-216 Halted chain processing is only logged
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`IsChainProcessingHalted`), `IBlockchainMonitor`
- **Evidence:** After NL-097 a poisoned block sets `IsChainProcessingHalted` and logs Critical; `IBlockchainMonitor` does not expose it and nothing fails the node or stops channel operations.
- **Fix sketch:** Expose the flag, surface it over IPC and refuse new channel operations (or stop the node) while halted.
- **Blocks/Blocked-by:** Related NL-097, NL-094
- **Plan ref:** —

---

## BOLT 7: Gossip

### NL-099 [EPIC] BOLT 7 gossip: announcements, channel_update, queries, graph
- **Status:** open (partial: e7b5269, 3ba2e4f, b93dd05, f9c54cc, 7c1ed4c, 5d1a9ed, 5f2a3ee)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs` (256-259 enum only; 261-265 absent)
- **Evidence:** No messages, no validation, no graph storage, no pathfinding; `channel_update` in failure messages must be empty. Sub-issues: NL-100, NL-101, NL-102, NL-103, NL-008. Update: 256-259 parse as raw `GossipMessage` and are dropped (156c375, NL-100); 261/263/265 are typed and queries get empty `reply_channel_range`/`reply_short_channel_ids_end` (9e17af2, NL-205). Remaining: announcements, channel_update, graph. Update (ABCD wave 0, `0b7e617`): `channel_update` (258) is typed (`ChannelUpdateMessage`/`ChannelUpdatePayload`, unknown trailing fields kept for the signature) and signed/verified with the node key (`ILightningSigner.SignNodeMessage`/`VerifyNodeMessage`); an LND-captured update parses byte-exact and verifies; `FailureChannelUpdateFactory` takes the typed update (e7b5269, 3ba2e4f). Remaining: sending/storing updates (ABCD W1-E), announcements, graph. Update (ABCD wave 1, `342d22e`): direct `channel_update` exchange with the channel peer (W1-E): `Application/Gossip/ChannelUpdateService` sends our signed update once a channel with a scid turns Open (under the channel lock, so it follows channel_ready) and again, unchanged, on every new connection to that peer; option_scid_alias channels use the peer's alias, no update when htlc_minimum exceeds capacity; inbound 258 is kept only if it is for our chain, names a channel with that peer, has the peer's direction, verifies with the peer's node key, is newer, not far in the future and not above capacity (b93dd05, f9c54cc, 7c1ed4c, 5d1a9ed). Docker `ChannelUpdateExchangeTests`: LND `GetChanInfo` shows our fee/CLTV policy and we store alice's. Remaining: announcements, graph, relay; LND never puts us into `addinvoice --private` hints without a node_announcement (NL-255).
- **Fix sketch:** Wire types first (so they stop killing peers), then announcement_signatures for public channels, graph store, gossip_queries.
- **Blocks/Blocked-by:** Blocks multi-hop sending in NL-073
- **Plan ref:** BOLT_COVERAGE roadmap steps 2, 12

### NL-100 Incoming even gossip types (256, 258, 262, 264) throw and kill the peer
- **Status:** fixed (156c375)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Serialization/Messages/MessageSerializer.cs:51,74`
- **Evidence:** Unregistered even type → `InvalidMessageException`. `gossip_queries` is advertised, so real peers send these.
- **Fix sketch:** Register message classes + serializers (both factory dictionaries) and route to a no-op/logging handler; keep gossip_queries advertised meanwhile.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T5

### NL-101 ShortChannelId(ulong) uses wrong masks
- **Status:** fixed (83d529d)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs:54-58`
- **Evidence:** tx index `& 0xFFFF` (should be `0xFFFFFF`), output `& 0xFF` (should be `0xFFFF`). Onion TLVs use the byte[] ctor, which is correct.
- **Fix sketch:** Fix masks; add round-trip tests for max values.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION M4 prerequisites

### NL-102 SCID transaction index counts only watched txs (and is ushort)
- **Status:** fixed (7df8f2d)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs:473-499`, `src/NLightning.Domain/Bitcoin/Transactions/Models/WatchedTransactionModel.cs:22`, `WatchedTransactionDbRepository.cs:67`
- **Evidence:** `index++` sits in `finally`, reached only after the `continue` for unwatched txs; `ushort` but BOLT 7 index is 24-bit. Every derived SCID (`ChannelManager.cs` ~317-320) is wrong.
- **Fix sketch:** Count every tx, widen to `uint` end to end incl. the persisted `TransactionIndex` column (3 migrations).
- **Blocks/Blocked-by:** Blocks NL-073, NL-099
- **Plan ref:** ONION M4 prerequisites

### NL-103 scid_alias values are random with no uniqueness check
- **Status:** fixed (1cc0488, 9e0915d, 1a38360)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/FundingConfirmedMessageHandler.cs`, `ChannelModel.LocalAliases`
- **Evidence:** Collisions across channels are not detected. Aliases are checked against known SCIDs/aliases (incl. persisted ones), reused on re-confirmation and persisted (`ChannelLocalAliases`, `Channels.RemoteAlias`; NL-209).
- **Fix sketch:** Check against existing aliases/SCIDs before use.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-205 gossip_queries negotiated but queries were never answered
- **Status:** fixed (9e17af2)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Node/Services/GossipQueryResponder.cs`, `PeerService.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 3. After NL-110/111, gossip_queries (default Optional) was really negotiated with LND, but query_channel_range / query_short_channel_ids were dropped, while BOLT 7 says the receiver MUST reply. Now: one `reply_channel_range` (sync_complete=1, no ids) and `reply_short_channel_ids_end` with full_information=0; bad queries get a warning; gossip_timestamp_filter is ignored.
- **Fix sketch:** Done; real gossip is NL-099.
- **Blocks/Blocked-by:** Part of NL-099
- **Plan ref:** —

### NL-209 scid aliases were not persisted and were regenerated after a restart
- **Status:** fixed (1a38360)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelLocalAliasEntity.cs`, `ChannelEntity.RemoteAlias`, `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`, `FundingConfirmedMessageHandler.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 7. The collision set started empty after restart and re-confirmation generated fresh aliases, breaking the BOLT 2 channel_ready alias MUSTs. Now `ChannelLocalAliases` (PK = alias) and `Channels.RemoteAlias` (migration `AddChannelScidAliases`, 3 providers); persisted aliases are reused and avoided.
- **Fix sketch:** Done; the real SCID is NL-225.
- **Blocks/Blocked-by:** Part of NL-103
- **Plan ref:** —

### NL-255 LND never adds route hints through us without a node_announcement
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Gossip/ChannelUpdateService.cs`
- **Evidence:** LND stores our direct `channel_update` (W1-E, b93dd05) but `addinvoice --private` still never hints through an NLightning node, because LND requires the hint node to be public (known through a node_announcement). The ABCD test uses explicit `route_hints` (roadmap decision B), so this does not block it (reported by W1-E).
- **Fix sketch:** Send a node_announcement (needs at least one announced channel: announcement_signatures, NL-236).
- **Blocks/Blocked-by:** Part of NL-099; related NL-236
- **Plan ref:** ABCD W1-E

---

## BOLT 8: Transport

### NL-104 Transport read loop uses ReadAsync; short TCP reads kill the connection
- **Status:** fixed (d0e0bbb)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Transport/Services/TransportService.cs:267,291`
- **Evidence:** Header (18 B) and body are read with a single `ReadAsync`; a partial read is treated as failure. A 1366-byte+ update_add_htlc often spans segments.
- **Fix sketch:** `ReadExactlyAsync` for both.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT_COVERAGE roadmap step 2; BOLT2 N0-T2

### NL-105 Messages are encrypted before taking the write lock (nonce desync)
- **Status:** fixed (de96c9e, d865a7e)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Transport/Services/TransportService.cs:201-209`
- **Evidence:** `_transport.WriteMessage` (nonce increment) runs before `_networkWriteSemaphore.WaitAsync`, so concurrent senders can write ciphertexts out of nonce order and the peer fails decryption (inferred, not reproduced). A frame is written whole once encrypted; a write fault closes the socket. The fault path has no dedicated test.
- **Fix sketch:** Hold the semaphore across encrypt + write.
- **Blocks/Blocked-by:** Blocks NL-073
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T2

### NL-106 Outgoing plaintext capped at 65519 bytes instead of 65535
- **Status:** fixed (9511b5c, 3ab999f)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Infrastructure/Transport/Encryption/Transport.cs:105`
- **Evidence:** `payload.Length + 16 > MaxMessageLength (65535)` rejects valid 65520-65535-byte messages.
- **Fix sketch:** Bound the plaintext (not ciphertext) by 65535; size buffers accordingly.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-107 IPv6 listen addresses unsupported
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Transport/Services/TcpService.cs`
- **Evidence:** Only IPv4 listen addresses parse.
- **Fix sketch:** Support `[::]:port` addresses.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-108 MessageService deserializes and runs handlers synchronously under a lock on the read loop
- **Status:** open (partial: d60a891)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/MessageService.cs`
- **Evidence:** Slow handlers stall reads (and ping handling) for that peer. Update: channel messages are now only queued on the read loop and handled by the per-peer inbound loop (d60a891); `MessageService` still deserializes on the read loop and ping/gossip replies still run there.
- **Fix sketch:** Queue messages to a per-peer consumer.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N0-T3 (partial)

### NL-228 PeerManager.StopAsync left the TCP listener bound
- **Status:** fixed (3d8bb07)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`StopAsync`)
- **Evidence:** `ITcpService.StopListeningAsync` was never called, so a node restarted in the same process (Docker test node, daemon restart) failed with "Address already in use". Found by the full Docker run. Fixed with `Given_Started_When_StopAsync_Then_TcpServiceStopsListening`.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N0-T8

### NL-229 PeerService could lose channel messages and its disconnect before anyone subscribed
- **Status:** fixed (d39ca18)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs`
- **Evidence:** The read loop is live before the constructor returns, so a channel message (e.g. LND's first message after init) or a disconnect that arrived before `PeerManager` subscribed was dropped. Fixed: pending channel messages are kept (up to `MaxPendingChannelMessages`, then disconnect) and replayed in order to the first subscriber; `OnDisconnect` fires once and is replayed to a late subscriber (`PeerServiceLifecycleTests`).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-033, NL-201
- **Plan ref:** BOLT2 N0-T3

### NL-239 NLightning-to-NLightning connect: the responder loses the initiator's init
- **Status:** fixed (df4ca92)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (init handshake), `src/NLightning.Application/Node/Managers/PeerManager.cs`
- **Evidence:** Seen by the ABCD W0-F multi-node harness (`Docker/MultiNodeHarnessTests.cs`): when one of our nodes connects to another, the responder sometimes logs "Failed to receive init message" (the first message is not `init`) and drops the connection. LND peers are not affected. The harness retries such connects (`NLightningTestNode.ConnectToAsync(NLightningTestNode)`, `KnownConnectBugLogFragments`, 6f1a316, cb06e60). Root cause not investigated. Update (ABCD wave 1, `342d22e`): the responder's transport read loop started before the message, peer-communication and peer services subscribed, so the initiator's init was raised to nobody. The transport now starts reading only once `MessageReceived` has a subscriber and each layer attaches to the one below on its own first subscriber. The harness retry tolerance is removed; `MultiNodeHarnessTests` asserts the log line never appears, and `PeerManagerConnectTests` run two real peer managers over loopback.
- **Fix sketch:** Reproduce with two in-process nodes; check whether the initiator's init is read before the responder's read loop/subscriber is ready (compare NL-229) or is consumed by the transport handshake; then drop the harness tolerance.
- **Blocks/Blocked-by:** Blocks the B-C hop of the ABCD e2e (flaky); related NL-229, NL-240
- **Plan ref:** ABCD W0-F

### NL-240 Simultaneous connect between two NLightning nodes can leave no live connection
- **Status:** fixed (df4ca92, 38d2f5e, b681f8a, 9ce68e0)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (simultaneous-connect tie-break), `src/NLightning.Infrastructure/Node/Services/PeerService.cs`
- **Evidence:** Seen by the W0-F harness: on a simultaneous connect the responder's `init` write fails ("Error initializing peer communication") while the other end's LND-style tie-break (lower pubkey's outbound wins, `SimultaneousConnectWindow`) keeps that same dead connection, so neither survives until a reconnect. Tolerated in `MultiNodeHarnessTests` (6f1a316, cb06e60). Update (ABCD wave 1, `342d22e`): writes checked the socket with Poll + Available, which the read loop could drain, so a live connection looked closed and the init write failed; writes now check `TcpClient.Connected`, a failed `PeerService` constructor disposes the stack, and `PeerManager` installs a session only after `IPeerService.WaitForInitAsync` (df4ca92). Follow-ups: an inbound connection whose init arrives after stopping began is closed (38d2f5e), a deterministic regression test for the write-side check (b681f8a), and no session is installed once stopping began; pending init waits are cancelled and inbound setups awaited on stop (9ce68e0).
- **Fix sketch:** Make the tie-break keep only a connection whose init exchange completed, or retry the survivor; test with two in-process nodes; then drop the harness tolerance.
- **Blocks/Blocked-by:** Related NL-239, NL-201
- **Plan ref:** ABCD W0-F

---

## BOLT 9: Features

### NL-109 Features advertised that are not implemented
- **Status:** fixed (0dd030e, 4d06f1b, 3c2b673, e93eb41)
- **Severity:** high
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs:14,24,31,40,65,67,76,83`
- **Evidence:** Defaults advertise `option_data_loss_protect` (Compulsory), `gossip_queries(_ex)`, `basic_mpp`, `option_dual_fund`, `option_quiesce`, `option_provide_storage`, `option_scid_alias` (partial) with no or partial implementation (route_blinding/attribution_data tracked in NL-074). Peers act on them and we disconnect or hang. Unimplemented features default to No and are refused (config validation) unless `Features:AllowExperimentalFeatures=true`. data_loss_protect stays Optional (ASSUMED) until NL-035; gossip_queries is now answered (NL-205).
- **Fix sketch:** Default each to No until implemented. Exception: keep gossip_queries until NL-100 is fixed (dropping it makes peers flood gossip). data_loss_protect is required by LND/CLN in practice, so fix NL-035 rather than dropping it.
- **Blocks/Blocked-by:** Related NL-019, NL-037, NL-081, NL-010
- **Plan ref:** BOLT_COVERAGE roadmap step 2; BOLT2 N0-T4 (partial)

### NL-110 Feature dependency table has only 2 entries
- **Status:** fixed (0e44e5a)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs:18-23`
- **Evidence:** Only gossip_queries_ex→gossip_queries and zeroconf→scid_alias. Missing e.g. basic_mpp→payment_secret, anchors→static_remote_key, payment_secret→var_onion_optin, route_blinding→var_onion_optin.
- **Fix sketch:** Encode the full BOLT 9 dependency table; test.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-111 Dependencies checked only on the remote set; no per-context feature filtering
- **Status:** fixed (0e44e5a, 4d06f1b, 3c2b673)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs`, `FeatureOptions.cs`
- **Evidence:** No I/N/C/9/B context masks; our own advertised set is not dependency-checked. Negotiated set: Compulsory if either side requires, Optional if both support; ASSUMED bits omitted by the peer count as supported.
- **Fix sketch:** Add context masks and validate local sets at startup.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-112 FeatureSet.DeserializeFromBytes reverses the caller's array in place
- **Status:** fixed (a3d5187, 89a6018)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs`
- **Evidence:** On little-endian hosts the input buffer is mutated. Raised from low: after the clone fix, multi-byte channel_type went out byte-reversed (`GetBytes` is little-endian); `FeatureSet.GetWireBytes()` is now used on both open paths.
- **Fix sketch:** Copy before reversing.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-206 Negotiated Optional features take effect; config could enable unimplemented features
- **Status:** fixed (e93eb41)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Node/Options/FeatureOptions.cs` (`ExperimentalFeatures`, `AllowExperimentalFeatures`, `GetValidationErrors`)
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 4. After NL-110/111, optional/optional negotiates as Optional, so setting OptionAnchors, OptionQuiesce, DualFund etc. in config would change behaviour with no implementation behind it. Now those features are refused at startup and never advertised unless `Features:AllowExperimentalFeatures=true`. LargeChannels (wumbo) enforcement checked: `ChannelOpenValidator` rejects >= 2^24 sat unless negotiated (tests only).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-109, NL-111
- **Plan ref:** —

### NL-226 FeatureSet.GetBytes dropped a highest set bit at a multiple of 8
- **Status:** fixed (d5a58ef)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Node/FeatureSet.cs` (`GetBytes`)
- **Evidence:** The byte count was `(lastIndexOfOne + 7) / 8`, so bit 8, 16, …, 48 as the highest set bit was cut off: channel_type zero_fee_commitments (40), payment_metadata compulsory (48), a global set whose only bit is 0. Found by the N1-T4 lane while echoing channel_type (NL-218). Fixed: `lastIndexOfOne / 8 + 1`; `GetSetBits`/`HasSameBits` added; two serializer tests that encoded the truncation were corrected.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-218
- **Plan ref:** BOLT2 N1-T4 (prerequisite)

---

## BOLT 10: DNS bootstrap

### NL-113 DNS seed bootstrap is fully commented out
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/DnsSeedClient.cs`, `test/NLightning.Integration.Tests/BOLT10/DNSBootstrapTests.cs`
- **Evidence:** 0 lines of live code in either file.
- **Fix sketch:** Restore and test, or delete.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

---

## BOLT 11: Invoices

### NL-114 [EPIC] Invoices not wired into the node: invoice store, create/pay commands, final-hop checks
- **Status:** open (partial: 6156173, 234607e, 899e36b, 6cfbcd1, c10a78e, 4ae2eb3)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Bolt11` (no `src/` project references it), `src/NLightning.Domain/Client/Enums/ClientCommand.cs`
- **Evidence:** No invoice/preimage table, no CreateInvoice/PayInvoice IPC, no payment_secret checks. Update (ABCD wave 0, `0b7e617`): `Invoice.Encode` validates first and rejects unknown even and doubled feature bits; the node-key encode path is covered and LND 0.20 invoice fixtures decode (NL-120, 2d8a9fe, 2c9f812, f93d059); `InvoiceModel`, `IInvoiceService`, `IInvoiceDbRepository` and `ClientCommand` 9-12 contracts exist (2ede2ee). No invoice store, service or final-hop processor yet (ABCD W1-B, W1-C). Update (ABCD wave 1, `342d22e`): Application references Bolt11; `InvoiceService : IInvoiceService` creates (CSPRNG preimage/secret), signs with the node key and persists invoices (features 8/14 compulsory, `c` from `Routing.InvoiceMinFinalCltvExpiry`, no route hints, NL-245) and the final-hop checks exist (6156173, 234607e); the invoice table and repository (899e36b); CreateInvoice/ListInvoices/PayInvoice/ListPayments IPC and CLI (6cfbcd1, c10a78e); composition root registration (4ae2eb3). CreateInvoice and ListInvoices work end to end; PayInvoice/ListPayments answer "not available" until `IPaymentService` exists (W2-C). Receive is not wired: the switch still fails every HTLC (W2-B).
- **Fix sketch:** Reference Bolt11 from Application/Daemon, invoice store (NL-137), IPC commands (NL-152), FinalHopProcessor (M4-T3), PaymentManager (M4-T6).
- **Blocks/Blocked-by:** Blocked-by NL-073
- **Plan ref:** ONION M4-T3, M4-T6; BOLT2 N8-T2, N8-T3 (partial)

### NL-115 MinFinalCltvExpiry returns null instead of the default 18
- **Status:** fixed (0879269, f41f4ba)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/TaggedFields/MinFinalCltvExpiryTaggedField.cs`, `Invoice.cs`
- **Evidence:** Absent `c` yields null.
- **Fix sketch:** Return 18 when absent.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N8-T3

### NL-116 Only the first r (route hint) field is kept
- **Status:** fixed (3948fda, 35f0809, 0e3a865)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/TaggedFieldList.cs:32`
- **Evidence:** Uniqueness rule drops later `r` fields; BOLT 11 allows several.
- **Fix sketch:** Allow repeated `r`; expose a list.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-117 RoutingInfo stores u32/u16 spec fields as signed int/short
- **Status:** fixed (dbe4f98, 0e3a865)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Models/RoutingInfo.cs:17-19`
- **Evidence:** fee_base > 2^31-1 or cltv_delta > 32767 fails `IsValid`.
- **Fix sketch:** Use uint/ushort.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-118 Fallback address field has no taproot (witness v1)
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Bolt11/Models/TaggedFields/FallbackAddressTaggedField.cs`
- **Evidence:** Unknown versions are skipped.
- **Fix sketch:** Support v1 (P2TR).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-119 Invoice feature bits are not validated
- **Status:** fixed (aadfd91)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/Invoice.cs:553`
- **Evidence:** `TODO: Check feature bits`; unknown even features accepted; writer doesn't force payment_secret/var_onion_optin.
- **Fix sketch:** Reject unknown even bits on decode; set required bits on encode.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N8-T2 (encode side)

### NL-120 Invoice.Encode never runs InvoiceValidationService
- **Status:** fixed (2d8a9fe, f93d059)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Bolt11/Services/InvoiceValidationService.cs`, `Models/Invoice.cs`
- **Evidence:** Validation runs on decode only. Update (ABCD wave 0, `0b7e617`): `Invoice.Encode` runs `InvoiceValidationService` before encoding and rejects unknown even and doubled feature bits; `InvoiceNodeEncodingTests` cover encode with a node key, decode and validate (features 9/14 compulsory, multiple `r`), and `LndInvoiceFixtureTests` decode LND 0.20 invoices (2c9f812).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-121 Tagged-field decode errors are swallowed
- **Status:** fixed (ea8b938, 344475d, f41f4ba)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Bolt11/Models/TaggedFieldList.cs:154-181`
- **Evidence:** Exceptions go to `Debug.WriteLine`; an over-long declared length `continue`s without skipping bits, misparsing the rest.
- **Fix sketch:** Skip unknown fields by declared length; fail on malformed known fields.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-122 Invoice API hazards: ToString() NRE without a key manager, setters throw on second set
- **Status:** fixed (6934c2c, 475d22b)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Bolt11/Models/Invoice.cs`
- **Evidence:** Documented in `src/NLightning.Bolt11/CLAUDE.md` gotchas.
- **Fix sketch:** Explicit exceptions / replace semantics.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-123 LightningMoney.Bits() returns the same value as Cents()
- **Status:** fixed (212cc89)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Money/LightningMoney.cs:197-208`
- **Evidence:** Both multiply by `Cent`; tests assert the wrong behaviour. No callers in `src/`.
- **Fix sketch:** Use the bit multiplier (100 sat); fix tests.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-124 BitWriter mask typo and ArrayPool misuse
- **Status:** fixed (04c8bb2, b7f407e)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Utils/BitWriter.cs:26-37,130,217`
- **Evidence:** `(1 >> bits) - 1` should be `(1 << bits) - 1` (L130); `Array.Resize` on the rented buffer returns a non-pooled array to the pool (L217). Used by BOLT 11 and FeatureSet.
- **Fix sketch:** Fix the shift; track the rented array separately.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-213 High-S invoice signatures were accepted when an n field is present
- **Status:** fixed (298919d)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/Invoice.cs` (`CheckSignature`)
- **Evidence:** The BOLT 11 invalid vector "Non canonical signature (high-S) with n field defined" decoded. Now low-S is required with n; recovery without n still accepts both. Found by the bolt11 batch reviewer.
- **Fix sketch:** Done (vector added to the invalid set).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-222 Non-minimal c/x/9 data_length accepted; `Invoice.Signature` stale after re-signing
- **Status:** open
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Bolt11/Models/TaggedFields/{MinFinalCltvExpiry,ExpiryTime,Features}TaggedField.cs`, `Invoice.cs` (`Signature`, `Encode(Key)`)
- **Evidence:** BOLT 11 says a reader SHOULD treat c, x or 9 with leading zero groups as invalid; not enforced. `Signature` is get-only and keeps the decoded value after `Encode(Key)`. Reported by the bolt11 batch.
- **Fix sketch:** Reject non-minimal lengths on decode; refresh the signature on encode.
- **Blocks/Blocked-by:** Related NL-122
- **Plan ref:** —

### NL-245 Our invoices carry no route hints
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Invoices/InvoiceService.cs`
- **Evidence:** `InvoiceService` writes no `r` fields, so a payer that is not our direct peer cannot reach us over private channels. Not needed for ABCD variant (c), where Alice pays Bob directly (reported by W1-B).
- **Fix sketch:** Add hints from our peers' stored `channel_update` policies (W1-E `ChannelUpdateService`) for private channels.
- **Blocks/Blocked-by:** Related NL-114, NL-099
- **Plan ref:** ONION M4-T6

---

## Persistence

### NL-125 HTLCs don't reload: byte.Equals(enum) is always false
- **Status:** fixed (fd41d73, dd4a969)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:201,204,210,213,223`, `HtlcDbRepository.cs:63,70`
- **Evidence:** Offered/Fulfilled HTLCs are never restored; Expired/Failed all land in the remote lists. In-flight HTLCs are lost on restart.
- **Fix sketch:** Compare `== (byte)HtlcState.X` / `(byte)HtlcDirection.X`; add a Sqlite round-trip test.
- **Blocks/Blocked-by:** Blocks NL-031, NL-035
- **Plan ref:** ONION_ROUTING_PLAN §7; BOLT2 N1-T5

### NL-126 Remote funding pubkey replaced by the local one on reload
- **Status:** fixed (1e8c803)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:230-231`
- **Evidence:** `new FundingOutputInfo(..., localKeySet.FundingCompactPubKey, localKeySet.FundingCompactPubKey)`; after restart the funding script is wrong, so signatures and close fail.
- **Fix sketch:** Use `remoteKeySet.FundingCompactPubKey`; test.
- **Blocks/Blocked-by:** Blocks NL-035
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N1-T5

### NL-127 CommitmentNumber rebuilt with (local, remote) basepoints regardless of opener
- **Status:** fixed (1e8c803)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:237-239`
- **Evidence:** Obscuring factor is SHA256(opener || accepter); `ChannelFactory.cs:123` passes the remote (opener) basepoint first for non-initiator channels, but reload always passes local first → wrong obscured commitment numbers after restart.
- **Fix sketch:** Order by `IsInitiator`; test both roles.
- **Blocks/Blocked-by:** Blocks NL-035
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N1-T5

### NL-128 HtlcDbRepository never writes HTLC Signature
- **Status:** fixed (fd41d73)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/HtlcDbRepository.cs` (MapDomainToEntity ~81)
- **Evidence:** `Signature` is read (L102-104) but never set, so it's always null after reload.
- **Fix sketch:** Map it in `MapDomainToEntity`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT_COVERAGE roadmap step 3; BOLT2 N1-T5

### NL-129 SQL Server maps RemoteNodeId as varbinary(32) for a 33-byte key
- **Status:** fixed (01af924, d08db67)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/EntityConfiguration/Channel/ChannelEntityConfiguration.cs:83`
- **Evidence:** Uses `TransactionConstants.TxIdLength`; inserts truncate/fail on SQL Server. Migration `FixRemoteNodeIdLength` was generated offline; its later Designer was corrected in NL-208.
- **Fix sketch:** `CryptoConstants.CompactPubkeyLen` + SqlServer migration.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N1-T5

### NL-130 UtxoDbRepository.GetByIdAsync uses an anonymous-object key; mapper drops fields
- **Status:** fixed (069e273)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Bitcoin/UtxoDbRepository.cs:50`
- **Evidence:** `new { txId, index }` makes `PrimaryKeyHelper` throw; `LockedToChannelId`/`UsedInTransactionId` are not mapped.
- **Fix sketch:** Pass `(txId, index)` ValueTuple; map both fields.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-131 ChannelModel.ChangeAddress is never mapped
- **Status:** fixed (df8b1c9)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`
- **Evidence:** Lost on reload.
- **Fix sketch:** Map it (and configure the relationship, see NL-134).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-132 BaseDbRepository.Get pages before ordering
- **Status:** fixed (3ae8fa8)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/BaseDbRepository.cs:35-38`
- **Evidence:** Skip/Take applied before orderBy, so pages are unordered.
- **Fix sketch:** Order first.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-133 UnitOfWork.AddUtxo/TrySpendUtxo can desync memory and DB
- **Status:** fixed (eff727f)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/UnitOfWork.cs`
- **Evidence:** Memory is rolled back only on immediate exceptions (swallowed); a SaveChanges failure leaves them out of sync. Memory changes apply only after a successful save; a deposit and its spend in one unit of work are handled.
- **Fix sketch:** Apply memory changes after a successful save.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-134 Convention-based shadow FKs in the schema
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Persistence/EntityConfiguration/`
- **Evidence:** `PeerEntityNodeId`, `ChangeAddressIsChange`, `ChangeAddressAddressType` were created by convention.
- **Fix sketch:** Configure relationships explicitly (schema change in all 3 providers).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-135 Postgres always enables EnableSensitiveDataLogging
- **Status:** fixed (9e4aee8)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/DependencyInjection.cs:57,67`
- **Evidence:** Parameter values (keys, signatures, preimages later) can reach logs regardless of config. The design-time `NLightningContextFactory` still enables it for Postgres (dotnet ef only).
- **Fix sketch:** Gate on a config flag, default off.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-136 Shachain / per-commitment secrets are not persisted
- **Status:** fixed (e3145a5, e7ca51e, 9dfafda, a604dff)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Services/SecretStorageService.cs`, `src/NLightning.Infrastructure.Persistence/Entities/`
- **Evidence:** Received per-commitment secrets live in memory only; after restart revoked states can't be punished and reestablish can't prove state. Fixed: `RemoteShachainEntity` + `RemoteShachainDbRepository` (migration `AddRemoteShachain`, 3 providers; save idempotent within one unit of work), `SecretStorageService.Export/Load`, and the shachain is the only store of peer secrets (`LastRevealedPerCommitmentSecret` is obsolete, NL-238). Nothing saves or loads it at runtime yet: the revoke_and_ack handler (N6, NL-031) must call `IPerCommitmentSecretVerifier.VerifyAndStore` + `Export` + `SaveAsync` in one transition, and startup must `Load` it. Update (ABCD wave 0, `0b7e617`): `IChannelStateDbRepository.ApplyAsync` now saves `ChannelStateExtras.RemoteShachain` in the same save as the transition and `LoadAsync` returns it (4472a8b); the Application call sites (`Export` on RAA, `Load` at startup) are still ABCD W1-A/W2-A. Update (ABCD wave 1, `342d22e`): the runtime call sites exist: the revoke_and_ack handler loads the peer shachain from `IRemoteShachainDbRepository` per use (`ChannelStateTransitionService.LoadRemoteShachainAsync`), inserts the secret (B2-RAA-R02 failure → warning + close) and saves `Export()` as `ChannelStateExtras.RemoteShachain` in the same save as the transition; `ISecretStorageServiceFactory` is now registered (Application `SecretStorageServiceFactory`) (a604dff).
- **Fix sketch:** Table + repo for shachain; load in `SecretStorageService` (NL-066).
- **Blocks/Blocked-by:** Blocks NL-035, NL-094
- **Plan ref:** BOLT_COVERAGE roadmap step 6; BOLT2 N3-T4

### NL-137 [EPIC] Payment/forwarding persistence: shared secrets, circuits, invoices, attempts, replay set, SCID map
- **Status:** open (partial: 4472a8b, 2ede2ee, 899e36b, 4ae2eb3)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/HtlcEntity.cs` (+ new entities)
- **Evidence:** No tables for per-HTLC onion shared secret, forwarding circuit, invoices/preimages, payment attempts, replay entries, SCID/alias→channel, or the channel graph. Update (ABCD wave 0, `0b7e617`): `HtlcEntity.OnionSharedSecret` with `IChannelStateDbRepository.Set/GetOnionSharedSecretAsync` (4472a8b); Domain models and repository ports for invoices, payments and forward circuits (2ede2ee, 1390027). Remaining: the tables and repositories (ABCD W1-C), replay set, SCID map. Update (ABCD wave 1, `342d22e`): migration `AddInvoicesPaymentsAndCircuits` (all three providers, no data step): `Invoices`, `Payments` + `PaymentHops` (route with each hop's Sphinx shared secret), `ForwardCircuits` (PK incoming channel/HTLC id, indexes on status and outgoing HTLC), `Htlcs.Origin*` (the `HtlcOrigin` of offered HTLCs, indexed for startup replay) and `Channels.MaxDustHtlcExposureMsat`; `InvoiceDbRepository`/`PaymentDbRepository`/`ForwardCircuitDbRepository` hang off `IUnitOfWork`, and `IChannelStateDbRepository.Set/Get/FindHtlcOrigin` (899e36b); the repositories are resolvable from the scope (4ae2eb3). Container round trips on Postgres and SQL Server migrate seeded pre-migration rows. Remaining: a persistent replay set (NL-078), an SCID/alias → ChannelId map, stored failure reasons for forwards, the graph; `OfferHtlcAsync` does not call `SetHtlcOriginAsync` yet (NL-250).
- **Fix sketch:** Entities + 3 migrations each (§4.5 of the onion plan); the shared secret can alternatively be recomputed from `AddMessageBytes`.
- **Blocks/Blocked-by:** Blocks NL-073, NL-114, NL-078
- **Plan ref:** ONION M4-T7; ONION_ROUTING_PLAN §4.5; BOLT2 N5-T1, N8 (partial)

### NL-138 ChannelMemoryRepository.TryGetChannel returns the shared mutable model
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Memory/ChannelMemoryRepository.cs`
- **Evidence:** Callers must remember `UpdateChannel` for `OnChannelUpdated` to fire.
- **Fix sketch:** Return copies or make updates go through the repo.
- **Blocks/Blocked-by:** Related NL-033
- **Plan ref:** BOLT2 N5-T2

### NL-191 Channel balances are persisted as whole satoshis
- **Status:** fixed (2d1fca6, a30fc57)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelEntity.cs:96,101`, `EntityConfiguration/Channel/ChannelEntityConfiguration.cs:79-80`, `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:156-157`
- **Evidence:** `LocalBalanceSatoshis`/`RemoteBalanceSatoshis` (`decimal`, SqlServer `bigint`) are written from `LightningMoney.Satoshi`; the msat part is lost on every restart, so our commitment disagrees with the peer's and signatures fail. Fixed: `long` `LocalBalanceMsat`/`RemoteBalanceMsat` columns with a ×1000 data step (3 providers); `ChannelRoundTripTests` checks a 1 msat remainder.
- **Fix sketch:** `LocalBalanceMsat`/`RemoteBalanceMsat` columns with a `sats × 1000` data step (3 migrations).
- **Blocks/Blocked-by:** Blocks NL-031, NL-035
- **Plan ref:** BOLT2 N1-T5

### NL-192 Channel UpdateAsync pushes the HTLC child graph through DbSet.Update
- **Status:** fixed (a8a8578)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs:39-43`, `src/NLightning.Infrastructure.Repositories/Database/BaseDbRepository.cs:103-133`
- **Evidence:** `UpdateAsync` maps the whole channel (with `Htlcs`) and calls `Update`, which falls back to `DbSet.Update(graph)`; new HTLC rows would be marked Modified and fail with a concurrency exception (inferred, not reproduced). Reproduced by the persist-channel reviewer (untracked context, new HTLC → `DbUpdateConcurrencyException`). `UpdateAsync` now syncs config/key sets/HTLCs/aliases by primary key (add/update/remove). Update (ABCD wave 0, `0b7e617`): `ChannelDbRepository.UpdateAsync` no longer writes HTLCs at all, nor the snapshot-owned scalars once a snapshot exists or is staged; HTLC, commitment and fee rows are written only by `ChannelStateDbRepository` (4472a8b, a8d1381).
- **Fix sketch:** Write HTLCs and commitment rows explicitly (upsert by PK) in a dedicated state repository; stop touching HTLCs in `UpdateAsync`.
- **Blocks/Blocked-by:** Part of NL-031
- **Plan ref:** BOLT2 N5-T2

### NL-208 SqlServer WidenWatchedTransactionIndex Designer had a stale target model
- **Status:** fixed (d08db67)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence.SqlServer/Migrations/20260925144542_WidenWatchedTransactionIndex.Designer.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 6. `RemoteNodeId` was `varbinary(32)` in the Designer (generated without `FixRemoteNodeIdLength`); the snapshot was right. A regression theory now diffs each Designer against the previous one for all 3 providers.
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-129, NL-102
- **Plan ref:** —

### NL-225 ChannelModel.ShortChannelId is not persisted
- **Status:** fixed (2d1fca6, a30fc57)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelEntity.cs`, `ChannelDbRepository.cs`
- **Evidence:** No SCID column or mapping; a reloaded channel has a default SCID until funding confirmation re-runs (flagged by the persistence follow-up, verified by grep). Fixed: nullable `Channels.ShortChannelId` (null until funding confirms), mapped both ways; round-trip tested.
- **Fix sketch:** Add a `ShortChannelId` column (3 migrations) and map it both ways.
- **Blocks/Blocked-by:** Related NL-103, NL-137
- **Plan ref:** BOLT2 N1-T5

### NL-232 The remote key set stores one per-commitment point; current and next remote points are not both persisted
- **Status:** fixed (4472a8b, a604dff)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelKeySetModel.cs` (`CurrentPerCommitmentCompactPoint`), `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelKeySetEntity.cs`
- **Evidence:** channel_ready replaces the peer's first point with its second (NL-051), so the point of the current remote commitment (0) is gone while `RemoteCommitmentNumber` is still 0; the factory now refuses to build a remote commitment with a point that belongs to another number (72a4ac6). The engine keeps `RemoteNextPerCommitmentPoint` in memory only. Plan §3.2 wants the remote current and next points both persisted (reported by the N1-T4 lane). Update (ABCD wave 0, `0b7e617`): the remote current point is stored on the remote commitment row and the next one in `Channels.RemoteNextPerCommitmentPoint`; both are restored into the engine, and the migration copies channel_ready's point for existing channels. Remaining (wiring, ABCD W1-A): `ChannelReadyMessageHandler` still overwrites the key set's first point, so the first snapshot must be created with `IChannelStateDbRepository.InitializeAsync` with both points before that happens. Update (ABCD wave 1, `342d22e`): `ChannelReadyMessageHandler` builds the first `ChannelCommitments` on the first channel_ready from the key set's current point and the message's next point, before the key set is overwritten, stages it with `InitializeAsync` in the same save and attaches it only after the save (a604dff). Channels that received channel_ready before this change have lost commitment 0's point and get no snapshot (NL-246).
- **Fix sketch:** Store remote current and next points (N5-T1 migration) and restore them into the engine.
- **Blocks/Blocked-by:** Part of NL-031; related NL-051, NL-188
- **Plan ref:** BOLT2 N5-T1

### NL-237 Postgres and SQL Server migration data steps never ran against existing rows
- **Status:** fixed (4472a8b, f2e1a4a, 79f7657)
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure.Persistence.{Postgres,SqlServer}/Migrations/*_{SplitChannelParams,StoreMsatBalancesAndShortChannelId,FlagInferredChannelParams,PersistCommitmentNumbers}.cs`
- **Evidence:** The hand-written `migrationBuilder.Sql` data steps (CAST, COALESCE, NOT EXISTS against Htlcs) are exercised with rows only on SQLite (`ChannelMigrationDataTests`); the Docker `PostgresTests`/`SqlServerTests` migrate an empty database (reported by the N1-T4/T5 lane). Update (ABCD wave 0, `0b7e617`): `CommitmentStateMigrationRoundTrip` (channels, key sets, legacy HTLCs, wallet addresses, UTXOs) and `LegacyChannelMigrationRoundTrip` (PersistCommitmentNumbers, SplitChannelParams, StoreMsat, FlagInferredChannelParams data steps) seed rows with raw SQL and migrate forward on SQLite (CI), Postgres and SQL Server (Docker, each in its own database).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT2 N1-T5, N5-T1

### NL-238 Obsolete ChannelKeySet LastRevealedPerCommitmentSecret column is still mapped
- **Status:** fixed (4472a8b, a8d1381)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelKeySetModel.cs`, `src/NLightning.Infrastructure.Persistence/Entities/Channel/ChannelKeySetEntity.cs`
- **Evidence:** Since NL-136 the shachain is the only store of the peer's secrets; `LastRevealedPerCommitmentSecret` is `[Obsolete]`, never written, but still round-trips a legacy column (9dfafda), which tests must suppress warnings for. Update (ABCD wave 0, `0b7e617`): `ChannelKeySetModel.LastRevealedPerCommitmentSecret` and its column are dropped in `AddCommitmentState`; `UpdateAsync` also keeps snapshot scalars staged by `InitializeAsync`/`ApplyAsync` in the same unit of work (a8d1381).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-136
- **Plan ref:** BOLT2 N5-T1

### NL-242 Reloaded commitment snapshots have no dust-exposure policy
- **Status:** fixed (899e36b, 98d19e6)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (`ToCommitmentParams`), `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`
- **Evidence:** `CommitmentParams.MaxDustHtlcExposureMsat` is not stored with the channel; `ChannelDbRepository` restores snapshots with null, so the dust-exposure check is off after every restart (latent: no config sets the policy yet; reported by the W0-B lane). Update (ABCD wave 1, `342d22e`): `ChannelStateDbRepository` stores `next.Params.MaxDustHtlcExposureMsat` in `Channels.MaxDustHtlcExposureMsat`, and `ChannelDbRepository` reloads with `CommitmentParams.FromChannel(model, stored value)`, which also keeps `HasInferredLimits` (899e36b); `ChannelModel.ToCommitmentParams` was removed (98d19e6). A reload now enforces the policy it was saved with; no node option sets one yet (NL-254).
- **Fix sketch:** Pass the node policy when loading (`IChannelStateDbRepository.LoadAsync` with node-policy params) or rebuild the params after load.
- **Blocks/Blocked-by:** Related NL-031
- **Plan ref:** BOLT2 N9-T3; ABCD W1-A/W2-A

### NL-243 Settled HTLC rows are never pruned and every channel load reads them all
- **Status:** open (partial: 899e36b, a02afa7)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs` (`PruneSettledHtlcsAsync`), `ChannelDbRepository.GetByIdAsync`
- **Evidence:** Settled HTLCs stay as an archive with their final state so events can be re-derived after a crash (I8), but nothing calls `PruneSettledHtlcsAsync`; rows grow without limit and `GetByIdAsync` (often used as an existence check) loads them all (reported by the W0-B lane). Update (ABCD wave 1, `342d22e`): `IChannelDbRepository.ExistsAsync` is a cheap existence check (899e36b); `LocalOnlyHtlcSwitch` prunes a settled outgoing HTLC's archived row on `OutgoingHtlcSettled` in one save (a02afa7). Remaining: the W2-B `HtlcSwitch` must keep pruning, in an order that respects payments and circuits (it replaces `LocalOnlyHtlcSwitch`); existence checks still using `GetByIdAsync` should move to `ExistsAsync`.
- **Fix sketch:** Prune once the switch has consumed an HTLC's settle event (W2-B); optionally add a cheap `ExistsAsync`.
- **Blocks/Blocked-by:** Related NL-137
- **Plan ref:** ABCD W1-A/W2-B

### NL-248 ChannelModel.ToCommitmentParams dropped HasInferredParams on reload
- **Status:** fixed (98d19e6)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Models/ChannelModel.cs` (removed `ToCommitmentParams`), `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelDbRepository.cs`
- **Evidence:** The repository reload used `ToCommitmentParams`, which dropped `ChannelParams.HasInferredParams` while `CommitmentParams.FromChannel` kept it, so a reloaded snapshot of an NL-194 migrated channel would enforce guessed limits (reported by W1-A). Fixed: reloads use `CommitmentParams.FromChannel(model, stored dust policy)` (899e36b) and `ToCommitmentParams` was removed (98d19e6).
- **Fix sketch:** Use `CommitmentParams.FromChannel` everywhere.
- **Blocks/Blocked-by:** Related NL-194, NL-242
- **Plan ref:** BOLT2 N5-T2

---

## Daemon / IPC / Client

### NL-139 `--cookie <path>` / `-c <path>` stores the flag itself as the path
- **Status:** fixed (55a8442)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon.Contracts/Helpers/CommandLineHelper.cs:88-95`
- **Evidence:** `cookiePath = args[i];` instead of `args[i + 1]`; only `--cookie=<path>` works.
- **Fix sketch:** Use the next arg; unit test.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-140 GetCommand skips the next arg after any option, including `--network=x`
- **Status:** fixed (55a8442, 4348947)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon.Contracts/Helpers/CommandLineHelper.cs:26-38`
- **Evidence:** `--network=regtest listpeers` silently runs `node-info`.
- **Fix sketch:** Only skip when the option takes a separate value.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-141 Minor CLI parsing gaps: `-?` unrecognized, NLTG_COOKIE only when NLTG_NETWORK unset
- **Status:** fixed (55a8442)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon.Contracts/Helpers/CommandLineHelper.cs`
- **Evidence:** See `src/NLightning.Client/CLAUDE.md` gotchas.
- **Fix sketch:** Recognize `-?`; read NLTG_COOKIE independently.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-142 Client indexes commandArgs without length checks; GetCookiePath runs before --help
- **Status:** fixed (994610e, 4348947)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Client/Program.cs`, `src/NLightning.Client/Handlers/OpenChannelMessageHandler.cs`
- **Evidence:** `connect` with no args prints an error then indexes `[0]`; `getaddress`/`openchannel` unchecked; missing `~/.nltg/<network>` makes even `--help` throw. `open-channel` blocks with `GetAwaiter().GetResult()`.
- **Fix sketch:** Validate lengths and return; resolve paths after the help check inside the try; await.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-143 GetAddressIpcResponse.AddressP2Wsh actually holds a P2WPKH address
- **Status:** fixed (8f42884)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Transport.Ipc/Responses/GetAddressIpcResponse.cs`, `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`
- **Evidence:** Printer labels it P2WSH; client defaults to P2Tr while the request DTO defaults to P2Wpkh.
- **Fix sketch:** Rename (append-only new key) and align defaults.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-144 OpenChannel IPC handlers put the exception message in the error code
- **Status:** fixed (e57eecd)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Ipc/Handlers/OpenChannelIpcHandler.cs:66`, `OpenChannelSubscriptionIpcHandler.cs:71`
- **Evidence:** `CreateErrorEnvelope(envelope, ce.Message, ce.Message)`.
- **Fix sketch:** Use `ce.ErrorCode`.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-145 SignedTransactionFormatter write/read mismatch and null dereference
- **Status:** fixed (232ccac)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Transport.Ipc/MessagePack/Formatters/SignedTransactionFormatter.cs:13-14`
- **Evidence:** Writes TxId with a bin header, reads it raw via `TxIdFormatter`; `value.TxId` dereferenced without null check (CS8602). No DTO uses it yet.
- **Fix sketch:** Use the same formatter both ways; handle null.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-146 Hash/TxId MessagePack formatters write raw bytes without a bin header
- **Status:** fixed (232ccac)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Transport.Ipc/MessagePack/Formatters/{HashFormatter,TxIdFormatter}.cs`
- **Evidence:** Output isn't valid MessagePack for non-.NET readers; default values write 0 bytes. Wire break between builds accepted as NL-210.
- **Fix sketch:** Write `bin 32` (IPC wire change: needs versioning).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-147 Daemon config args: `-n`/`-c` unmapped, bare flags eat the next arg, default network mismatch
- **Status:** fixed (a4ce86f, 45c1e74)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs`, `src/NLightning.Daemon/Utilities/DaemonUtils.cs`
- **Evidence:** `AddCommandLine(args)` has no switch mappings; `--daemon --network regtest` loses the network; without `--config` the default dir is mainnet while the template says regtest. A config whose Node:Network differs from its directory now refuses to start.
- **Fix sketch:** Add switch mappings; normalize flags; make defaults agree.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-148 `--password` visible in the process list; IPC cookie never rotated
- **Status:** fixed (76b67cf, d91b1ad, 45c1e74)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Program.cs`, `src/NLightning.Daemon/Services/Ipc/CookieFileAuthenticator.cs`
- **Evidence:** Wallet password on the command line leaks via `ps`; the cookie at `{configPath}/nltg.cookie` is static.
- **Fix sketch:** Read the password from stdin/file/env; rotate the cookie per start.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-149 NamedPipeIpcService.StopAsync throws if never started; Linux fork() after runtime start
- **Status:** fixed (f133f04, d91b1ad)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Services/Ipc/NamedPipeIpcService.cs`, `src/NLightning.Daemon/Utilities/DaemonUtils.cs`
- **Evidence:** See `src/NLightning.Daemon/CLAUDE.md` gotchas.
- **Fix sketch:** Null-guard StopAsync; daemonize via re-exec rather than fork.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-150 OpenChannel*IpcHandler resolves its handler with `as ConcreteType`
- **Status:** fixed (e57eecd)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Ipc/Handlers/OpenChannelIpcHandler.cs`, `OpenChannelSubscriptionIpcHandler.cs`
- **Evidence:** A decorator or substitute registration makes the cast return null.
- **Fix sketch:** Resolve the interface.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-151 Dead or unwired code (plugin loader and friends)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Services/PluginLoaderService.cs`, `Models/PluginEntry.cs`, `Helpers/AesGcmHelper.cs`, `Models/FeeRateCacheData.cs`, `src/NLightning.Daemon.Contracts/IControlClient.cs`, `src/NLightning.Domain/Node/Interfaces/IPeerFactory.cs`, `ISecretStorageServiceFactory.cs`, `IChannelKeySetFactory.cs`, `ISignatureValidator.cs`, `src/NLightning.Infrastructure.Bitcoin/Adapters/OutputAdapters/*`, `src/NLightning.Domain/Protocol/Enums/HtlcType.cs`
- **Evidence:** Never registered/called; `IDaemonContext` has no implementation.
- **Fix sketch:** Wire the plugin loader or delete; delete the rest.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-152 Missing IPC commands: close, list channels, invoice, pay, disconnect
- **Status:** open (partial: 5611156, 2ede2ee, 6cfbcd1, c10a78e, c50fc7b)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Client/Enums/ClientCommand.cs`
- **Evidence:** Only NodeInfo, ConnectPeer, ListPeers, GetAddress, WalletBalance, OpenChannel(+Subscription). A user can't close a channel or pay. Update: `ClientCommand.ListChannels = 8` with the `listchannels [peer_id]` CLI (5611156; local/remote commitment numbers since 3c625e1). Close, invoice, pay and disconnect remain. Update (ABCD wave 0, `0b7e617`): Domain side of invoice/payment IPC: `ClientCommand` CreateInvoice=9, PayInvoice=10, ListInvoices=11, ListPayments=12 (next free 13), their request/response DTOs, and `ChannelInfoClientResponse.IsReestablished`/`FeeBaseMsat`/`FeePpm` (2ede2ee, 1390027). Handlers, IPC registration and CLI remain (ABCD W1-D); close and disconnect remain. Update (ABCD wave 1, `342d22e`): CreateInvoice (9), PayInvoice (10), ListInvoices (11) and ListPayments (12) have MessagePack DTOs, daemon IPC handlers on a shared `ClientCommandIpcHandler` base ("not available" while a payment service is unregistered), scoped client handlers and CLI commands with snapshot-tested printers; `RoutingOptions` is bound and the default config writes `Node:EnableHtlcs` (true on regtest) and the `Node:Routing` defaults; hardening after review: exact error mapping, pipe cap 64, payinvoice wait ≤ 300 s, list count ≤ 1000 (6cfbcd1, c10a78e, c50fc7b). Remaining: close and disconnect commands; PayInvoice/ListPayments need `IPaymentService` (W2-C).
- **Fix sketch:** Append commands per the recipe as each epic lands.
- **Blocks/Blocked-by:** Blocked-by NL-034, NL-114
- **Plan ref:** ONION M4-T6; BOLT2 N0-T8, N8-T2, N8-T3, N10-T3

### NL-153 BitcoinChainService ctor makes a blocking RPC call; key creation needs bitcoind
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinChainService.cs`
- **Evidence:** DI resolution fails when bitcoind is down.
- **Fix sketch:** Lazy/async init with retry.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-154 IPC framing implemented twice with a native-endian length prefix
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Services/Ipc/IpcFraming.cs`, `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`
- **Evidence:** Two copies must change together; host-endian prefix.
- **Fix sketch:** Share one implementation in Transport.Ipc; use big-endian.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-155 Daemon.Contracts and Daemon.Plugins target net9.0 with older packages
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon.Contracts/*.csproj`, `src/NLightning.Daemon.Plugins/*.csproj`
- **Evidence:** Pulls MessagePack 3.1.3 (vulnerable, see NL-170).
- **Fix sketch:** Decide on net10.0 or multi-target; bump packages.
- **Blocks/Blocked-by:** Related NL-170
- **Plan ref:** —

### NL-156 DI graph hand-maintained in NodeServiceExtensions and duplicated in Docker tests
- **Status:** fixed (fe4a551)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`, `test/NLightning.Integration.Tests/Docker/{AbcNetworkTests,ChannelOpeningFlowTests}.cs`
- **Evidence:** Layer services (`ChannelFactory`, validators, tx factories, signer) registered only in the daemon; Docker tests rebuild them by hand. Fixed: layer registrations live in `AddApplicationServices`/`AddBitcoinInfrastructure`; `AddNltgNodeServices` is the whole node graph, used by the daemon and by the Docker `NLightningTestNode`.
- **Fix sketch:** Move layer-owned registrations into each `DependencyInjection.cs`; share a test helper.
- **Blocks/Blocked-by:** —
- **Plan ref:** ONION_ROUTING_PLAN §9 risk 8; BOLT2 N0-T8

### NL-157 Application references Infrastructure; PeerService lives in Infrastructure
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/NLightning.Application.csproj`, `src/NLightning.Infrastructure/Node/Services/PeerService.cs:17`
- **Evidence:** Handlers use `IBlockchainMonitor`, tx builders and `ITcpService` directly; `TODO: Eventually move this to the Application layer`.
- **Fix sketch:** Move ports to Domain incrementally; don't add new references.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-210 IPC wire format changed: old clients and new daemons cannot talk
- **Status:** wontfix
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Transport.Ipc/MessagePack/Formatters/{HashFormatter,TxIdFormatter}.cs`, MessagePack package version
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 8. Accepted break: Hash/TxId now write MessagePack `bin` (NL-146) and MessagePack went 3.1.3/3.1.4 → 3.1.10. Client and daemon from one build match. Documented in the Transport.Ipc CLAUDE.md.
- **Fix sketch:** None (accepted). Version the IPC envelope before the next wire change.
- **Blocks/Blocked-by:** Related NL-146, NL-154
- **Plan ref:** —

### NL-241 listchannels counts pending HTLCs from legacy collections that are empty after reload
- **Status:** fixed (e30a845)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Handlers/ListChannelsClientHandler.cs:81-82`
- **Evidence:** `OfferedHtlcCount`/`ReceivedHtlcCount` read `ChannelModel.LocalOfferedHtlcs`/`RemoteOfferedHtlcs`, which are no longer persisted since the commitment snapshot (4472a8b) and are always empty after a reload; the ABCD test asserts zero pending HTLCs through this handler (reported by the W0-B lane). Update (ABCD wave 1, `342d22e`): `ListChannelsClientHandler` counts the snapshot's non-final HTLCs per direction (legacy lists only for a channel without a snapshot) and also reports the fee policy, `IsReestablished` (false until N7) and `DataLossDetected` from the model.
- **Fix sketch:** Count from `ChannelModel.Commitments.Htlcs` (non-final states per direction).
- **Blocks/Blocked-by:** Related NL-152
- **Plan ref:** ABCD W1-D

---

## Crypto providers and key management

### NL-158 Key file encryption: fixed Argon2 salt, all-zero XChaCha nonce, 64 KiB Argon2 memory
- **Status:** fixed (953a33b, b999208)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs:27-31,176-177`, `src/NLightning.Infrastructure/Crypto/Hashes/Argon2Id.cs:9`
- **Evidence:** Static `s_salt`, a never-filled stackalloc nonce, and `DeriveKeyMemLimit = 1 << 16` (comment says 64 MiB; it's 64 KiB) weaken offline password attacks on the node key file. v2 key file (random salt/nonce, full UTF-8 password); v1 upgraded in place with a `.v1.bak`. Follow-ups: NL-211 (accepted break), NL-212 (Windows legacy fallback), NL-224 (owner/ACL).
- **Fix sketch:** Random per-file salt and nonce stored in the file, Argon2 memory ≥ 64 MiB, versioned key-file format with migration (changing any of these breaks existing files).
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-159 Node key is the BIP32 master key; non-standard master derivation from mnemonic
- **Status:** open
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs:85,141-143`
- **Evidence:** `GetNodeKeyPair` returns the master key itself; the master is not derived the standard BIP32 way, so the seed can't be restored in other wallets.
- **Fix sketch:** Derive the node key on a dedicated path; document/migrate.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-160 JS (WASM) RandomBytes swallows errors and leaves the buffer unfilled
- **Status:** fixed (d10bf55)
- **Severity:** critical
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/SodiumJsCryptoProvider.cs:212-222`
- **Evidence:** `catch (Exception e) { Console.WriteLine(e); }`; a failed RNG call yields predictable (zero) key material. Other JS methods also only log exceptions.
- **Fix sketch:** Throw on failure; audit all JS provider catch blocks.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-161 Argon2 not implemented in the JS (WASM) provider
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/SodiumJsCryptoProvider.cs:207-210`
- **Evidence:** `throw new NotImplementedException();`
- **Fix sketch:** Bind libsodium.js `crypto_pwhash`.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-162 JS ChaCha20 keystream never executed; blazorSodium.js export unverified
- **Status:** open
- **Severity:** medium
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/{LibsodiumJsWrapper,SodiumJsCryptoProvider}.cs`, `test/BlazorTests/`
- **Evidence:** Compiled in CI `Release.Wasm` only; no Blazor/Playwright test; unverified whether `blazorSodium.js` needs to re-export `crypto_stream_chacha20_ietf_xor`.
- **Fix sketch:** Add a BlazorTestApp page + Playwright test with the RFC 8439 vector.
- **Blocks/Blocked-by:** Blocked-by NL-169 for local runs
- **Plan ref:** M1-T2 open items

### NL-163 Native provider mlock/munlock are no-ops off Windows
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/Native/NativeCryptoProvider.cs:92-106`
- **Evidence:** TODOs; secrets can be swapped to disk on Linux/macOS under the Native/AOT build.
- **Fix sketch:** P/Invoke `mlock`/`munlock` on Unix.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-164 Hkdf validates key/output lengths with Debug.Assert only
- **Status:** fixed (2f19776)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Crypto/Functions/Hkdf.cs:32-33,55-56,75-76`
- **Evidence:** Release builds skip the checks; current callers pass 32-byte keys.
- **Fix sketch:** Throw `ArgumentException`.
- **Blocks/Blocked-by:** —
- **Plan ref:** M1-T1 open item

### NL-165 Crypto value-object equality/validation hazards
- **Status:** fixed (1df7537)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Crypto/ValueObjects/*`, `src/NLightning.Domain/Bitcoin/ValueObjects/TxId.cs`, `src/NLightning.Domain/Protocol/Tlv/BaseTlv.cs`, `ChainHash.cs`
- **Evidence:** `PrivKey`/`CompactSignature` compare by reference; `default(TxId/Hash/Secret)` throws in `GetHashCode`; `Hash`/`Secret`/`TxId` accept arrays longer than 32 bytes; `BaseTlv`/`ChainHash` hash codes are inconsistent with `Equals`.
- **Fix sketch:** Content equality, exact-length validation, null-safe hashing.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-166 Implicit byte[]→CompactPubKey conversion turns a bare null into a NullReferenceException
- **Status:** fixed (938655c, 8371531)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Crypto/ValueObjects/CompactPubKey.cs`
- **Evidence:** `cond ? x : null` binds to the implicit operator and throws; tests cast `(CompactPubKey?)null` to avoid it. Binary break recorded in the Domain CHANGELOG.
- **Fix sketch:** Make the conversion explicit or null-tolerant.
- **Blocks/Blocked-by:** —
- **Plan ref:** M2-sphinx open item

### NL-211 Key files upgraded to v2 cannot be read by older builds
- **Status:** wontfix
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs`
- **Evidence:** Cross-batch review of the swarm integration (2026-09-25), item 8. Accepted break: v1 files still load (fixed salt, 64 KiB, truncated-UTF-8 fallback) and are rewritten as v2 with a `<file>.v1.bak` copy and a stderr notice; older builds cannot open v2. Recorded in the Infrastructure and Infrastructure.Bitcoin CHANGELOGs.
- **Fix sketch:** None (accepted). Keep the `.v1.bak` for downgrade.
- **Blocks/Blocked-by:** Related NL-158
- **Plan ref:** —

### NL-212 Legacy key-file password fallback misses Windows ANSI-codepage files
- **Status:** open
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs` (legacy retry), libsodium P/Invoke
- **Evidence:** The old libsodium path marshalled the password as LPStr: UTF-8 on Unix (covered by the truncated-UTF-8 retry) but the ANSI codepage on Windows. A non-ASCII password on a Windows v1 file will not decrypt.
- **Fix sketch:** On Windows also retry with `Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)` truncated to the char count; test with a known-answer vector.
- **Blocks/Blocked-by:** Related NL-158, NL-211
- **Plan ref:** —

### NL-224 Atomic key-file writes keep the mode but not owner, group or Windows ACL
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs` (atomic write)
- **Evidence:** The temp file copies the Unix mode before the move; no chown and no ACL copy (crypto-security batch).
- **Fix sketch:** Copy owner/group where permitted; copy the ACL on Windows.
- **Blocks/Blocked-by:** Related NL-158
- **Plan ref:** —

### NL-247 ISha256 is a stateful singleton shared by concurrent users
- **Status:** open
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/DependencyInjection.cs:29` (`AddSingleton<ISha256, Sha256>()`), consumers such as `ChannelFactory` and the repositories
- **Evidence:** `Sha256` keeps state between `AppendData` and `GetHashAndReset`, so two concurrent users of the singleton can interleave and get wrong hashes. The update_fulfill_htlc handler now hashes preimages with its own instance (1392489); other users still share it (reported by W1-A).
- **Fix sketch:** Register it transient (or through a factory) and audit singleton consumers that hold it.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

---

## Tests / CI / Build

### NL-167 Application.Tests and Daemon.Tests lack xunit.runner.visualstudio (47 tests skipped in CI)
- **Status:** fixed (34d431d, 9e03b7e, df708a7)
- **Severity:** high
- **Kind:** test
- **Location:** `test/NLightning.Application.Tests/NLightning.Application.Tests.csproj`, `test/NLightning.Daemon.Tests/NLightning.Daemon.Tests.csproj`
- **Evidence:** `dotnet test` discovers 0 tests; CI silently skips 24 + 23 tests. They pass via `dotnet run`. `scripts/check-sln-configs.py` (CI step) now fails if a test project lacks the runner.
- **Fix sketch:** Add `xunit.runner.visualstudio` 3.1.5 (and `IsTestProject` for Daemon.Tests).
- **Blocks/Blocked-by:** Blocks NL-073 (M4-T1 tests go here)
- **Plan ref:** ONION M4-T1; BOLT_COVERAGE roadmap step 1; BOLT2 N0-T1

### NL-168 PeerAddressTests HttpAddress test depends on live DNS
- **Status:** fixed (4c5207d, df708a7)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Tests/Protocol/Models/PeerAddressTests.cs` (`Given_HttpAddress_When_ConstructingPeerAddress_...`)
- **Evidence:** Resolves `dnstest.nlightn.ing`; fails offline.
- **Fix sketch:** Inject a resolver or move to an integration category.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-169 Release.Wasm build fails on macOS (linux-x64 pinned npm deps)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure/Crypto/Providers/JS/package.json`
- **Evidence:** esbuild/rollup pinned to linux-x64 → `EBADPLATFORM`; Wasm changes can only be verified in CI.
- **Fix sketch:** Use optional platform deps or unpin.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-170 ~570 NuGet vulnerability warnings (NU1902/NU1903)
- **Status:** fixed (525dc43, df708a7)
- **Severity:** high
- **Kind:** tech-debt
- **Location:** package references across `src/` and `test/`
- **Evidence:** Known high/moderate advisories: System.Security.Cryptography.Xml 8.0.2, MessagePack 3.1.4 and 3.1.3 (IPC deserialization surface), SharpCompress 0.41.0, SQLitePCLRaw.lib.e_sqlite3 2.1.11. They bury real warnings.
- **Fix sketch:** Bump or pin patched versions (transitives via `Directory.Packages`/explicit refs); then consider `WarningsAsErrors` for NU190x.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-171 ~20 nullability warnings (10 unique sites)
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `FundingConfirmedMessageHandler.cs:52`, `FundingCreatedMessageHandler.cs:74`, `ChannelManager.cs:245,320`, `OpenChannelClientSubscriptionHandler.cs:120`, `ChannelDbRepository.cs:121,147`, `PeerService.cs:65,256`, `SignedTransactionFormatter.cs:14`
- **Evidence:** CS8602/CS8604/CS8622 in a Release build. Update: 8 CS86xx warnings remain after the swarm (FundingConfirmedMessageHandler, FundingCreatedMessageHandler, ChannelManager, OpenChannelClientSubscriptionHandler, ChannelDbRepository, PeerService); `SignedTransactionFormatter` is fixed (NL-145). Update (ABCD wave 0, `0b7e617`): 7 CS86xx warnings in Release and Release.Native: `FundingCreatedMessageHandler.cs:75`, `ChannelManager.cs:549`, `OpenChannelClientSubscriptionHandler.cs:120`, `ChannelDbRepository.cs:302,320`, `PeerService.cs:127,413`. Update (ABCD wave 1, `342d22e`): 5 CS86xx warning sites in Release and Release.Native (each reported twice): `FundingCreatedMessageHandler.cs:75`, `ChannelManager.cs:840`, `OpenChannelClientSubscriptionHandler.cs:120`, `ChannelDbRepository.cs:310,328`; the two `PeerService` warnings went away with the W1-E connect rewrite (df4ca92).
- **Fix sketch:** Fix each, then enable nullable warnings as errors.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-172 Solution configuration mappings are hand-maintained and partly wrong
- **Status:** fixed (33cc93c, 9e03b7e, df708a7)
- **Severity:** medium
- **Kind:** tech-debt
- **Location:** `NLightning.sln` (~L283 and newer projects)
- **Evidence:** Serialization maps Release.Native→Release.Wasm; Repositories, Contracts, Client, Plugins, Transport.Ipc, Application.Tests, Daemon.Tests map every custom config to Debug, so Native/Wasm builds silently use the wrong backend for them.
- **Fix sketch:** Fix the mappings; consider `.slnx` or a check script.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-173 test/NLightning.Node.Tests is an empty orphan
- **Status:** fixed (71e3e57, df708a7)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `test/NLightning.Node.Tests/NLightning.Node.Tests.csproj`
- **Evidence:** 3-byte BOM-only csproj, not in the sln.
- **Fix sketch:** Delete it.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-174 scripts/testwithcoverage.sh and .vscode/launch.json are stale
- **Status:** fixed (edf7b77, 8d18d27, df708a7)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `scripts/testwithcoverage.sh`, `.vscode/launch.json:12`
- **Evidence:** Script lists nonexistent projects and exits on the first; launch.json points at `src/NLightning.NLTG/bin/Debug/net8.0/NLightning.NLTG.dll`. Shared Persistence BaseOutputPath split out as NL-223.
- **Fix sketch:** Update or delete.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-175 No serializer tests for open_channel, accept_channel, funding_created, funding_signed
- **Status:** fixed (3c335a6, 3c4af42)
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Serialization.Tests/Messages/`
- **Evidence:** Only ChannelReady of the v1-open messages has round-trip tests.
- **Fix sketch:** Add round-trip tests, ideally with LND-captured bytes.
- **Blocks/Blocked-by:** —
- **Plan ref:** BOLT_COVERAGE roadmap step 1

### NL-176 BOLT 3 vector coverage gaps
- **Status:** fixed (d69eea3, 82e08d2, 4f9f550, a0dfebd, b222896)
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/BOLT3/Bolt3IntegrationTests.cs:64-`, `test/NLightning.Tests.Utils/Vectors/Bolt3AppendixCVectors.cs`, `Bolt3AppendixFVectors.cs`
- **Evidence:** Appendix B funding test body is commented out (passes vacuously); Appendix F (anchors) unused; `ExpectedCommitTx1..15` unreferenced (only signatures asserted); no HTLC second-stage vectors. Appendix B funding tx and every Appendix C commitment tx asserted byte-for-byte; 9-vector Appendix F theory. HTLC second-stage vectors wait for NL-056 (1 skipped test). Update: Appendix C/F now load from verbatim spec copies (`BOLT3/Vectors/appendix-c.txt`, `appendix-f.json`) and every commitment is built from its msat balances through `CommitmentTxSpec` and compared as a full signed tx (a0dfebd, b222896).
- **Fix sketch:** Restore Appendix B, assert full txs, add Appendix C HTLC-tx and Appendix F tests.
- **Blocks/Blocked-by:** Related NL-061, NL-056
- **Plan ref:** BOLT_COVERAGE roadmap step 1; BOLT2 N2-T2, N2-T5

### NL-177 Fully commented-out test files
- **Status:** fixed (94a8cbf, 017050a, 71d0944)
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Infrastructure.Bitcoin.Tests/Outputs/{Base,Change,Funding,ToRemote}OutputTests.cs`, `Transactions/{Commitment,Funding}TransactionTests.cs`, `test/NLightning.Infrastructure.Tests/Node/Models/PeerTests.cs`, `test/NLightning.Integration.Tests/Docker/{Sqlite,Postgres,SqlServer}Tests.cs`, `BOLT10/DNSBootstrapTests.cs`, `test/NLightning.Infrastructure.Serialization.Tests/Tlv/TlvSerializerTests.cs` (mostly)
- **Evidence:** 0 live lines in each (sweep 2026-09-25). Revived or deleted every listed file.
- **Fix sketch:** Revive against the current API or delete.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-178 Untested components
- **Status:** open
- **Severity:** medium
- **Kind:** test
- **Location:** `test/`
- **Evidence:** No unit tests for PeerService, PeerCommunicationService, TcpService, ChannelManager, AcceptChannel1/FundingSigned/FundingConfirmed/ChannelReady handlers, MessageFactory, RemoteAddressTlvConverter (beyond IPv4), the IPC stack/formatters/client, and persistence round trips.
- **Fix sketch:** Add tests as each area is touched; a Sqlite `:memory:` round-trip is the cheapest persistence test.
- **Blocks/Blocked-by:** Related NL-179
- **Plan ref:** —

### NL-179 InternalsVisibleTo gaps
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon/AssemblyInfo.cs:3`, `src/NLightning.Domain/AssemblyInfo.cs`, `src/NLightning.Application/AssemblyInfo.cs`
- **Evidence:** Daemon lists a stale `NLightning.Bolts.Tests` and not `NLightning.Daemon.Tests`; Domain.Tests/Application.Tests have no internals access. Update: Application lists `NLightning.Application.Tests` (424ae84) and Daemon lists `NLightning.Daemon.Tests`; Daemon still lists the stale `NLightning.Bolts.Tests`, and Domain.Tests still has no internals access (the N4 engine is public instead).
- **Fix sketch:** Fix the lists.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-180 Interop coverage: Docker e2e only against LND and not in CI
- **Status:** open
- **Severity:** medium
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/`, `.github/workflows/`
- **Evidence:** The project goal requires LND/CLN/Eclair/LDK interop; only LND v0.20.0 is exercised, manually.
- **Fix sketch:** Add CLN/Eclair fixtures and a scheduled CI job with Docker.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-181 FakeSha256 returns zeros by default
- **Status:** fixed (82f2713)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Tests.Utils/Mocks/FakeSha256.cs`
- **Evidence:** Tests using it bare can pass with wrong hashes.
- **Fix sketch:** Make the default throw or delegate to the real hash.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-223 Persistence provider projects share one BaseOutputPath
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Persistence.{Postgres,Sqlite,SqlServer}/*.csproj`, `src/NLightning.Infrastructure.Persistence/scripts/*`
- **Evidence:** All three write to `Persistence/bin`, which can race in parallel builds (not reproduced in 3 runs). The EF migration scripts depend on the shared output (ci-build batch).
- **Fix sketch:** Point `dotnet ef` at each provider's own output, then split the paths.
- **Blocks/Blocked-by:** Related NL-174
- **Plan ref:** —

### NL-233 dotnet-ef loads stale Debug provider assemblies when generating migrations
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Persistence/bin/Debug`, `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh`
- **Evidence:** During the four-lane integration, `dotnet ef` loaded the provider migration assemblies from the shared `Persistence/bin/Debug`; they were stale until the three provider projects were rebuilt in Debug, and until then both the regeneration and the `HasPendingModelChanges` check gave wrong results.
- **Fix sketch:** Build the three provider projects in Debug before `migrations add`/`has-pending-model-changes` (script it), and fix the shared output path (NL-223).
- **Blocks/Blocked-by:** Related NL-223
- **Plan ref:** —

### NL-249 FakeServiceProvider throws for unregistered services instead of returning null
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Tests.Utils/Mocks/FakeServiceProvider.cs`
- **Evidence:** `GetService` throws `KeyNotFoundException` for unregistered types, which breaks the `IServiceProvider` contract; `ChannelManager` now calls `GetService<IHtlcSwitch>()` and resolves `ChannelDomainEventQueue`, so tests using the fake must register them (reported by W1-A).
- **Fix sketch:** Return null for unknown types; keep an opt-in strict mode if some tests rely on the throw.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

---

## Docs

### NL-182 REPO_MAP.md has stale pre-M1 claims
- **Status:** fixed (docs commit "update issue ledger and plans after swarm fixes")
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `docs/agents/REPO_MAP.md` §3.2, §3.4, §6.1, §10.1, §10.2
- **Evidence:** Still says ICryptoProvider has no raw ChaCha20/HMAC, TlvStreamSerializer is a closed switch, BigSize isn't canonical, update_add_htlc uses the UpfrontShutdownScript constant (all fixed; see NL-016, NL-017, NL-029, NL-085). REPO_MAP §1/§3/§4/§5/§8/§9/§11 refreshed; §10 now points at this ledger.
- **Fix sketch:** Point §10 at this ledger and trim the duplicated bug tables.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-183 Docs say blinded-onion-message vector is unused
- **Status:** fixed (docs commit "update issue ledger and plans after swarm fixes")
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `docs/agents/BOLT_COVERAGE.md` (BOLT 4 vectors row), `docs/agents/ONION_ROUTING_PLAN.md` (open follow-ups)
- **Evidence:** `OnionVectorTests.Given_BlindedOnionMessageVector_When_PeelingChain_Then_EachNextPacketMatches` uses it (M2 review). Only `route-blinding-test.json` is unused.
- **Fix sketch:** Correct both lines.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-184 test/CLAUDE.md onion section says there are no BOLT 4 tests
- **Status:** fixed (docs commit "update issue ledger and plans after swarm fixes")
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `test/CLAUDE.md:53-56`
- **Evidence:** BOLT 4 tests now exist in Integration/Domain/Infrastructure/Serialization/Bitcoin test projects.
- **Fix sketch:** Rewrite the section to list them.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-185 VERSIONING.md, CONTRIBUTING.md, Integration.Tests README and CLI help text are stale
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `VERSIONING.md`, `CONTRIBUTING.md`, `test/NLightning.Integration.Tests/README.md`, `src/NLightning.Client/Utils/ClientUtils.cs`
- **Evidence:** CONTRIBUTING says "master" (default is `main`); client help calls the binary `nltg` (it is `NLightning.Client`) and says the cookie file is `nltg.ipc` (it's `nltg.cookie`). Partial (docs commit "update issue ledger and plans after swarm fixes"): CONTRIBUTING, VERSIONING and the Integration.Tests README are current; the cookie text was already fixed. Remaining: usage texts call the binary `nltg` while the assembly is `NLightning.Client` (decide on `AssemblyName`).
- **Fix sketch:** Update text; set `AssemblyName` if `nltg` is intended.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-186 Agent docs stale after onion M1/M2
- **Status:** fixed (5583af2, 2745e9d)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `CLAUDE.md`, `docs/agents/*`, `src/*/CLAUDE.md`
- **Evidence:** Root CLAUDE.md, BOLT_COVERAGE, ONION plan and per-project guides updated for M1/M2.
- **Fix sketch:** Done (residue in NL-182, NL-183, NL-184).
- **Blocks/Blocked-by:** —
- **Plan ref:** —
