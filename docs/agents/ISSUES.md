# NLightning Issue Ledger

The single durable issue ledger for this repo. GitHub issues are disabled on the fork, so this file replaces them. Every known bug, gap, spec violation, missing feature, test/CI hygiene problem and tech-debt item lives here, so nothing is lost between agent sessions.

Snapshot: 2026-09-25, `wip/fafo`. Sources: `docs/agents/{BOLT_COVERAGE,REPO_MAP,ONION_ROUTING_PLAN,LNBOLT_REVIEW}.md`, every `CLAUDE.md`, the onion M1/M2 workflow reports (open items, review fixes, final follow-ups), a `TODO`/`FIXME`/`NotImplementedException`/commented-out-file sweep, and a Release build. Bug claims were re-checked against the code at that snapshot; items still marked "unverified" in the evidence were not reproduced. Line numbers drift, so re-check the cited line before editing.

Updated 2026-09-25 after the fix swarm and its follow-ups were integrated into `wip/fafo` (at `1a38360`): statuses carry the `wip/fafo` SHAs (the swarm commits were cherry-picked with `-x`), and NL-203..NL-225 record the cross-batch review findings and the follow-ups the batches reported.

Updated 2026-09-25 after the four-lane work (l1 runtime, l2 BOLT 3/signer, l3 state machine, l4 onion M3) was integrated into `wip/fafo` (at `3c625e1`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `integrate:` commits have no lane counterpart), and NL-226..NL-238 record the lanes' new findings and open items.

Updated 2026-09-25 after ABCD wave 0 (W0-A engine seam + events, W0-B persistence, W0-C contracts, W0-D Bolt11, W0-E channel_update, W0-F multi-node harness) was integrated into `wip/fafo` (at `0b7e617`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`), and NL-239..NL-244 record the lanes' new findings (NL-239/NL-240 were already cited by ID in the W0-F harness).

Updated 2026-09-25 after ABCD wave 1 (W1-A channel wiring, W1-B payment core, W1-C payment schema, W1-D IPC/CLI, W1-E channel_update exchange and connect fixes) was integrated into `wip/fafo` (at `342d22e`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `4ae2eb3` and `342d22e` are `integrate:` commits), and NL-245..NL-255 record the lanes' new findings and seams.

Updated 2026-09-25 after ABCD wave 2 (W2-A reestablish, W2-B HTLC switch, W2-C send, W2-D N8 and ABCD Docker proofs) was integrated into `wip/fafo` (at `a5675cb`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `f2f1ef6` and `a5675cb` are `integrate:` commits), and NL-256..NL-270 record the lanes' and the integrator's new findings (NL-256 and NL-257 keep the IDs W2-B proposed; NL-257 was not reproduced and is wontfix).

Updated 2026-09-25 after ABCD wave 3 (W3-A N9 safety, W3-B N10 close (migration owner), W3-C N9 fees, W3-D replay and debt, W3-E CLN interop, W3-F BOLT 5 plan) was integrated into `wip/fafo` (at `c92d837`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `983b2b4` and `c92d837` are `integrate:` commits), and NL-271..NL-290 record the lanes' and the integrator's new findings (NL-278, NL-281 and NL-287 were found and fixed within the wave). The LND Docker suite could not run at integration (NL-276).

Updated 2026-09-26 after ABCD wave 4 (W4-A BOLT 5 plumbing (migration owner), W4-B BOLT 5 builders, W4-C net11, W4-D signet, W4-E interop follow-ups) was integrated into `wip/fafo` (at `6b5d50e`): statuses carry the `wip/fafo` SHAs (lane commits cherry-picked with `-x`; `53accb1`, `960cf05`, `1161213`, `5cc32ba` and `6b5d50e` are `integrate:` commits), and NL-291..NL-300 record the lanes' and the integrator's new findings (NL-291 was cited by W4-D's commits; W4-A's proposed IDs were renumbered to NL-292..NL-294). The LND Docker suite ran from an SDK container (`--network host`, NL-276).

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
| open | 1 | 10 | 27 | 57 | 95 |
| in-progress | 0 | 0 | 0 | 0 | 0 |
| fixed | 13 | 41 | 87 | 58 | 199 |
| wontfix | 0 | 0 | 2 | 4 | 6 |
| duplicate | 0 | 0 | 0 | 0 | 0 |
| **Total** | **14** | **51** | **116** | **119** | **300** |

### Epics

- NL-031: HTLC normal operation (add / fulfill / fail / malformed / commitment_signed / revoke_and_ack / update_fee) (fixed, critical; N6 in wave 1, reestablish and switch in wave 2; the fail-the-channel broadcast is N9-T4 under NL-094)
- NL-034: Channel close (shutdown / closing_signed / option_simple_close) (fixed, critical; legacy close in wave 3 W3-B, Docker proof against LND and CLN close in wave 4; remaining: simple close NL-020, NL-279, NL-285, NL-286, NL-045)
- NL-035: channel_reestablish / option_data_loss_protect (fixed, critical; ABCD wave 2 W2-A; shutdown re-send is N10, Closing resumption NL-036)
- NL-037: Dual funding / interactive-tx (v2 open) (open, medium)
- NL-070: Error onions: failure messages, create / wrap / decrypt (ONION M3) (fixed, high; attribution_data NL-072 open)
- NL-073: Onion integration with HTLC flow: peel after lock-in, forward, final hop, send (ONION M4) (fixed, high; `HtlcSwitch` W2-B and `PaymentService` W2-C; persistent replay set NL-078 deferred)
- NL-079: Route blinding payload handling (ONION M5) (open, medium)
- NL-094: On-chain handling: unilateral close sweeps, HTLC resolution, penalty/justice (open, critical; fail-the-channel broadcast in wave 3; wave 4: `BOLT5_ONCHAIN_PLAN.md` O0 (broadcasts, outpoint watches, per-block unit of work, reorgs) and O1 (revocation log, resolution tables) wired, O2-O6 building blocks (S1, classifier, output mapper, sweep/penalty builders, planner, fee policy) not wired; next: the watcher O2-T5 (NL-272), NL-271, NL-292, NL-296, NL-297)
- NL-099: BOLT 7 gossip: announcements, channel_update, queries, graph (open, high; typed and signed channel_update, direct exchange with the channel peer done)
- NL-114: Invoices not wired into the node: invoice store, create/pay commands, final-hop checks (fixed, high; receive, route hints and pay done in wave 2)
- NL-137: Payment/forwarding persistence: shared secrets, circuits, invoices, attempts, replay set, SCID map (open, high; partial: shared secrets, circuits with replay, invoices, payments and HTLC origins done; replay set (in-memory `IOnionReplayStore` since wave 3, NL-078) and forward failure reasons remain)

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
- **Status:** fixed (a604dff, f5315c0, a02afa7, a388fc4, aac5f60, e5f7312, 4ec83d3, 22c29ae, ca87313)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:88-133`
- **Evidence:** The switch handles only OpenChannel/AcceptChannel/FundingCreated/ChannelReady/FundingSigned; `default` (L133) throws `ChannelErrorException`, so any update_add_htlc, commitment_signed, update_fee etc. **disconnects the peer**. No handlers exist. Update: `default` now throws a channel-scoped `ChannelWarningException` (the peer stays connected), an unknown channel_id gets an `error` for that id, and malformed-without-BADONION warns and closes (699c67b, 00095cb). Update (four-lane integration, `3c625e1`): the building blocks now exist but nothing is wired: ordered per-peer processing and per-channel lock (NL-033, NL-193), per-side params (NL-194), separate commitment numbers (NL-188), HTLC txs and signatures (NL-056, NL-057), revocation guard (NL-189), remote shachain storage (NL-136), and the pure commitment engine with its two-engine invariant simulator (`Channels/Commitments/`, N4). Still missing: persistence of the engine state (N5), the handlers and `ChannelManager` cases (N6), reestablish (N7). The engine/builder seam is NL-230. Update (ABCD wave 0, `0b7e617`): the engine is now connected to the real signer (NL-230, 2fa8cf4), uses one fee calculator (NL-231, 192e212), raises lock-in/fulfill/irrevocable-fail/settle events with `IHtlcSwitch` as the consumer port (N4-T4, b166ea0), and is persisted with one save per transition (`IChannelStateDbRepository`, N5-T1..T3, 4472a8b, bb2731a); `IChannelOperations`, `ChannelState.Failed` and `ChannelFailedException` contracts exist (2ede2ee). Still missing: the handlers, `ChannelOperationsService`/`CommitScheduler` and `ChannelManager` cases (N6, ABCD W1-A), reestablish (N7, W2-A). Update (ABCD wave 1, `342d22e`): N6 is done. The seven receive handlers (`Application/Channels/Handlers/`, all through the scoped `ChannelStateTransitionService`: `ApplyAsync` + one save, then `UpdateCommitments`, then send; the RAA secret is revealed only after the save) and their `ChannelManager` cases (a604dff); the send side `ChannelOperationsService : IChannelOperations`, the debounced `CommitScheduler` (never signs while `RemoteNextCommit` exists, persists `SentCommitDiff` first) and `LocalOnlyHtlcSwitch`, gated by `NodeOptions.EnableHtlcs`, plus startup replay of `DerivePending` (a02afa7); each channel's link is pinned to the connection it turned Open on and every send-side update and signature needs that link (e5f7312). Proofs: in-process `TwoNodeHarness` (30 HTLCs each way, fulfills, fails, fee round, anchors and not, txids identical at every step, I7; f5315c0) and Docker N6-T5 against LND 0.20 (`NormalOperationFlowTests.Given_LndPaysUs_When_LockedIn_Then_FailedBackAndChannelActive`: fail-back decoded by LND at source index 1, commitment numbers 2/2, channel Active; a388fc4). Remaining: forwarding and final-hop receive (NL-073, ABCD W2-B), reestablish (NL-035, W2-A; until then a channel loaded at startup never sends updates, NL-252), fail-the-channel broadcast (NL-200). Deviation: a normal-operation message on a channel that is not Open gets warning + close rather than an error. Update (ABCD wave 2, `a5675cb`): the remaining pieces landed: reestablish (NL-035, 4ec83d3, 22c29ae) and the forwarding/final-hop switch (NL-073, ca87313). Every update message is exercised end to end against LND 0.20 (Docker N6/N7/N8 proofs) and between two NLightning nodes (ABCD suite, green). The fail-the-channel broadcast is N9-T4 and is tracked in NL-094; close is NL-034.
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
- **Status:** fixed (34757a3, b38ce86, 6d81ecd, 9733937, 287a956, 5d0aafc, 8e0e154, d8680cd, 8096700, 8249044)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Enums/ChannelState.cs` (Closing/Closed enum only), `src/NLightning.Infrastructure.Bitcoin/Transactions/ClosingTransaction.cs` (commented out)
- **Evidence:** Nothing moves a channel to Closing; an incoming shutdown disconnects the peer (see NL-031). Funds can only leave a channel via the peer's force close. Update (ABCD wave 3, `c92d837`): legacy cooperative close is implemented (N10-T1..T3): shutdown/closing_signed handlers, `ChannelCloseCoordinator`, pure `LegacyClosingNegotiator`, the BOLT 3 legacy closing tx, states ShuttingDown 23 / Negotiating 25 / Closing 30, migration `AddShutdownState` (3 providers), `closechannel` IPC (ClientCommand 13). Crash-safe: the closing watch is saved with Closing; a funding-spend watch records a mutual close the peer broadcast; startup and every block finish a confirmed close. Docker `CooperativeCloseFlowTests` passed 4/4 against LND at lane step 2 (287a956) but was **not re-run** after the step-3 fixes (8e0e154) nor at integration (Docker env, NL-276). Remaining, each with its own entry: `option_simple_close` (NL-020), the closing timeouts (NL-284), HTLCs added after our shutdown (NL-279), the R09 deviation (NL-285), Docker-only proof gaps (NL-286), local upfront script (NL-045). Close this epic once the Docker close proof passes on `wip/fafo`. Update (ABCD wave 4, `6b5d50e`): the Docker close proof `CooperativeCloseFlowTests` passed 4/4 against LND on `wip/fafo` at integration (in-container runner, NL-276), and `ClnCloseTests` closes against CLN in both roles with `fee_range` (8096700, a38c999). The closing timeouts are in (NL-284). The legacy close is done, so the epic is closed; what remains has its own entry: `option_simple_close` (NL-020), HTLCs added after our shutdown (NL-279), the R09 deviation (NL-285), the Docker restart-while-closing proof (NL-286), the local upfront script (NL-045).
- **Fix sketch:** shutdown/closing_signed handlers with fee_range, closing tx builder (NL-065), then option_simple_close (NL-020). Needs a close IPC command (NL-152).
- **Blocks/Blocked-by:** Blocked-by NL-031 (must wait for HTLCs to clear)
- **Plan ref:** BOLT_COVERAGE roadmap step 11; BOLT2 N10 (legacy), N11 (simple close)

### NL-035 [EPIC] channel_reestablish / option_data_loss_protect
- **Status:** fixed (4620895, 4ec83d3, 1ad14ce, a4e95d7, 82c4c37, 30c1bb8, 22c29ae, f2f1ef6)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:62`
- **Evidence:** TODO only; an incoming channel_reestablish disconnects the peer. `option_data_loss_protect` is advertised **Compulsory** (`FeatureOptions.cs:14`) with no implementation, and state needed for it (NL-136) is not persisted. Update: an incoming channel_reestablish now gets a channel-scoped `warning` instead of a disconnect (699c67b); `option_data_loss_protect` is advertised Optional (ASSUMED bit), not Compulsory. Update (ABCD wave 1, `342d22e`): W1-A left the reestablish hooks for N7 (W2-A): `ChannelStateTransitionService.LoadRemoteShachainAsync`, `SentCommitDiffCodec`, `CreateRevokeAndAck`, and `IPeerLivenessProbe.MarkLinkUp(channelId, peer)` plus the pending-event replay after reestablish (NL-252). `listchannels` reports `IsReestablished`, always false until N7 (e30a845). Update (ABCD wave 2, `a5675cb`): N7 is done. `Domain/Channels/Reestablish/ReestablishPlanner` is a pure implementation of the BOLT 2 rules, with named cases plus a table of about 4.7k cases checked against a spec-literal oracle (4620895). `IChannelManager.OnPeerConnectedAsync`/`OnPeerDisconnectedAsync`/`OnPeerConnectionChanged` are called by `PeerManager`: revert the peer's unsigned updates, send our `channel_reestablish` at connect for V1FundingSigned/ReadyForThem/ReadyForUs/Open, gate updates with `ReestablishGatedLivenessProbe` until the exchange completes, retransmit the stored `SentCommitDiff` byte-identical and a regenerated revoke_and_ack in `LastSent` order, then unsigned updates with their original ids and channel_ready. Data loss is detected: `DataLossDetected` is persisted, then the channel fails without broadcasting (4ec83d3). A peer's reestablish that arrives after channel_ready is answered, and `Publish` drops normal-operation messages of a channel not reestablished on the current connection (22c29ae). Proofs: I11 harness, with a drop at every message boundary, a crash at every save and an old-backup restore (1ad14ce); Docker `ReestablishFlowTests` (a) our restart, (b) an LND restart through `RestartByAlias("alice")` with address-hold containers (30c1bb8, NL-262), (c) a crash after our commitment_signed is persisted (a4e95d7); the ABCD reestablish test. `listchannels` `IsReestablished` reads `IReestablishTracker` (f2f1ef6). Deviation from plan §3.11 step 3: the secret is checked against our secret Y-1, as the spec says. Left for other milestones: shutdown re-send (N10, B2-RE-28), signer-level broadcast refusal after data loss (N9-T4), Closing/Negotiating resumption (NL-036).
- **Fix sketch:** Reestablish on reconnect with commitment/revocation number sync, retransmission, data-loss detection.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-136, NL-125, NL-126, NL-127
- **Plan ref:** BOLT_COVERAGE roadmap step 6; BOLT2 N7

### NL-036 Closing/Stale channels are not handled on startup
- **Status:** fixed (b38ce86, 8e0e154)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs:70`
- **Evidence:** `TODO: Deal with channels that are Closing, Stale, or any other state`. Update (ABCD wave 2, `a5675cb`): `ChannelManager.ResumeStartupStateAsync` handles every stored state (82c4c37). Closed/Stale are skipped. None/V1Opening/V2Opening are never persisted, so they are logged and skipped. V1FundingCreated moves to V1FundingSigned when a watch for the funding txid exists; otherwise it is persisted Stale and not registered (BOLT 2: the funder SHOULD NOT remember). Explicit cases for V1FundingSigned, ReadyFor*, Closing and Failed; Failed re-sends its stored error. Remaining: Closing/Negotiating resumption and close logic (N10), and a Stale funder channel keeps its UTXO locks (NL-259). The funding-tx rebroadcast is NL-258. Update (ABCD wave 3, `c92d837`): ShuttingDown/Negotiating send channel_reestablish at connect, re-send our shutdown and restart negotiation (B2-RE-28/29, b38ce86); Closing channels take part in the reestablish, re-send the agreed closing_signed, and at startup close at once (watch completed), re-create a missing watch or rebroadcast the stored tx (8e0e154). Proven in-process (`CooperativeCloseHarnessTests`, `ClosingLifecycleTests`). Stale funder UTXO locks remain NL-259, funding rebroadcast NL-258.
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
- **Status:** open (partial: 34757a3, b38ce86)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Channels/Factories/ChannelFactory.cs:101,235`
- **Evidence:** `TODO: Generate a script from the local key set`; the feature is advertised Optional. Update: `upfront_shutdown_script` now defaults to No (3c2b673). `ChannelFactory` still throws when the peer requires it; BOLT 2 allows sending a zero-length script instead. Update (ABCD wave 3, `c92d837`): the peer's upfront script is enforced (B2-SHUT-R05) and a script we sent upfront would be reused (B2-SHUT-S09), but we still never generate one.
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
- **Evidence:** A crash between funding_created and funding_signed loses the channel state (funding tx is not yet broadcast, so no direct loss). Spec-wrong: BOLT 2 "Message Retransmission" says a funder that has not broadcast the funding tx SHOULD NOT remember the channel on disconnect. Persisting was tried in eb59385 and reverted in d855f0f; `FundingSignedMessageHandler` persists before publishing. Update (ABCD wave 2, `a5675cb`): the wontfix is pinned by a test (82c4c37): `test/NLightning.Application.Tests/Channels/Reestablish/FunderRememberRuleTests.Given_FundingSigned_Then_PersistedBeforeBroadcast` checks the order add V1FundingCreated, save, broadcast, update V1FundingSigned, save; no store or broadcast after a bad signature. The startup rule forgets a V1FundingCreated channel without a watch (NL-036). A failed broadcast leaves V1FundingCreated only with a mocked monitor. The real `BlockchainMonitorService` saves the watch before publishing, so the channel is V1FundingSigned and the tx is never rebroadcast (NL-258).
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
- **Status:** fixed (699c67b, 00095cb, 4961ba5, 2ede2ee, 1390027, a604dff, a02afa7, 4ec83d3)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Node/Managers/PeerManager.cs` (`HandleChannelMessageResponseAsync`), `src/NLightning.Domain/Channels/Enums/ChannelState.cs`
- **Evidence:** The spec's "send error and fail the channel" can't be expressed: nothing persists a failed state, refuses later updates or re-sends the error on reconnect. Partial: errors are now scoped to their channel id (699c67b); `ChannelWarningException.CloseConnection` gives "warning + close" where BOLT allows it (00095cb); `MessageService` dispose moved off the read loop (4961ba5). Remaining: no failed state; other `ChannelErrorException`s on Open channels (e.g. `ChannelReadyMessageHandler`) still make the peer force-close while we keep the channel (BOLT2 plan G21). Update (ABCD wave 0, `0b7e617`): contracts in place: `ChannelState.Failed = 35` (between Closing and Closed), `ChannelFailedException` (FailedChannelId, MustBroadcast, RequirementId; PeerMessage defaults to null so no local text leaks), `Channels.ErrorSent`/`DataLossDetected` columns (4472a8b). Persisting Failed + ErrorSent, sending the error, refusing updates and re-sending on reconnect remain (N6-T3, ABCD W1-A). Update (ABCD wave 1, `342d22e`): a handler's `ChannelFailedException` makes `ChannelManager` persist `ChannelState.Failed` and the serialized error (`MarkErrorSent`) under the lock before `PeerManager` sends it and disconnects; every later message on the channel is answered with the error again, Failed channels stay in memory at startup (a604dff), and every `IChannelOperations` call on a Failed channel is refused with nothing persisted (a02afa7). Remaining: re-send `ErrorSent` when the peer reconnects (B2-RE-05) and error without disconnect (PeerManager, W2-A); the broadcast / fail-the-channel service (N9-T4). Update (ABCD wave 2, `a5675cb`): a Failed channel's stored error is re-sent on every connection, and any message on it gets the error again without a second persist. `PeerManager` turns every `ChannelFailedException` into an error that keeps the connection, through `PeerOutbox.TryEnqueueError` and `IPeerService.SendErrorAsync` (4ec83d3). Other `ChannelErrorException`s still disconnect. The fail-the-channel broadcast (N9-T4) is tracked in NL-094. `SendErrorAsync` has no unit test (NL-261).
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
- **Status:** fixed (4ec83d3)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Domain/Channels/Interfaces/IChannelManager.cs`, `src/NLightning.Application/Channels/Managers/ChannelManager.cs`
- **Evidence:** It raises every reply through `OnResponseMessageReady` under the channel lock and also returns them as `Task<IReadOnlyList<IChannelMessage>>`. Documented, but a new caller that sends the returned list would send each reply twice (reported by the N0-T3 lane). Update (ABCD wave 1, `342d22e`): not changed. Replies reach the peer only through `OnResponseMessageReady`; `PeerManager.cs:670` ignores the returned list, so nothing is sent twice today. The signature change belongs to the owner of `IChannelManager` (W2-A); `PeerManager.cs` was owned by W1-A in wave 1, not W1-E. Update (ABCD wave 2, `a5675cb`): `HandleChannelMessageAsync` returns `Task`; replies go out only through `OnResponseMessageReady` (4ec83d3).
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
- **Status:** wontfix
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs`, `Channels/Managers/ChannelManager.cs` (`RegisterExistingChannelAsync`)
- **Evidence:** The first snapshot is built at the first channel_ready (NL-232, a604dff). Channels that received channel_ready earlier had the peer's commitment-0 per-commitment point overwritten, so no snapshot can be built: they are logged at startup and HTLC messages on them get a warning (reported by W1-A). Update (ABCD wave 3, `c92d837`): cannot be fixed (813fc85): the first snapshot needs the peer's commitment-0 per-commitment point, which that channel_ready replaced, and BOLT 2 makes the receiver ignore `my_current_per_commitment_point` in channel_reestablish, so nothing recovers it. Such channels keep working without HTLCs; close them with `closechannel` (N10) and reopen. Never build a snapshot from an unverified point.
- **Fix sketch:** Close and reopen such channels (no automated path); or recover commitment 0's point via reestablish (`my_current_per_commitment_point` is not sent for commitment 0), so closing is the practical answer once N10 exists.
- **Blocks/Blocked-by:** Related NL-232, NL-034
- **Plan ref:** BOLT2 N6-T1

### NL-251 Ping-before-commit only checks that the peer is connected
- **Status:** fixed (d7f09a9, c84f81a)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ConnectedPeerLivenessProbe.cs`, `CommitScheduler.cs`
- **Evidence:** BOLT 2 says to send a ping before commitment_signed when the peer has been quiet (B2-CS-S05). `IPeerLivenessProbe`'s default implementation only checks that the channel's pinned connection is still the peer's current one (a02afa7, e5f7312); there is no last-message timestamp or ping API on `IPeerService` (reported by W1-A). Update (ABCD wave 3, `c92d837`): `IPeerService` exposes `LastMessageReceivedAt` and `PingAsync`; the commit scheduler asks `IPingBeforeCommit` outside the lock and pings a quiet peer, awaiting its pong before signing (B2-CS-S05). The ping handler is subscribed before the ping loop is marked started (c84f81a).
- **Fix sketch:** Expose `IPeerService.LastMessageReceivedAt` (or a ping-and-wait API) and ping when it is older than a threshold; swap the probe with `services.Replace` or register it before `AddApplicationServices` (TryAdd).
- **Blocks/Blocked-by:** Related NL-031
- **Plan ref:** BOLT2 N6-T2

### NL-252 Reestablish seam: channel links are marked up only at Open, so channels loaded at startup never send updates and pending events are not replayed after reconnect
- **Status:** fixed (4ec83d3, d1476a4, 22c29ae)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ConnectedPeerLivenessProbe.cs` (`MarkLinkUp`), `Channels/Managers/ChannelManager.cs` (`QueuePendingDomainEventsAsync`, `RaiseDomainEventsAsync`), `ChannelOperationsService.cs`
- **Evidence:** Since e5f7312 a channel's link is pinned to the connection it turned Open on, and every `IChannelOperations` call and every commitment_signed needs that link, so no signature can cover an update the peer never received. A channel loaded at startup or after a reconnect is never marked, so its startup replay is refused (nothing persisted) and locked-in HTLCs stay unresolved until N7. An unsigned update enqueued just as its connection closes stays `SentRemoveHtlc` (the link stays down for good). A `ReadyForThem` channel loaded from the DB that turns Open on funding confirmation after a reconnect is pinned without reestablish (reported by W1-A). Update (ABCD wave 2, `a5675cb`): after each reestablish, `ChannelManager` calls `MarkLinkUp`, queues the pending events for the switch after the lock, and schedules a commit (4ec83d3). `LinkUpReplayingPeerLivenessProbe` also replays a channel's pending events on every `MarkLinkUp` (d1476a4). Both paths run, which is harmless but redundant (NL-264). Channels past funding_signed send their reestablish at connect (22c29ae).
- **Fix sketch:** In N7: after channel_reestablish call `IPeerLivenessProbe.MarkLinkUp(channelId, peer)`, retransmit or forget our unsigned updates and the stored `SentCommitDiff` per BOLT 2, then replay pending domain events (`QueuePendingDomainEventsAsync` + `RaiseDomainEventsAsync`).
- **Blocks/Blocked-by:** Part of NL-035; related NL-031
- **Plan ref:** BOLT2 N7-T3; ABCD W2-A

### NL-254 No node option for the dust-exposure policy
- **Status:** fixed (1dbbc1f, 525973a)
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Node/Options/NodeOptions.cs`, `CommitmentParams.FromChannel` callers
- **Evidence:** The snapshot stores and reloads `MaxDustHtlcExposureMsat` (NL-242), but no option sets it, so the first snapshot is created with none and the dust-exposure check (BOLT 2 `max_dust_htlc_exposure_msat`) never runs (reported by W1-A). Update (ABCD wave 3, `c92d837`): `NodeOptions.MaxDustHtlcExposureMsat` is stored with the first snapshot (`CreateInitialCommitments`); `DustExposureHtlcSwitch` fails incoming dust HTLCs over the limit before forward or preimage (B2-DUST-01/02); `FeeUpdatePolicy` checks the dust limit on a non-anchor fee increase (B2-DUST-05). Snapshots created earlier have no stored limit: NL-290.
- **Fix sketch:** Add a node option (e.g. `Node:MaxDustHtlcExposureMsat`, or feerate-scaled like LND) and pass it to `CommitmentParams.FromChannel` when creating the first snapshot.
- **Blocks/Blocked-by:** Related NL-242
- **Plan ref:** BOLT2 N9-T3

### NL-256 The engine raised no event when an incoming HTLC's removal became irrevocable
- **Status:** fixed (d1476a4)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Commitments/` (`ReceiveCommit`, `ChannelDomainEvents.DerivePending`), `Payments/Switch/HtlcSwitch.cs`
- **Evidence:** Without it, invoices were marked Settled when the fulfill was persisted rather than when it was irrevocable, and archived incoming rows could not be pruned (reported by W2-B). Fixed: the new Domain event `IncomingHtlcSettled` is raised when our removal is final (state 39) and derived by `DerivePending`. The switch prunes the row; simulator invariants (500 seeds, 10k Long) are green (d1476a4).
- **Fix sketch:** Done.
- **Blocks/Blocked-by:** Related NL-243, NL-253
- **Plan ref:** ONION M4-T7; BOLT2 N4-T4

### NL-258 The funder's funding transaction is never rebroadcast
- **Status:** fixed (f251dde, 5bedf44, a9e33a7)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`PublishAndWatchTransactionAsync`), `Application/Channels/Handlers/FundingSignedMessageHandler.cs`
- **Evidence:** The watch is saved before `SendTransactionAsync`, so a crash or a failed publish leaves the channel V1FundingSigned, waiting for a tx that never went out. Startup (NL-036) treats the watch as proof of broadcast (reported by W2-A, review F3). Update (ABCD wave 4, `6b5d50e`): `FundingSignedMessageHandler` saves V1FundingSigned, the funding watch, the signed funding tx (a `BroadcastTransactions` row, purpose Funding) and the funding-output watch in one save, then publishes; every Pending row is sent again after each processing round and at start, also while processing is halted, until a block holds it (IT `ChainMonitorPersistenceTests`: rebroadcast until mined, published at startup). A tx the node refuses for good is retried forever: NL-294.
- **Fix sketch:** Add a publish-only `IBlockchainMonitor` method and rebroadcast unconfirmed funder V1FundingSigned channels at startup (rebuild from the locked UTXOs as `FundingSignedMessageHandler` does), or store the signed raw tx with the watch (migration).
- **Blocks/Blocked-by:** Related NL-048, NL-036
- **Plan ref:** BOLT2 N7-T5

### NL-259 A funder channel forgotten at startup keeps its UTXO locks
- **Status:** open
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`ResumeStartupStateAsync`), `UtxoModel.LockedToChannelId`
- **Evidence:** A V1FundingCreated channel with no watch is persisted Stale and not registered (82c4c37), but nothing calls `ReturnUtxosNotSpentOnChannel` or persists the unlock, so those wallet UTXOs stay locked (reported by W2-A). Update (ABCD wave 4, `6b5d50e`): still open. A funding tx the node refuses for good is now retried every block with a periodic Warning, but never abandoned, so its UTXOs stay locked too (NL-294).
- **Fix sketch:** Release the channel's UTXO locks in the same save that marks it Stale.
- **Blocks/Blocked-by:** Related NL-036
- **Plan ref:** BOLT2 N7-T5

### NL-260 channel_ready retransmitted at reestablish carries only the first local alias
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Reestablish/` (retransmission), `FundingConfirmedMessageHandler.cs`
- **Evidence:** `FundingConfirmedMessageHandler` sends one channel_ready per alias; the reestablish retransmission sends one with the first local alias (or the real scid) (reported by W2-A). Only matters with option_scid_alias, which defaults to No.
- **Fix sketch:** Retransmit one channel_ready per local alias.
- **Blocks/Blocked-by:** Related NL-103
- **Plan ref:** BOLT2 N7-T3

### NL-264 Pending HTLC events are replayed twice after a reestablish
- **Status:** fixed (82b7dcc)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`CompleteReestablishAsync`), `Payments/Switch/LinkUpReplayingPeerLivenessProbe.cs`
- **Evidence:** `CompleteReestablishAsync` queues the pending events itself and also calls `MarkLinkUp`. The W2-B probe decorator turns that call into a second replay. The switch is idempotent, so nothing breaks, but every reconnect does the work twice (reported by the integrator). Update (ABCD wave 3, `c92d837`): the link-up probe leaves the replay to the channel manager when the tracker says the channel was just reestablished, so pending events are replayed once.
- **Fix sketch:** Keep one path (the probe replay) and drop the manager's own queueing, or have the probe skip channels the manager just replayed.
- **Blocks/Blocked-by:** Related NL-252
- **Plan ref:** BOLT2 N7-T2; ABCD W2-A/W2-B

### NL-269 A channel that turned Open on this connection may send an update before LND finishes its reestablish sync (unverified)
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs`, `Reestablish/ReestablishTracker`
- **Evidence:** A channel opened on this connection counts as reestablished, so an update can go out before LND's link has processed its own channel_reestablish; LND would then see update_add_htlc as its first sync message. Not reproduced; no Docker proof covers a reconnect before channel_ready (reported by W2-A).
- **Fix sketch:** Add a Docker proof with a reconnect before channel_ready; if LND objects, hold updates until the peer's reestablish arrives.
- **Blocks/Blocked-by:** Related NL-035
- **Plan ref:** BOLT2 N7-T2

### NL-273 PeerChannelErrorSender bypasses the PeerOutbox ordering
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Safety/` (`PeerChannelErrorSender`), `IPeerService.SendErrorAsync`
- **Evidence:** The fail-the-channel `error` is sent through `IPeerService.SendErrorAsync`, not enqueued on the peer's `PeerOutbox`, so it can overtake queued replies. Harmless for a Failed channel (nothing else is sent for it) (reported by W3-A).
- **Fix sketch:** Add a `PeerManager` API that enqueues a channel error on the outbox and use it here.
- **Blocks/Blocked-by:** Related NL-033, NL-261
- **Plan ref:** BOLT2 N9-T4

### NL-274 HtlcExpiryMonitor treats an invoice settled by another HTLC as preimage-known
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Safety/HtlcExpiryMonitor.cs` (incoming resolution)
- **Evidence:** The monitor derives "preimage known" from the invoice status. An invoice settled by another HTLC of the same payment hash makes an unrelated incoming HTLC look fulfillable, so the channel is failed at cltv_expiry - 18 instead of the HTLC being failed back. A residual race with a switch forward exists only if the switch read a height more than cltv_expiry_delta + ExpiryTooSoonBlocks blocks old (reported by W3-A).
- **Fix sketch:** Resolve per HTLC (stored preimage or origin), not per invoice.
- **Blocks/Blocked-by:** Related NL-094
- **Plan ref:** BOLT2 N9-T2

### NL-277 MessageFactory.CreateClosingSignedMessage uses msat for fee_satoshis and always adds fee_range
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Protocol/Factories/MessageFactory.cs` (`CreateClosingSignedMessage`)
- **Evidence:** The fee is passed as a `ulong` that converts implicitly to msat, so it is 1000x too small, and a `fee_range` is always added. Unused today: `ChannelCloseCoordinator` builds closing_signed directly (reported by W3-B).
- **Fix sketch:** Take a `LightningMoney` (satoshis) and an optional range, or delete the method.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T3

### NL-278 A mutual close the peer broadcast before our final closing_signed was not detected
- **Status:** fixed (8e0e154)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`HandleFundingSpentAsync`), `IBlockchainMonitor.WatchOutpointSpend`
- **Evidence:** If the peer broadcast a closing tx we signed and the link dropped before our final closing_signed, we stayed Negotiating forever (reported by W3-B). Fixed: from the first shutdown the funding outpoint is watched for a spend; a spend shaped like a mutual close to the two shutdown scripts becomes the closing tx (Closing + watch in one save, then Closed at depth).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Part of NL-034
- **Plan ref:** BOLT2 N10-T3

### NL-279 HTLCs added to us after our shutdown are not failed back (B2-SHUT-S08)
- **Status:** open
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`
- **Evidence:** BOLT 2: after sending shutdown a node MUST fail to route any HTLC added after it. The switch forwards or accepts such an HTLC like any other; only our own adds are refused after shutdown (B2-ADD-S13) (reported by W3-B).
- **Fix sketch:** Fail an incoming HTLC on a ShuttingDown channel whose add came after our shutdown, before forwarding or final-hop accept.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T3, B2-SHUT-S08

### NL-282 ChannelCloseCoordinator mutates the in-memory ChannelModel before its save
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Close/ChannelCloseCoordinator.cs`
- **Evidence:** Scripts and state are set on the in-memory model before `SaveChangesAsync`; a failed save leaves memory ahead of the database until a restart (reported by W3-B).
- **Fix sketch:** Apply to a copy and swap after the save, as the commitment engine does.
- **Blocks/Blocked-by:** Related NL-034
- **Plan ref:** BOLT2 N10-T3

### NL-284 closing_signed reply timeout and no-overlap fee_range timeout are missing (B2-CLS-03, B2-CLS-R04)
- **Status:** fixed (d8680cd, 8249044)
- **Severity:** medium
- **Kind:** spec-violation
- **Location:** `src/NLightning.Application/Channels/Close/`
- **Evidence:** BOLT 2: the funder MUST fail the channel if it gets no closing_signed reply in time, and a node that sees no fee_range overlap MUST fail the channel if the peer does not send a new range after a reasonable time; we only warn. Now possible through `IChannelFailureService` (N9-T4) (reported by W3-B). Update (ABCD wave 4, `6b5d50e`): `ClosingTimeoutMonitor` fails the channel through `IChannelFailureService` when our `closing_signed` goes unanswered (`Node:Close:ClosingSignedReplyTimeout`, 5 min, counted only while the peer is on the pinned link) or no overlapping `fee_range` follows (`FeeRangeTimeout`, 10 min), with a `StillApplies` precondition checked under the lock. Memory only: a restart restarts the negotiation.
- **Fix sketch:** Timer per negotiation; on expiry call `IChannelFailureService.FailChannelAsync`.
- **Blocks/Blocked-by:** Related NL-034, NL-094
- **Plan ref:** BOLT2 N10-T3

### NL-285 Closing negotiation holds our fee at our limit instead of proposing strictly between (B2-CLS-R09 deviation)
- **Status:** open (partial: 8096700)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Channels/Closing/LegacyClosingNegotiator.cs`
- **Evidence:** LND 0.20 sends no fee_range and lowers its fee 10 % per closing_signed from its own estimate (4225 sat vs our funder limit 513 sat), so the old R09 rule failed the channel on LND's second message. We now re-send our limit, but only to a peer that already moved strictly towards us (R07); otherwise warning + close, and after `MaxRounds` (100) warning + close (9733937, 5d0aafc). Takes about 19 rounds against LND. A strict peer (CLN/Eclair legacy) may fail over a repeated fee (reported by W3-B). Update (ABCD wave 4, `6b5d50e`): the `fee_range` receive path is proven against CLN in both roles (`ClnCloseTests`, R03/R05/R06), so the repeated-fee path only runs with a peer that sends no `fee_range` (LND 0.20). The integrator listed this as fixed; the ledger keeps it open because the R09 deviation itself is unchanged (option_simple_close, NL-020, is the real fix).
- **Fix sketch:** A better funder limit (e.g. the peer's first offer capped by the commitment fee), or option_simple_close (NL-020).
- **Blocks/Blocked-by:** Related NL-034, NL-020
- **Plan ref:** BOLT2 N10-T3, B2-CLS-R09

### NL-287 A retransmitted channel_ready in ShuttingDown/Negotiating/Closing disconnected the peer
- **Status:** fixed (8e0e154)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Application/Channels/Handlers/ChannelReadyMessageHandler.cs`, `ChannelReestablishMessageHandler`
- **Evidence:** `ChannelReadyMessageHandler` threw `ChannelErrorException` ('Unexpected ChannelReady') when the peer retransmitted channel_ready in a closing state, which LND does per spec (B2-RE-15); and we did not retransmit ours in those states (reported by W3-B). Fixed: ignored in those states, and the reestablish retransmits ours; harness test `Given_IdleChannelShuttingDown_When_Reconnect_Then_ChannelReadyAndShutdownRetransmitted`.
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-036
- **Plan ref:** BOLT2 N10-T3, B2-RE-15

### NL-288 Fee estimate multiplier is sat/kvB, not sat/kw: our feerates are 4x too high
- **Status:** fixed (3350020, 803df11, 1036dfe, 960cf05)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Options/FeeEstimationOptions.cs` (`RateMultiplier` "1000"), `Services/FeeService.cs:162`, `NodeConfigurationExtensions.cs:239`
- **Evidence:** The estimate in sat/vB is multiplied by 1000 (sat/kvB) and used as sat/kw, so 10 sat/vB becomes 10,000 sat/kw (should be 2,500). Our default open to CLN is refused above its 2530 sat/kw limit (Explicit reproducer `ClnInteropTests.Given_OurDefaultFeerate_When_OpeningToCln_Then_ClnAccepts`), and as fundee we refuse LND's open_channel ('Fee rate per kw is too small: 6250, currentFee 10000'; `FeeUpdateFlowTests` LND-funded case skipped, 533330f) (reported by W3-C, W3-E). Update (ABCD wave 4, `6b5d50e`): `FeeRateConverter` converts sat/vB × 250 to sat/kw with a 253 sat/kw floor (`RateMultiplier` ignored with a warning); one shared started `FeeService` for every consumer (`AddFeeServices`, 803df11, registered in 960cf05) that never reports 0 (`FallbackFeeRatePerKw`); the fee source is configurable (`Http`/`Bitcoind`/`Fixed`). The CLN reproducer is a regular test and the LND-funded `FeeUpdateFlowTests` case runs (1036dfe).
- **Fix sketch:** Convert sat/vB to sat/kw (x 250) in one place; drop the Explicit/Skip markers and the explicit test feerates.
- **Blocks/Blocked-by:** Related NL-289
- **Plan ref:** BOLT2 N9-T1

### NL-289 As fundee we refuse a feerate below 80 % of our own estimate
- **Status:** fixed (1036dfe)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:99`, `IFeeService`
- **Evidence:** A CLN-funded channel at CLN's own estimate (253 sat/kw on an idle regtest) is refused (Explicit reproducer `ClnInteropTests.Given_ClnFundsAtItsOwnEstimate_When_Opening_Then_WeAccept`). BOLT 2 only asks to fail an unreasonably low feerate; LND and CLN accept down to the relay floor. Made worse by NL-288 (reported by W3-E). Update (ABCD wave 4, `6b5d50e`): as fundee we accept any `open_channel` feerate from the 253 sat/kw floor; `ClnInteropTests.Given_ClnFundsAtItsOwnEstimate_When_Opening_Then_WeAccept` is a regular test.
- **Fix sketch:** Accept anything from the 253 sat/kw floor (or a configurable lower bound) upwards.
- **Blocks/Blocked-by:** Related NL-288
- **Plan ref:** BOLT2 N9-T1, B2-FEE-R01

### NL-290 Snapshots without a stored dust limit offer unlimited dust on the send side
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Fees/DustExposurePolicy.cs`, engine `UpdateValidator`
- **Evidence:** The engine's send rules B2-DUST-03/04 read `CommitmentParams.MaxDustHtlcExposureMsat` from the snapshot; channels whose first snapshot predates wave 3 have none, and the `NodeOptions` fallback covers only the receive and fee checks (reported by W3-C, 9bfa09d).
- **Fix sketch:** Backfill the stored limit from `NodeOptions` at load (a migration or a one-time save).
- **Blocks/Blocked-by:** Related NL-254
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
- **Status:** fixed (34757a3)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Transactions/ClosingTransaction.cs` (commented out, `TODO: Find out correct lockTime`)
- **Evidence:** No closing tx; `BaseTransaction.cs`/`FundingTransaction.cs` in the same folder are also dead commented code. Update (ABCD wave 3, `c92d837`): Domain `ClosingTransactionModel` + `LegacyClosingTransactionFactory`, `ClosingFeeCalculator`, Infra.Bitcoin `ClosingTransactionBuilder` (v2, locktime 0, BIP69, `0 sig1 sig2 script` in funding-key order); the dead `Transactions/{Closing,Base,Funding}Transaction.cs` are deleted. BOLT 3 has no closing vector: signed txs are verified with NBitcoin's interpreter.
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
- **Status:** fixed (6156173, 234607e, a02afa7, ca87313, c4ad8e9, d1476a4, 6cb279f, 083a726, f2f1ef6)
- **Severity:** high
- **Kind:** gap
- **Location:** planned `src/NLightning.Application/Payments/` (`HtlcSwitch`, `HtlcForwardingPolicy`, `FinalHopProcessor`, `PaymentManager`)
- **Evidence:** Nothing calls peel → replay → deserialize → validate. No forwarding, no final-hop checks, no sending. Update (ABCD wave 0, `0b7e617`): the engine-side gates exist: `IncomingHtlcLockedIn` fires once at lock-in, `OutgoingHtlcFailed` only when the removal is irrevocable, `OutgoingHtlcFulfilled` at once, all re-derivable at startup with `ChannelDomainEvents.DerivePending` (b166ea0); `IForwardingPolicy`/`ForwardingFee` (BOLT 7 fee formula), `ForwardCircuitModel` and `HtlcOrigin` contracts are in Domain (2ede2ee, 1390027). No processor, policy implementation or switch yet (ABCD W1-B, W2-B). Update (ABCD wave 1, `342d22e`): the payment core exists in `Application/Payments/` (6156173, 234607e): `IncomingOnionProcessor` (peel, replay record after a good peel, payload parse/validate, forward/final/malformed/failed results; route blinding refused), `FinalHopProcessor` (0x0013/0x0012 before the 0x400F invoice checks, read-only), `HtlcForwardingPolicy : IForwardingPolicy` (reads `RoutingOptions` on every call), `HintRouteBuilder` (mandatory fee limit) and `PaymentOnionFactory`. `LocalOnlyHtlcSwitch` peels every locked-in HTLC and fails it back (a02afa7). Nothing in `src/` calls the processor, policy or route builder yet: the forwarding switch (M4-T2 wiring, T4, T5, replay) is W2-B and send is W2-C. The final-hop accept must be atomic (NL-253) and the offered HTLC's origin persisted (NL-250). Update (ABCD wave 2, `a5675cb`): M4 is wired. `Application/Payments/Switch/HtlcSwitch` (registered by `AddHtlcSwitchServices`, called from `AddApplicationServices`) handles several cases. It peels locked-in HTLCs, storing the shared secret first. It runs the final hop under a per-payment-hash lock, with the invoice settled in the fulfill's own save (NL-253). It forwards by scid (real, or alias per `option_scid_alias`) with `HtlcForwardingPolicy`, saves the circuit Pending before the offer and marks it Offered after. It fulfills upstream immediately and fails upstream only when the failure is irrevocable, wrapping with the incoming secret and converting malformed. It replays after restart or link-up (`LinkUpEventReplayer`) (ca87313, d1476a4). The send side `Payments/Send/PaymentService` decodes the invoice, routes directly or through the first usable hint and persists the per-hop secrets before `OfferHtlcAsync(HtlcOrigin.Local)`. It decrypts failures with `FailureInterpreter` (6cb279f, 083a726) and is wired to the switch through `PaymentOutcomeSwitchHandler` (f2f1ef6). Proofs: `ThreeNodeSwitchTests` on SQLite with restarts (c4ad8e9), `PaymentHarnessTests`, and the ABCD Docker suite (happy path with exact BOLT 7 fees, variant a decoded at index 3, b1/b2 restarts, c send/receive). Remaining, tracked elsewhere: persistent replay set (NL-078, deferred by roadmap decision 5), attribution_data (NL-072), route blinding (NL-079), alias channel_update in failures (NL-266), forward checks before the first block (NL-267).
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
- **Status:** open (partial: eb597d7)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure/Protocol/Onion/OnionReplayCache.cs`
- **Evidence:** 100k-entry FIFO; replays older than capacity or across restarts are accepted. Update (ABCD wave 2, `a5675cb`): still in memory, by ABCD roadmap decision 5 (regtest only). The switch skips the replay check (`checkReplay: false`) for an HTLC whose shared secret is already stored, so a restart replays persisted HTLCs without tripping the cache (ca87313). A restart still forgets the replay entries of onions that are not stored. Update (ABCD wave 3, `c92d837`): `IOnionReplayStore` (Domain) with `InMemoryOnionReplayStore` (Infrastructure, `AddOnionReplayStore()`): entries are owned by the incoming HTLC and expire at its cltv_expiry, ready to be backed by a table. Not yet wired into the switch in place of `OnionReplayCache`, and not persisted.
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
- **Status:** fixed (4ca9b56)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Services/ChannelOperationsService.cs` (`OfferHtlcAsync`)
- **Evidence:** The `IChannelOperations` contract says the origin (Local payment hash or Forwarded incoming HTLC) is saved atomically with the add, so a restart can tie the outgoing HTLC back to its payment or circuit. `OfferHtlcAsync` validates the origin but does not store it (a02afa7); `IChannelStateDbRepository.SetHtlcOriginAsync` exists since 899e36b (reported by W1-A, integrator). Update (ABCD wave 2, `a5675cb`): `OfferHtlcAsync` stages `SetHtlcOriginAsync` after `ApplyAsync` in the same save, through the new optional `stageWithTransition` callback of `ChannelStateTransitionService.CommitAsync` (4ca9b56). Regression tests are in `ChannelOperationsServiceTests`, and `ThreeNodeHarness` covers it on real SQLite.
- **Fix sketch:** Call `SetHtlcOriginAsync(channelId, key, origin)` after `ApplyAsync` in the same unit of work, together with the payment/circuit rows (W2-B/W2-C).
- **Blocks/Blocked-by:** Blocks NL-073 (restart mid-forward); related NL-137
- **Plan ref:** ONION M4-T7; ABCD W2-B, W2-C

### NL-253 Final-hop invoice accept must be an atomic check-and-mark
- **Status:** fixed (ca87313, d1476a4)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/FinalHop/FinalHopProcessor.cs` (read-only by design), future `Application/Payments/Switch/HtlcSwitch`
- **Evidence:** `FinalHopProcessor` never mutates the invoice, so two concurrent HTLCs for the same payment hash can both pass `Evaluate` and both be fulfilled; a second run for an already Accepted invoice fails with 0x400F, so a restart must act on the HTLC's persisted state instead of re-running the final hop (`IncomingOnionProcessor.ProcessAsync(checkReplay: false)` exists for that path) (reported by W1-B). Update (ABCD wave 2, `a5675cb`): under a per-payment-hash lock, the switch re-reads the invoice and runs `FinalHopProcessor.Evaluate`. It stages Accept, then Settle, then `UpdateAsync` on the fulfill's own unit of work, through the new `IChannelOperations.FulfillHtlcAsync(..., stageWithFulfill, ct)` overload, so the invoice and the fulfill commit in one save. A second HTLC for the hash gets PERM|15 (ca87313, d1476a4). The interleaving is proven deterministically (`HookedUnitOfWork.AfterInvoiceRead`; the test fails when the lock is removed).
- **Fix sketch:** In the switch, under a per-payment-hash lock and in the same unit of work as the staged fulfill: re-read the invoice, `Evaluate`, `Accept` + `UpdateAsync`, save (or compare-and-set the status); fail the loser with PERM|15.
- **Blocks/Blocked-by:** Part of NL-073; related NL-114
- **Plan ref:** ONION M4-T3; ABCD W2-B

### NL-257 Forwarded onions that name a local alias fail after a restart (not reproduced)
- **Status:** wontfix
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (scid resolution)
- **Evidence:** W2-B reported that `LocalAliases` are not persisted, so the switch would answer unknown_next_peer after a restart. Not reproduced at `a5675cb`: aliases are persisted in `ChannelLocalAliases` (NL-103) and reloaded (`ChannelDbRepository.cs:426`), and the switch matches `LocalAliases` (`HtlcSwitch.cs:847`). Wontfix: not a bug. Reopen only with a failing test.
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-103
- **Plan ref:** ONION M4-T4

### NL-265 An outgoing HTLC with no stored origin never reaches the payment handlers
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (origin dispatch)
- **Evidence:** With no stored origin the switch logs a warning and does not call `ILocalPaymentHtlcHandler`; W2-C asked for "Local or no circuit". Since NL-250 every new add stores its origin, so only HTLCs offered by older builds are affected, and `PaymentService.ReconcileInFlightPaymentsAsync` (startup and re-pay) still resolves them (reported by the integrator).
- **Fix sketch:** Call the local payment handlers for an outgoing HTLC with neither an origin nor a circuit.
- **Blocks/Blocked-by:** Related NL-250
- **Plan ref:** ONION M4-T5

### NL-266 UPDATE-class failures embed our channel_update only when its scid equals the onion's
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`, `Application/Gossip/ChannelUpdateService.cs`
- **Evidence:** For option_scid_alias channels `ChannelUpdateService` signs with `RemoteAlias`, so an onion that names one of our `LocalAliases` gets `len=0` (allowed by BOLT 4). Needs an alias-policy decision; moot while ScidAlias=No (reported by W2-B).
- **Fix sketch:** Sign an update for the scid the onion used (alias or real), or keep one update per alias.
- **Blocks/Blocked-by:** Related NL-099, NL-236
- **Plan ref:** ABCD W3-C

### NL-267 Forwarding checks before the first processed block answer temporary_node_failure
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs`, `IBlockchainMonitor.LastProcessedBlockHeight`
- **Evidence:** Forward CLTV and amount checks read the monitor's height; right after startup it is 0, so an HTLC replayed or received then is failed with temporary_node_failure instead of waiting (reported by W2-B).
- **Fix sketch:** Defer the switch's lock-in handling until the monitor has a height (the replay reruns), or read the stored tip.
- **Blocks/Blocked-by:** Related NL-073
- **Plan ref:** ONION M4-T4

### NL-268 The forward liquidity check ignores commitment fees
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Application/Payments/Switch/HtlcSwitch.cs` (usable-channel / `AvailableToSend` estimate)
- **Evidence:** The per-channel usable check and the `AvailableToSend` estimate leave out commitment fees, so the engine's re-check at offer time can still refuse. The forward then fails with temporary_channel_failure after the circuit was saved (handled, but the pre-check is imprecise) (reported by W2-B).
- **Fix sketch:** Compute spendable balance with `CommitmentFeeCalculator` (fee for one more HTLC plus reserve) before choosing the channel.
- **Blocks/Blocked-by:** Related NL-073
- **Plan ref:** ONION M4-T4

### NL-270 Payments have no per-call fee limit and no retries
- **Status:** open
- **Severity:** low
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Payments/Interfaces/IPaymentService.cs`, `Application/Payments/Send/PaymentSendOptions.cs`
- **Evidence:** `IPaymentService.PayInvoiceAsync` takes no fee limit, so every payment uses `PaymentSendOptions.GetMaxFee` = max(0.5 %, 5000 msat) (CLN defaults, `Node:Payments`). There are no automatic retries: a Failed hash can be paid again and the new attempt replaces the old one (W0-C policy) (reported by W2-C).
- **Fix sketch:** Add an optional `maxFeeMsat` to the pay request/IPC (new `ClientCommand` or field) and, later, retries over other hints.
- **Blocks/Blocked-by:** Related NL-114, NL-152
- **Plan ref:** ONION M4-T6

---

## BOLT 5: On-chain handling

### NL-094 [EPIC] On-chain handling: unilateral close sweeps, HTLC resolution, penalty/justice
- **Status:** open (partial: 36d2270, 06da54b, 983b2b4, 7b4a173, f251dde, 4fd3617, dbe4cc8, 5926d0c, 263ab8f, d7a4c73, 369314d, 7394f19, 0df889a, 36e8797, ff7f6cc, 0761773, 960cf05)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`, `src/NLightning.Domain/Onchain/`, `src/NLightning.Infrastructure.Bitcoin/Onchain/`, `src/NLightning.Infrastructure.Bitcoin/Builders/{Sweep,Penalty}TransactionBuilder.cs`
- **Evidence:** No detection of commitment broadcasts, no sweeps, no HTLC on-chain resolution, no penalty tx. A revoked-state broadcast by a peer goes unpunished. Update (ABCD wave 2, `a5675cb`): this entry also tracks the fail-the-channel broadcast service (BOLT2 N9-T4). NL-200 and NL-035 are closed without it: a Failed channel is persisted and its error re-sent but our commitment is not broadcast, and the signer does not yet refuse broadcast signing after `DataLossDetected` (no broadcast path exists). Update (ABCD wave 3, `c92d837`): N9-T4 done: `ChannelFailureService` is the only broadcast path; under the lock it persists Failed + the error, signs the latest local commitment with the stored remote signature (`ILightningSigner.SignLocalCommitmentForBroadcast`, refuses a revoked number), saves the watch and publishes after the lock, retries a refused publish every block and resumes interrupted broadcasts at start (06da54b); the signer refuses every signature after `MarkDataLoss` (also set at registration from `DataLossDetected`). `ChannelManager` hands a `MustBroadcast` failure to it after the lock, and the daemon starts it (983b2b4). Docker `ChannelSafetyFlowTests` (02b12f7, a681dad) passed in the lane, not re-run at integration (NL-276). The design for the rest is `docs/agents/BOLT5_ONCHAIN_PLAN.md` (3b02972, 6290443). Still open: sweeps (to_local after the delay, HTLC outputs), HTLC-timeout/success broadcasts, detecting the peer's commitment on chain (NL-272), a persisted broadcast intent (NL-271), penalty (NL-095). An upstream HTLC forwarded onto a force-closed channel stays AwaitingDownstream until BOLT 5 resolves the downstream HTLC. Update (ABCD wave 4, `6b5d50e`): `BOLT5_ONCHAIN_PLAN.md` O0 and O1 are done and wired: persisted broadcasts with per-block rebroadcast (`IChainBroadcaster`), persisted outpoint watches with every channel's funding output watched (`IOutpointWatcher`), one unit of work per block, the reorg header ring and rewind (7b4a173, f251dde, 5bedf44, a9e33a7; Docker `Onchain/OnchainSmokeTests`); the revocation log written in the revoke_and_ack save, the `ChannelCloses`/`OutputResolutions` tables, `ChannelState.OnchainResolving = 37`, migration `AddOnchainResolution` (4fd3617). The O2-O6 building blocks exist but are **not wired**: signer invariant S1 (dbe4cc8, 0761773; not restored after a restart, NL-297), `FundingSpendClassifier` + `CommitmentNumber.Decode` (5926d0c), `CommitmentOutputMapper` (263ab8f), preimage extraction (d7a4c73), `SignSweepInput` + `SweepTransactionBuilder` (7394f19, ff7f6cc), `PenaltyTransactionBuilder` (0df889a), `OutputResolutionPlanner` (36e8797), `SweepFeePolicy` (369314d), all against the Appendix C/F vectors; `AddOnchainBitcoinServices()` is registered (960cf05). Still open: the watcher that classifies funding spends and drives the planner (O2-T5, NL-272), the sweep scheduler (O6-T1), HTLC resolution into the switch (O3-T3/T4), penalty execution (O5-T2/T3), anchors CPFP (O7), reorg rollback of completed watches (O6-T3, NL-292), a confirmation-target fee estimate (NL-296).
- **Fix sketch:** Watch funding outpoints, classify spends, sweep to_local/to_remote/HTLC outputs, justice txs. Sub-issues: NL-095, NL-096, NL-097, NL-098.
- **Blocks/Blocked-by:** Blocked-by NL-031, NL-056, NL-066, NL-136, NL-067
- **Plan ref:** BOLT_COVERAGE roadmap step 11; BOLT2 N9-T4 (done); `BOLT5_ONCHAIN_PLAN.md` O0-O8

### NL-095 Revocation watch / penalty is a stub
- **Status:** fixed (4fd3617, 0df889a)
- **Severity:** critical
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Interfaces/IRevocationWatchDbRepository.cs` (empty), `src/NLightning.Infrastructure.Persistence/Entities/Bitcoin/RevocationWatchEntity.cs` (not mapped), `BlockchainMonitorService.cs:189-198,400-411` (commented out), `src/NLightning.Infrastructure.Bitcoin/Transactions/PenaltyTransaction.cs`
- **Evidence:** Nothing is watched for revoked commitments. Update (ABCD wave 4, `6b5d50e`): the stubs `RevocationWatchEntity`, `RevocationWatchDbRepository`, `IRevocationWatchDbRepository`, `PenaltyTransactionModel` and `PenaltyTransaction` are deleted (4fd3617). They are replaced by the revocation log (`RevokedCommitments`, written in the revoke_and_ack save when the revoked commitment has HTLCs, plan D2) and the `OutputResolutions` table (D3), and by `PenaltyTransactionBuilder` (batched, single and split; script-executed against every Appendix C commitment and HTLC tx, 0df889a). Executing penalties on chain is part of NL-094 (O5-T2/T3) and NL-272.
- **Fix sketch:** Map the entity (key + config + 3 migrations), implement the repo and the watcher.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** BOLT2 N5-T1 (entity mapping)

### NL-096 BlockchainMonitorService has no reorg handling
- **Status:** open (partial: f251dde, 5bedf44, a9e33a7, 53accb1)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs`
- **Evidence:** ZMQ rawblock only; confirmations and SCIDs are never rolled back. Update (ABCD wave 4, `6b5d50e`): a 100-block header ring (`BlockHeaders`) drives a rewind to the fork point with one rollback save (pending first-seen heights, outpoint spends, broadcast confirmations, headers, state), then `OnBlockDisconnected` per block and the new branch; a deeper reorg halts; a late orphan notification is dropped without a rewind and the fork is searched from our own tip (a9e33a7). Not rolled back: a watch completed in a disconnected block (funding confirmation, channel SCID; NL-292, explicit Docker reproducer `OnchainSmokeTests.Given_FundingBlockReorged_When_CompetingBranchIsActive_Then_ScidFollowsTheFundingTransaction`) and wallet UTXOs (NL-293).
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
- **Status:** fixed (f251dde)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`ProcessBlock`)
- **Evidence:** Exceptions are caught per block but `SaveChangesAsync` still runs for the whole scope afterwards. Reported by the persist-misc batch (unverified). Update (ABCD wave 4, `6b5d50e`): each block is staged in one unit of work; memory and events change only after its save (IT `ChainMonitorPersistenceTests.Given_BlockFailsMidway_When_Processed_Then_NothingPersistedAndLaterRoundProcessesItOnce`, real SQLite).
- **Fix sketch:** One unit of work per block; save only when the block fully succeeds, otherwise discard the scope.
- **Blocks/Blocked-by:** Related NL-097, NL-133
- **Plan ref:** —

### NL-215 The tip block is not processed at startup
- **Status:** fixed (f251dde)
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`StartAsync`, `AddMissingBlocksToProcessAsync`)
- **Evidence:** Catch-up fetches only below the current height, so the tip waits for the next ZMQ block. Reported by the scid-chain batch (unverified). Update (ABCD wave 4, `6b5d50e`): the tip is processed at start.
- **Fix sketch:** Include the current height in the catch-up range.
- **Blocks/Blocked-by:** Related NL-097
- **Plan ref:** —

### NL-216 Halted chain processing is only logged
- **Status:** open (partial: f251dde, a9e33a7)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`IsChainProcessingHalted`), `IBlockchainMonitor`
- **Evidence:** After NL-097 a poisoned block sets `IsChainProcessingHalted` and logs Critical; `IBlockchainMonitor` does not expose it and nothing fails the node or stops channel operations. Update (ABCD wave 4, `6b5d50e`): `IsChainProcessingHalted` is on `IBlockchainMonitor` and is also set by a reorg deeper than the header ring; pending broadcasts are still sent while halted (a9e33a7). Not done: an IPC surface and refusing channel operations while halted.
- **Fix sketch:** Expose the flag, surface it over IPC and refuse new channel operations (or stop the node) while halted.
- **Blocks/Blocked-by:** Related NL-097, NL-094
- **Plan ref:** —

### NL-271 Fail-the-channel has no persisted broadcast intent
- **Status:** open (partial: eb38c26, 7b4a173, 0761773)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Safety/ChannelFailureService.cs`
- **Evidence:** Failed is saved under the lock and the commitment watch only after it. A stop between the two saves leaves no record that a broadcast was wanted, so the start-up resume (06da54b, which covers Failed channels with an unconfirmed commitment watch) skips it, unless an HTLC past its deadline makes the monitor ask again (reported by W3-A). The BOLT 5 plan's invariant S1 wants Failed + the broadcast row in one save. Update (ABCD wave 4, `6b5d50e`): `ChannelFailureService` now saves the commitment's watch in the same save as Failed + the error and publishes after the lock (eb38c26); a failure request can carry a precondition checked under the lock. W4-A added the persisted broadcast row and its staging API (`IBroadcastTransactionDbRepository.Add` + `IChainBroadcaster.PublishAsync`), and the signer can restore S1 from `ChannelSigningInfo.BroadcastSignedCommitmentNumber` (0761773). Remaining: a `MustBroadcast` `ChannelFailedException` from a handler is still persisted Failed by `ChannelManager` before it reaches the service (hub file); the failure service stores a watch, not a `BroadcastTransactions` row; nothing fills the S1 field at registration (NL-297).
- **Fix sketch:** Save the watch (or a 'broadcast requested' marker) in the same save as Failed (migration owner).
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** BOLT2 N9-T4; `BOLT5_ONCHAIN_PLAN.md`

### NL-272 No detection of the peer's commitment (or any non-close tx) spending our funding output
- **Status:** open (partial: f251dde, 5926d0c, 263ab8f)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Application/Channels/Managers/ChannelManager.cs` (`HandleFundingSpentAsync`), `BlockchainMonitorService`
- **Evidence:** A funding spend that is not a mutual close is only logged at critical level. A Failed channel whose peer's commitment confirmed stays Failed and our conflicting publish is retried (PublishFailed) every block; the funding spend watch exists only from the first shutdown on (reported by W3-A and W3-B). Update (ABCD wave 4, `6b5d50e`): every channel's funding output is a persisted watch (both roles, from funding_signed, the fundee's first sighting and a startup backfill) and every spend is raised through `IOutpointWatcher.OnWatchedOutpointSpent` (f251dde). `FundingSpendClassifier` (5926d0c) and `CommitmentOutputMapper` (263ab8f) exist, but nothing calls them: `ChannelManager.HandleFundingSpentAsync` now sees every funding spend and still only logs a non-mutual one as Critical until O2-T5 (`OnchainChannelWatcher`).
- **Fix sketch:** Watch every channel's funding outpoint and classify spends (ours, the peer's current/previous, revoked) per the BOLT 5 plan.
- **Blocks/Blocked-by:** Part of NL-094; related NL-095
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md`

### NL-275 ChannelFailureService logs TxIds in internal byte order
- **Status:** fixed (eb38c26, 8249044)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Application/Channels/Safety/ChannelFailureService.cs`
- **Evidence:** Log lines print the txid bytes as stored, not in the reversed display order that bitcoind and LND show (reported by W3-A). Update (ABCD wave 4, `6b5d50e`): `ChannelFailureService` logs txids in display order; regression test in 8249044.
- **Fix sketch:** Format through the display-order helper.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-280 The wallet hands out spent and shared addresses
- **Status:** open (partial: 10b39e7, 8249044)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinWalletService.cs` (`GetUnusedAddressAsync`), `UtxoDbRepository.Spend`
- **Evidence:** An address with no UTXO rows counts as unused, and spending deletes the row, so spent addresses come back. Concurrent closes get the same shutdown address, which links the channels on chain (reported by W3-B). Close re-watches its address, so the output is credited (9733937). Update (ABCD wave 4, `6b5d50e`): a close skips a shutdown address that another unconfirmed close already pays to (10b39e7) and reservations are atomic across concurrent closes (`ClosingNegotiationRegistry.TryReserveShutdownScript`, 8249044), swapping to the first unused change address. A third concurrent close can still collide (logged), and the wallet still infers use from UTXO rows, so spent addresses come back.
- **Fix sketch:** Record handed-out addresses (reserve per use) instead of inferring use from UTXO rows.
- **Blocks/Blocked-by:** Related NL-281, NL-283
- **Plan ref:** —

### NL-281 BlockchainMonitorService stopped watching a wallet address after its first deposit
- **Status:** fixed (9733937, 8e0e154)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`CheckBlockForWalletMovement`)
- **Evidence:** An address was removed from `_watchedAddresses` after its first deposit (a restart reloaded it), so a second deposit to a reused address, such as a closing output, was never credited (reported by W3-B). Workaround 9733937 (close re-watches its address), fix 8e0e154 (addresses stay watched; regression test with two deposits).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-280
- **Plan ref:** BOLT2 N10-T3

### NL-283 Wallet address generation re-adds index 9 in the second batch
- **Status:** fixed (3350020, 803df11)
- **Severity:** high
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BitcoinWalletService.cs` (`GetUnusedAddressAsync`)
- **Evidence:** New addresses start at `GetLastUsedAddressIndex` (the highest existing index) instead of that + 1, so the second batch of 10 re-adds index 9 and `SaveChanges` hits the (Index, IsChange, AddressType) key; after 10 used addresses, getaddress, funding change and shutdown addresses fail (found by W3-B, not reproduced in Docker). Update (ABCD wave 4, `6b5d50e`): a new batch starts one past the highest stored index of its type and chain; lookup and generation run under a process-wide semaphore, so two scopes never generate the same indexes.
- **Fix sketch:** Start at max + 1; add a test that generates two batches.
- **Blocks/Blocked-by:** Related NL-280
- **Plan ref:** —

### NL-292 A watch completed in a disconnected block is not rolled back (funding confirmation, SCID)
- **Status:** open
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs` (`TryRewindAsync`), `ChannelManager` funding confirmation
- **Evidence:** After a reorg the rewind resets pending watches only; a funding confirmation completed in a disconnected block is logged Critical and the channel keeps its `ShortChannelId` and stays Open, even when the funding tx confirms at another position on the new branch (reported by W4-A, review finding 1). Explicit Docker reproducer `Onchain/OnchainSmokeTests.Given_FundingBlockReorged_When_CompetingBranchIsActive_Then_ScidFollowsTheFundingTransaction` (the smoke logs the funding tx at 256x1 while the channel keeps 255x1x1).
- **Fix sketch:** Raise the disconnect to the watch consumers and re-derive the confirmation and SCID on the new branch (BOLT 5 plan O6-T3); drop the reproducer's `Explicit`.
- **Blocks/Blocked-by:** Part of NL-096, NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6-T3

### NL-293 Wallet effects of disconnected blocks are not rolled back
- **Status:** open
- **Severity:** medium
- **Kind:** bug
- **Location:** `BlockchainMonitorService` (reorg path), `src/NLightning.Infrastructure.Repositories/Database/Bitcoin/UtxoDbRepository.cs` (`Spend`)
- **Evidence:** Deposits added in a disconnected block stay spendable in `IUtxoMemoryRepository` and the database, and UTXOs spent in one stay deleted, because `Spend` deletes the row (reported by W4-A, review finding 1).
- **Fix sketch:** Record a spent-at height on UTXO rows instead of deleting them (migration owner), then undo deposits and spends above the fork point in the rollback save.
- **Blocks/Blocked-by:** Part of NL-096; related NL-280
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O6-T3

### NL-294 No abandonment rule for a broadcast the node refuses for good
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `BlockchainMonitorService` (`RebroadcastPendingAsync`, `TrySendAsync`), `BroadcastTransactions` (state `Abandoned` unused)
- **Evidence:** A Pending broadcast is re-sent after every block until a block holds it. A tx bitcoind will never accept (e.g. a funding tx with missing or double-spent inputs) is retried forever and its UTXOs stay locked. Visibility is fixed: Warning on the first refusal and then every 6 in a row (a9e33a7) (reported by W4-A, review finding 5).
- **Fix sketch:** Mark a row Abandoned after N refusals with a permanent reject reason or when its inputs are spent elsewhere, and release the channel's UTXO locks with it.
- **Blocks/Blocked-by:** Related NL-259, NL-258
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O0-T1

### NL-296 IFeeService has no confirmation-target fee estimate
- **Status:** open
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Bitcoin/Interfaces/IFeeService.cs`, `src/NLightning.Domain/Onchain/Fees/SweepFeePolicy.cs`
- **Evidence:** `SweepFeePolicy.GetConfirmationTarget` computes the target from the deadline (clamp(deadline - tip - 3, 1, 144)), but `IFeeService` returns one node-wide rate, so the caller must pass an estimate for that target in (plan gap OG12; reported by W4-B).
- **Fix sketch:** Add `GetFeeRatePerKwAsync(confirmationTarget)` (bitcoind `estimatesmartfee` / mempool.space buckets) for the sweep scheduler.
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` OG12, O6-T1

### NL-297 Signer invariant S1 is not restored after a restart
- **Status:** open
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs` (`RegisterChannel`), `ChannelSigningInfo.BroadcastSignedCommitmentNumber`, channel registration at startup
- **Evidence:** S1 (never reveal the secret of, or sign past, a commitment signed for broadcast) is kept in memory; the signer restores it at `RegisterChannel` from `ChannelSigningInfo.BroadcastSignedCommitmentNumber` (0761773), but nothing fills that field from the persisted broadcast, so after a restart a racing revoke_and_ack is not blocked by the signer. The tx is already published, and a Failed channel refuses normal-operation messages, so this is defense in depth (reported by W4-B, O2-T1 partial).
- **Fix sketch:** Fill the field at registration from the persisted commitment broadcast (`BroadcastTransactions` row or the Failed channel's commitment watch); add a restart test through `PeerManager.StartAsync`.
- **Blocks/Blocked-by:** Part of NL-094; related NL-271
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O2-T1, O2-T2

### NL-299 BOLT 5 penalty witness weights: we estimate the real witness, below the spec's upper bounds
- **Status:** wontfix (deliberate: the real witness weight is exact and never below the signed weight; the spec numbers are upper bounds)
- **Severity:** low
- **Kind:** spec-violation
- **Location:** `src/NLightning.Domain/Onchain/Fees/SweepWeights.cs`
- **Evidence:** BOLT 5 gives 160 (to_local penalty) and 249 (accepted HTLC penalty) as witness weights; they assume an 8-byte to_self_delay push and a 3-byte cltv push and count the `1` element as one byte. Our estimator computes the actual witness: to_local 155-156, accepted HTLC 249 with a 3-byte cltv push and 248 with Appendix C's 2-byte expiries, offered HTLC exactly 243; the estimate is never below the signed weight (reported by W4-B).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Part of NL-094
- **Plan ref:** `BOLT5_ONCHAIN_PLAN.md` O5-T1

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
- **Status:** fixed (6156173, 234607e, 899e36b, 6cfbcd1, c10a78e, 4ae2eb3, ca87313, d1476a4, 0870ab1, 6cb279f, 083a726, f2f1ef6)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Bolt11` (no `src/` project references it), `src/NLightning.Domain/Client/Enums/ClientCommand.cs`
- **Evidence:** No invoice/preimage table, no CreateInvoice/PayInvoice IPC, no payment_secret checks. Update (ABCD wave 0, `0b7e617`): `Invoice.Encode` validates first and rejects unknown even and doubled feature bits; the node-key encode path is covered and LND 0.20 invoice fixtures decode (NL-120, 2d8a9fe, 2c9f812, f93d059); `InvoiceModel`, `IInvoiceService`, `IInvoiceDbRepository` and `ClientCommand` 9-12 contracts exist (2ede2ee). No invoice store, service or final-hop processor yet (ABCD W1-B, W1-C). Update (ABCD wave 1, `342d22e`): Application references Bolt11; `InvoiceService : IInvoiceService` creates (CSPRNG preimage/secret), signs with the node key and persists invoices (features 8/14 compulsory, `c` from `Routing.InvoiceMinFinalCltvExpiry`, no route hints, NL-245) and the final-hop checks exist (6156173, 234607e); the invoice table and repository (899e36b); CreateInvoice/ListInvoices/PayInvoice/ListPayments IPC and CLI (6cfbcd1, c10a78e); composition root registration (4ae2eb3). CreateInvoice and ListInvoices work end to end; PayInvoice/ListPayments answer "not available" until `IPaymentService` exists (W2-C). Receive is not wired: the switch still fails every HTLC (W2-B). Update (ABCD wave 2, `a5675cb`): receive and pay are wired. The switch runs the final hop and settles the invoice in the fulfill's save (ca87313, d1476a4). Invoices carry route hints for private channels (NL-245, 0870ab1). `PaymentService : IPaymentService` backs `payinvoice`/`listpayments` (6cb279f, 083a726). It is registered in `AddApplicationServices`, and in-flight payments are reconciled at startup (f2f1ef6). Docker N8: LND pays our invoice (also a trimmed HTLC), we pay LND's invoice, 10 concurrent payments each way, and a restart with our HTLC in flight. ABCD c-send and c-receive are green. No automatic retries and no per-call fee limit (NL-270).
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
- **Status:** fixed (0870ab1, 083a726)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Application/Payments/Invoices/InvoiceService.cs`
- **Evidence:** `InvoiceService` writes no `r` fields, so a payer that is not our direct peer cannot reach us over private channels. Not needed for ABCD variant (c), where Alice pays Bob directly (reported by W1-B). Update (ABCD wave 2, `a5675cb`): `InvoiceService` adds up to 3 `r` hints for our private channels (largest peer balance first). Each hint carries the **peer's** stored `channel_update` policy (BOLT 11: the policy of the channel from the hint node towards the payee), not ours. It skips disabled updates, peers with no update, down links, and (for an amount) channels the peer can't spend it over. scid_alias channels use `RemoteAlias` (not checked against LND). Tests: `InvoiceRouteHintTests`.
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
- **Status:** open (partial: 4472a8b, 2ede2ee, 899e36b, 4ae2eb3, 4ca9b56, ca87313, d1476a4)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Infrastructure.Persistence/Entities/Channel/HtlcEntity.cs` (+ new entities)
- **Evidence:** No tables for per-HTLC onion shared secret, forwarding circuit, invoices/preimages, payment attempts, replay entries, SCID/alias→channel, or the channel graph. Update (ABCD wave 0, `0b7e617`): `HtlcEntity.OnionSharedSecret` with `IChannelStateDbRepository.Set/GetOnionSharedSecretAsync` (4472a8b); Domain models and repository ports for invoices, payments and forward circuits (2ede2ee, 1390027). Remaining: the tables and repositories (ABCD W1-C), replay set, SCID map. Update (ABCD wave 1, `342d22e`): migration `AddInvoicesPaymentsAndCircuits` (all three providers, no data step): `Invoices`, `Payments` + `PaymentHops` (route with each hop's Sphinx shared secret), `ForwardCircuits` (PK incoming channel/HTLC id, indexes on status and outgoing HTLC), `Htlcs.Origin*` (the `HtlcOrigin` of offered HTLCs, indexed for startup replay) and `Channels.MaxDustHtlcExposureMsat`; `InvoiceDbRepository`/`PaymentDbRepository`/`ForwardCircuitDbRepository` hang off `IUnitOfWork`, and `IChannelStateDbRepository.Set/Get/FindHtlcOrigin` (899e36b); the repositories are resolvable from the scope (4ae2eb3). Container round trips on Postgres and SQL Server migrate seeded pre-migration rows. Remaining: a persistent replay set (NL-078), an SCID/alias → ChannelId map, stored failure reasons for forwards, the graph; `OfferHtlcAsync` does not call `SetHtlcOriginAsync` yet (NL-250). Update (ABCD wave 2, `a5675cb`): `OfferHtlcAsync` stores the `HtlcOrigin` in the add's save (NL-250, 4ca9b56). The switch uses circuits for replay: a Pending circuit adopts the HTLC `FindHtlcsByOriginAsync` finds or is failed upstream, an Offered one resumes, and a Fulfilled/Failed circuit resolves its upstream HTLC from the live or archived outgoing record (ca87313, d1476a4). Payments store every hop's shared secret (6cb279f). No schema change was needed in wave 2. Remaining: persistent replay set (NL-078), stored failure reasons for forwards, the graph.
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
- **Status:** fixed (899e36b, a02afa7, ca87313, d1476a4)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Infrastructure.Repositories/Database/Channel/ChannelStateDbRepository.cs` (`PruneSettledHtlcsAsync`), `ChannelDbRepository.GetByIdAsync`
- **Evidence:** Settled HTLCs stay as an archive with their final state so events can be re-derived after a crash (I8), but nothing calls `PruneSettledHtlcsAsync`; rows grow without limit and `GetByIdAsync` (often used as an existence check) loads them all (reported by the W0-B lane). Update (ABCD wave 1, `342d22e`): `IChannelDbRepository.ExistsAsync` is a cheap existence check (899e36b); `LocalOnlyHtlcSwitch` prunes a settled outgoing HTLC's archived row on `OutgoingHtlcSettled` in one save (a02afa7). Remaining: the W2-B `HtlcSwitch` must keep pruning, in an order that respects payments and circuits (it replaces `LocalOnlyHtlcSwitch`); existence checks still using `GetByIdAsync` should move to `ExistsAsync`. Update (ABCD wave 2, `a5675cb`): `HtlcSwitch` prunes a settled outgoing row once the upstream is resolved and the circuit or payment is handled. It prunes incoming final rows on the new `IncomingHtlcSettled` event (NL-256), and startup/link-up replay prunes rows left by older builds (ca87313, d1476a4). The ABCD and three-node proofs assert that no settled rows are left. Leftover, not tracked separately: some existence checks still use `GetByIdAsync` instead of `ExistsAsync`.
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
- **Status:** open (partial: 5611156, 2ede2ee, 6cfbcd1, c10a78e, c50fc7b, f2f1ef6, 6d81ecd)
- **Severity:** high
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Client/Enums/ClientCommand.cs`
- **Evidence:** Only NodeInfo, ConnectPeer, ListPeers, GetAddress, WalletBalance, OpenChannel(+Subscription). A user can't close a channel or pay. Update: `ClientCommand.ListChannels = 8` with the `listchannels [peer_id]` CLI (5611156; local/remote commitment numbers since 3c625e1). Close, invoice, pay and disconnect remain. Update (ABCD wave 0, `0b7e617`): Domain side of invoice/payment IPC: `ClientCommand` CreateInvoice=9, PayInvoice=10, ListInvoices=11, ListPayments=12 (next free 13), their request/response DTOs, and `ChannelInfoClientResponse.IsReestablished`/`FeeBaseMsat`/`FeePpm` (2ede2ee, 1390027). Handlers, IPC registration and CLI remain (ABCD W1-D); close and disconnect remain. Update (ABCD wave 1, `342d22e`): CreateInvoice (9), PayInvoice (10), ListInvoices (11) and ListPayments (12) have MessagePack DTOs, daemon IPC handlers on a shared `ClientCommandIpcHandler` base ("not available" while a payment service is unregistered), scoped client handlers and CLI commands with snapshot-tested printers; `RoutingOptions` is bound and the default config writes `Node:EnableHtlcs` (true on regtest) and the `Node:Routing` defaults; hardening after review: exact error mapping, pipe cap 64, payinvoice wait ≤ 300 s, list count ≤ 1000 (6cfbcd1, c10a78e, c50fc7b). Remaining: close and disconnect commands; PayInvoice/ListPayments need `IPaymentService` (W2-C). Update (ABCD wave 2, `a5675cb`): `payinvoice` and `listpayments` work end to end now that `IPaymentService` is registered (f2f1ef6; `PaymentSendIpcTests`, ABCD c-send). The daemon binds `Node:Payments` (`PaymentSendOptions`). Remaining: close and disconnect commands. Update (ABCD wave 3, `c92d837`): `closechannel` (ClientCommand.CloseChannel = 13, next free 14) with daemon handler, CLI and `Node:Close` options (6d81ecd). Remaining: disconnect.
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
- **Status:** fixed (d35784f, 9e6f5ef, 07b383e)
- **Severity:** low
- **Kind:** tech-debt
- **Location:** `src/NLightning.Daemon.Contracts/*.csproj`, `src/NLightning.Daemon.Plugins/*.csproj`
- **Evidence:** Pulls MessagePack 3.1.3 (vulnerable, see NL-170). Update (ABCD wave 4, `6b5d50e`): every project targets net10.0, plus net11.0 when built with SDK 11 (gated in `src/`/`test/Directory.Build.props`); `Daemon.Contracts`/`Daemon.Plugins` dropped net9.0; Microsoft.Extensions 10.0.12; CI installs SDK 10 and 11 (`docs/agents/NET11_PLAN.md`). Docker, NativeAOT and Wasm on SDK 11 are not verified (NL-300).
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

### NL-291 No signet or custom signet (Mutinynet) network; testnet chain hash garbled
- **Status:** fixed (e7d9037, 3350020, 959f310, 53accb1, 960cf05)
- **Severity:** medium
- **Kind:** gap
- **Location:** `src/NLightning.Domain/Protocol/Constants/ChainConstants.cs`, `src/NLightning.Domain/Protocol/ValueObjects/BitcoinNetwork.cs`, `src/NLightning.Daemon/Extensions/NodeConfigurationExtensions.cs`
- **Evidence:** The node knew only mainnet/testnet/regtest; an unknown network fell back to mainnet in the wallet services; the testnet chain hash constant was wrong. Fixed in W4-D: signet chain hash, static custom-signet registration (`Node:CustomSignet`), fail-fast `BitcoinNetwork.Resolve`, signet/Mutinynet daemon defaults with a per-network fee source, `NBitcoinNetworkResolver.ToNBitcoinNetwork()` with no mainnet fallback (e7d9037, 3350020, 959f310); integration binds through `Resolve` and lists signet/mutinynet in the usage text (53accb1, 960cf05). How to run on Mutinynet: `docs/agents/MUTINYNET.md`. The live Mutinynet smoke test is not done; per-call network resolution remains in places (NL-298).
- **Fix sketch:** —
- **Blocks/Blocked-by:** Related NL-298
- **Plan ref:** ABCD wave 4 W4-D

### NL-295 The open-channel subscription misses a V1FundingSigned reached before it subscribes
- **Status:** open
- **Severity:** low
- **Kind:** bug
- **Location:** `src/NLightning.Daemon/Handlers/OpenChannelClientSubscriptionHandler.cs`
- **Evidence:** The handler only reacts to channel updates raised after it subscribes, so a channel that reaches V1FundingSigned first is never reported to the client. Seen once in the O0 Docker smoke before 5bedf44 moved the update after the publish (reported by W4-A).
- **Fix sketch:** Check the channel's current state right after subscribing.
- **Blocks/Blocked-by:** Related NL-263
- **Plan ref:** —

### NL-298 Network resolution is not unified: builders, signer and some Application code resolve per call
- **Status:** open
- **Severity:** low
- **Kind:** tech-debt
- **Location:** Infrastructure.Bitcoin builders, `LocalLightningSigner`, `SecureKeyManager`, `ShutdownScriptProvider`, `FallbackAddressTaggedField` (`Network.GetNetwork(name)`); `ChannelFailureService` (falls back to `Network.RegTest`); `ChannelManager`, `ChannelCloseCoordinator` (`Network.Main` for parsing)
- **Evidence:** Only the `Wallet/` services use `NBitcoinNetworkResolver.ToNBitcoinNetwork()`; `GetNetwork` knows `signet` and throws otherwise, so a custom signet works, but the fallbacks hide misconfiguration (reported by W4-D, `docs/agents/MUTINYNET.md` known gaps).
- **Fix sketch:** Resolve every NBitcoin network through `ToNBitcoinNetwork()` and remove the fallbacks.
- **Blocks/Blocked-by:** Related NL-291
- **Plan ref:** —

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
- **Status:** fixed (03a19fa, 9762e28)
- **Severity:** medium
- **Kind:** bug
- **Location:** `src/NLightning.Infrastructure/DependencyInjection.cs:29` (`AddSingleton<ISha256, Sha256>()`), consumers such as `ChannelFactory` and the repositories
- **Evidence:** `Sha256` keeps state between `AppendData` and `GetHashAndReset`, so two concurrent users of the singleton can interleave and get wrong hashes. The update_fulfill_htlc handler now hashes preimages with its own instance (1392489); other users still share it (reported by W1-A). Update (ABCD wave 3, `c92d837`): `ISha256` is registered as `ThreadLocalSha256` (one hash state per thread), and hashing goes through a one-shot `ComputeHash` that resets the state even when appending throws.
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
- **Status:** fixed (c53afa2)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Tests.Utils/Mocks/FakeServiceProvider.cs`
- **Evidence:** `GetService` throws `KeyNotFoundException` for unregistered types, which breaks the `IServiceProvider` contract; `ChannelManager` now calls `GetService<IHtlcSwitch>()` and resolves `ChannelDomainEventQueue`, so tests using the fake must register them (reported by W1-A). Update (ABCD wave 3, `c92d837`): `FakeServiceProvider.GetService` returns null for unregistered types; `Strict = true` restores the throw.
- **Fix sketch:** Return null for unknown types; keep an opt-in strict mode if some tests rely on the throw.
- **Blocks/Blocked-by:** —
- **Plan ref:** —

### NL-261 PeerService.SendErrorAsync has no unit test
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `src/NLightning.Infrastructure/Node/Services/PeerService.cs` (`SendErrorAsync`), `test/NLightning.Infrastructure.Tests/`
- **Evidence:** W2-A added `IPeerService.SendErrorAsync` (error without disconnect, NL-200); it is covered only through `PeerManager`/Docker, and Infrastructure.Tests was outside that lane (reported by W2-A).
- **Fix sketch:** Add a PeerService test that the error is sent and the connection stays open.
- **Blocks/Blocked-by:** Related NL-200
- **Plan ref:** BOLT2 N6-T3

### NL-262 A restarted LND container can come back on another address; LNUnit RestartByAlias(isLND: true) then hangs
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/ReestablishFlowTests.cs` (`HoldAddressesBelowAsync`), LNUnit 3.0.4
- **Evidence:** OrbStack/Docker gives a restarted container the lowest free address in the network, so alice moved (.5 → .2) once earlier containers were removed; every later test lost her. LNUnit's `isLND: true` path re-adds the node without removing the stale connection and waits forever in `WaitUntilAliasIsServerReady`. Worked around (30c1bb8): idle `nltg-address-hold-N` containers fill the lower addresses during the restart, and the test asserts her address is unchanged (reported by W2-A).
- **Fix sketch:** Fix upstream in LNUnit (drop the stale connection, re-resolve the address), or give fixture containers static IPs.
- **Blocks/Blocked-by:** Related NL-180
- **Plan ref:** BOLT2 N7 proof

### NL-263 Docker ChannelOpeningFlowTests.GivenSingleP2TRInput flake: "No locked UTXOs found"
- **Status:** fixed (5cc32ba)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/ChannelOpeningFlowTests.cs`
- **Evidence:** Failed once in a full Docker run and passed in every other full run and in class reruns (reported by W2-A). Not seen in the integrator's runs at `a5675cb`. Update (ABCD wave 3, `c92d837`): the same message ('No locked UTXOs found for channel', `OpenChannelClientSubscriptionHandler`) failed `NormalOperationFlowTests.Given_InFlightRestart` once during its open (W3-B) and passed on rerun. Possibly related to the wallet address bugs NL-280/NL-283. Update (ABCD wave 4, `6b5d50e`): reproduced at integration: `AcceptChannel1MessageHandler` moved the UTXO locks to the real channel id only after `UpgradeChannel` raised `OnChannelUpgraded`, so the open subscription looked the locks up under the new id too early. The locks now move first (regression test `Given_ValidAcceptChannel_When_ChannelIsUpgraded_Then_UtxoLocksAlreadyCarryTheNewChannelId`); `AbcNetworkTests` + `ChannelOpeningFlowTests` 8/8 after it. A related race (V1FundingSigned raised before the subscription exists) is NL-295.
- **Fix sketch:** Capture the wallet/UTXO state on failure; check for a race between funding the wallet and the lock.
- **Blocks/Blocked-by:** Related NL-180
- **Plan ref:** —

### NL-276 Host dotnet cannot reach Docker bridge container IPs; LNUnit Docker tests fail in the fixture
- **Status:** open
- **Severity:** high
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/` (LNUnit `LightningRegtestNetworkFixture`), this macOS host
- **Evidence:** In wave 3 the host process got EHOSTUNREACH ('No route to host 192.168.215.x:18443') to container IPs while ping and /usr/bin/curl worked (likely macOS Local Network privacy for the parent app, or OrbStack routing). All 44 LND-based Docker tests failed at setup at integration (`c92d837`), so the ABCD 3-run gate and the N9/N10 Docker proofs were not re-verified there; the CLN and database fixtures publish ports on 127.0.0.1 and passed. Lanes worked around it by running the host-built test dll inside an SDK container on the bridge (`test/CLAUDE.md`). Concurrent Docker runs also force-remove each other's fixture containers (miner/alice/...). Update (ABCD wave 4, `6b5d50e`): still reproduces on the host. Workaround documented in `test/CLAUDE.md` (6b5d50e): run the host-built test dll inside an SDK container with `--network host`, which reaches the bridge addresses, the ports published on 127.0.0.1 and `host.docker.internal`; with it all 77 Docker tests passed at integration (`--network bridge` fails the database and connect-back cases).
- **Fix sketch:** Grant Local Network access to the terminal/Claude app or restart OrbStack; longer term publish LNUnit ports on 127.0.0.1 or run Docker tests from a container by default, and serialize Docker runs across agents.
- **Blocks/Blocked-by:** Related NL-180, NL-263
- **Plan ref:** ABCD wave 3 gate

### NL-286 Close interop gaps are proven in-process only
- **Status:** open (partial: 8096700, a38c999)
- **Severity:** low
- **Kind:** test
- **Location:** `test/NLightning.Integration.Tests/Docker/CooperativeCloseFlowTests.cs`, `test/NLightning.Application.Tests/Channels/Close/`
- **Evidence:** LND 0.20 ignores fee_range, so the fee_range receive path (B2-CLS-R03..R06) runs only between two of our nodes; there is no Docker restart while ShuttingDown/Negotiating/Closing, no close against CLN/Eclair, and an LND-funded channel closed by LND (our non-funder path) is in-process only (reported by W3-B). Update (ABCD wave 4, `6b5d50e`): close against CLN is proven in Docker (`ClnCloseTests`: we close with and without our `fee_range`, CLN closes a channel we funded and one it funded, each to a confirmed closing tx; CLN's fee, range and our decision asserted from the log). Still missing: a Docker restart while ShuttingDown/Negotiating/Closing, and Eclair. The integrator listed this as fixed; the ledger keeps it open for the restart case.
- **Fix sketch:** Add a Docker restart-while-ShuttingDown case, and CLN close cases on the W3-E `ClnFixture`.
- **Blocks/Blocked-by:** Related NL-034, NL-285
- **Plan ref:** BOLT2 Proof N10

### NL-300 net11.0 Docker suite, NativeAOT publish and Wasm on SDK 11 not verified
- **Status:** open
- **Severity:** low
- **Kind:** test
- **Location:** `src/Directory.Build.props`, `test/Directory.Build.props`, `docs/agents/NET11_PLAN.md`
- **Evidence:** The multi-target build and the non-Docker tests pass on net10.0 and net11.0 (SDK 11 rc.1), but the Docker suite ran on net10.0 only (both frameworks would fight over the fixed container names), and NativeAOT and Wasm were not built with SDK 11. Seen with SDK 11 rc.1: building through a symlinked path skipped `CopyToOutputDirectory` items (not reproduced from the real path). `IHost.RunAsync` exits 1 on a failed BackgroundService on net11.0 but 0 on net10.0 (reported by W4-C).
- **Fix sketch:** Run the Docker suite with `-f net11.0`; publish AOT and build Wasm with SDK 11; after GA set `allowPrerelease: false`.
- **Blocks/Blocked-by:** Related NL-155
- **Plan ref:** `NET11_PLAN.md` step 6

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
