# BOLT 7 Gossip: Implementation Plan for NLightning

This plan covers BOLT 7 "P2P Node and Channel Discovery". It has six milestones:
- **G0** typed wire messages with strict parsing and signature helpers;
- **G1** public channels (`announce_channel`, `announcement_signatures`, our `channel_announcement` and `node_announcement`);
- **G2** validation of incoming gossip and a persisted graph store with pruning;
- **G3** gossip queries, sync and relay;
- **G4** pathfinding over the graph, wired into `PaymentService`;
- **G5** hardening, IPC, metrics and the mainnet gate.

Every repo claim cites a repo-relative path, with line numbers where they are useful. Line numbers drift, so check them before you edit. Claims marked **(unverified)** or **(inferred)** were not proven against running code or a live peer.

- **Spec source:** `lightning/bolts` master, fetched 2026-09-26: `07-routing-gossip.md` (whole document); `04-onion-routing.md` (§Failure Messages: the `channel_update` field and the origin rules); `09-features.md` (bits 6/7 `gossip_queries`, 10/11 `gossip_queries_ex`, 46/47 `option_scid_alias`); `02-peer-protocol.md` (`channel_flags.announce_channel`, the rule forbidding `option_scid_alias` together with `announce_channel`, the `channel_ready` alias). Re-read the requirement block before you implement a handler.
- **Relation to the other plans:** ABCD roadmap §1.1 picked option B (route hints) and deferred option A (public channels plus BOLT 7) as "2-3 more waves on NL-099". This plan is option A. It builds on the W1-E `channel_update` exchange (`Application/Gossip/ChannelUpdateService`) and on BOLT 5 O0 (the chain monitor's per-block input scan and reorg ring). It feeds `PaymentRoutePlanner` (ABCD W6-C) with graph paths.
- **Issue ledger:** the epic is NL-099. Related entries: NL-008 (address descriptors), NL-054 (routing-table TODOs), NL-236 (no public-channel option), NL-255 (LND never hints through us without a `node_announcement`); NL-100, NL-101, NL-102, NL-103, NL-205 and NL-209 are already fixed. New gaps found while writing this plan are listed in §2.2 as `GG#` rows marked "new" in the NL column; the ledger agent files them (the next free ID at `368a057` is **NL-332**). Tasks say "Resolves NL-…". Update the ledger entry in the same commit as the fix.
- **Status (2026-09-26, `wip/fafo` @ `48a8951`, after gossip wave G-D):** waves G-A..G-D are done (see the wave records in §5). G5-T2 (spam protection: rate limits with replay of refused updates, keep-alive rule, misbehaviour score and 1 h ban, `MaxChannels`/`MaxNodes`, future timestamps, relay backlog bound), G5-T3 (batched write-behind, streamed bulk load: 200k channels in 1.55 s on SQLite, no `AddGossipIndexes` needed) and G5-T4 (`describegraph` = `ClientCommand` 20, `Meter("NLightning.Gossip")`) are done. G5-T1 is partial: caps enforced and the memory estimate exists, but node ids are not interned and `Gossip:MaxMemoryMb` is not enforced (measured 475 MiB store + 39 MiB per snapshot at 200k channels, NL-373). G5-T5 is partial: the template writes the D12 gate per network (sync, relay and public channels off on mainnet), the 24 h Mutinynet soak is running and its first 20 min are recorded in `MUTINYNET.md` (NL-376). **D12 stays closed on mainnet** until the soak is evaluated and NL-373 is settled. Proof G5 so far: flood, rate-limit and cap tests, the 200k SQLite measurement, `scripts/run-gossip.sh 3` 3 x 24/24; the 24 h soak is pending. Separately the BOLT 5 O6-T4 gate opened in this wave (HTLCs on for every network, `BOLT5_ONCHAIN_PLAN.md`).
- Status after wave G-C (superseded by the line above; `wip/fafo` @ `4dc0f77`): waves G-A, G-B and G-C are done (see the wave records in §5). G0-G4 are complete and wired (open: Docker Proof G4 (b)/(c), NL-367, and B7-CU-01b): gossip queries answered from the graph (`QueryResponder`), sync by queries (`GossipSyncManager`, `gossip_queries_ex` advertised Optional), relay of other nodes' gossip with per-peer filters through the `PeerOutbox`, re-query of dropped SCIDs, graph routes in `PaymentService` (`MissionControl`, `GraphPathSource`, graph as a third candidate source), `getroute` (`ClientCommand` 19), invoices without route hints once an announced channel can receive, and G1-T5's offline disable (NL-349). The goal is proven in Docker: paying and getting paid over public channels without route hints against LND 0.20 (goal proofs (b)-(d), plus Proofs G1 (d), G2 (d), G3 (a)-(c)) and CLN v26.06.8 (Proof G3 (d) and goal proof (e)); goal proof (a), LND's graph showing our public channel and our node_announcement, is Proof G1 (a) from wave G-B (alice's `GetChanInfo` has both policies; bob's `DescribeGraph` has the channel and `GetNodeInfo` our alias and color). Next: wave G-D (G5 hardening and the mainnet gate D12, where sync and relay stay off by default) and the BOLT 5 O6-T4 gate decision.
- Status after wave G-B (superseded by the lines above; `wip/fafo` @ `5bbfbb5`): waves G-A and G-B are done (see the wave records in §5). G0 and G2 are complete and G1 is complete except G1-T5's disable-when-offline policy (NL-349), all wired: public channels (`openchannel --public`), `announcement_signatures`, our `channel_announcement`/public `channel_update`/`node_announcement` relayed to connected peers, the graph (`GossipIngress`, `GraphStore`, `GraphPruner`) with `listnodes`/`listgraphchannels`, and Docker Proofs G0, G1 (a)-(c) and G2 (a)-(c) green against LND 0.20. G4-T1 exists as a library. Next: wave G-C (C1 sync/relay G3-T1..T4, C2 routing G4-T2..T4 + G3-T5, C3 proofs), plus NL-348 (switch refuses the real SCID of a public channel with option_scid_alias Optional), which blocks routing through us.
- Status after wave G-A (superseded by the lines above; `wip/fafo` @ `164289a`): wave G-A is done (see "Gossip wave G-A record" in §5). G0 complete (G0-T1..T5), G1-T1 storage half and G1-T2, G2-T1..T3 and G4-T1 landed as library code with tests; **nothing is wired into the node yet**: 256/257 are typed and dropped, 259 reaches `ChannelManager` and gets a channel-scoped "not supported yet" warning until G1-T3. Next: wave G-B (B1 public channels, B2 graph ingress/store/pruner/IPC, B3 Docker proofs).
- Status before wave G-A (superseded by the lines above; `wip/fafo` @ `368a057`, ABCD wave 7 in progress, with uncommitted W7 changes in the main checkout):
  - Nothing of G0-G5 exists beyond the W1-E subset.
  - `channel_announcement` (256), `node_announcement` (257) and `announcement_signatures` (259) are parsed as raw bytes and dropped.
  - Queries get empty replies (`full_information=0`).
  - No graph, no pathfinding.
- **Out of scope:**
  - gossip v1.5 / taproot gossip (`channel_announcement_2` and related, not merged in BOLT 7 master);
  - splice `announcement_signatures` (we have no splicing);
  - zlib encoding 1 (the spec now says it "MUST NOT be used");
  - `option_zeroconf` public channels.

---

## 0. How to use this document (agents)

1. **Order of work.** Do the milestones in order: **G0, then G1 and G2 (in parallel lanes), then G3, then G4, then G5.** G4-T1 (the pure pathfinder) may start in parallel with G2, because it only needs the Domain graph read model from G2-T1.
2. **Definition of done for a task.** All of these must pass:
   - `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121` and `-c Release.Native` (the signature code is crypto code);
   - `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`;
   - `dotnet test --no-build -c Release --filter 'FullyQualifiedName!~Docker'`;
   - the task's own tests.
   Every signature path is checked against a captured LND or CLN message (§6.2) as well as against our own round trip.
3. **Definition of done for a milestone:** its **Proof** passes.
   - Docker proofs live in `test/NLightning.Integration.Tests/Docker/Gossip/`, collection `GossipRegtestCollection` (`DisableParallelization = true`, its own `LightningRegtestNetworkFixture`).
   - They run in their own process through a new `scripts/run-gossip.sh` (a copy of `scripts/run-onchain.sh`). The reason: public channels change the shared LND nodes' graph for every later test, and the fixture needs extra LND flags (§5 Proof conventions).
   - Run them with the in-container runner (`--network host`, NL-276; `test/CLAUDE.md` line 55).
   - The CLN proofs go in `Docker/Interop/Cln/ClnGossipTests` on the existing `ClnFixture`.
4. **Schema.** One migration per wave, all three providers, through `src/NLightning.Infrastructure.Persistence/scripts/add_migration.sh <Name>` (root `CLAUDE.md` › "Add an EF migration"). Only the wave's migration-owner lane touches `Infrastructure.Persistence*` and `Infrastructure.Repositories`.
5. **DI.** Gossip services go in `Application/Gossip/GossipServiceCollectionExtensions.AddGossipServices()` (already called from `AddApplicationServices`). Bitcoin-side lookups go in `AddBitcoinInfrastructure`. Nothing is mirrored by hand: the Docker node uses `AddNltgNodeServices`.
6. **Safety and privacy gates:**
   - Channels stay private by default (`openchannel --public` is opt-in, and fundee acceptance of public channels is governed by `Gossip:AcceptPublicChannels`).
   - Graph sync and relay default **on for regtest/signet/mutinynet and off for mainnet** until Proof G5 passes (§4 D12).
7. **Commit shape:** one task per commit, lowercase imperative subject, citing the NL ID and the task ID (e.g. `(NL-099 / G2-T3)`).

---

## 1. Spec requirements summary

Requirement IDs (`B7-…`) are used by the tasks and by the traceability matrix (§6).

### 1.1 announcement_signatures (259)
- **B7-AS-01** MUST send it once `channel_ready` was received **and** the funding tx has enough confirmations (6) to be safe from reorgs, but only if `announce_channel` was set and no `shutdown` was sent (MUST NOT otherwise).
- **B7-AS-02** On reconnection, if the peer's `announcement_signatures` was not received yet, MUST send ours again. When the peer's arrives, MUST respond with ours (retransmission).
- **B7-AS-03** Receiver:
  - SHOULD send a `warning` if `short_channel_id` does not match the funding tx;
  - on invalid signatures MAY send a `warning` and close the connection, or send an `error` and fail the channel;
  - SHOULD defer handling if `channel_ready` has not been sent yet.
- **B7-AS-04** With both valid signatures and 6 confirmations, SHOULD queue the `channel_announcement` for broadcast.

### 1.2 channel_announcement (256)
- **B7-CA-01** Fields: `node_signature_1/2`, `bitcoin_signature_1/2`, `u16 len features`, `chain_hash`, `short_channel_id`, `node_id_1/2`, `bitcoin_key_1/2`.
  - `node_id_1` is the lexicographically lesser (compressed) key.
  - The bitcoin keys are the matching `funding_pubkey`s.
  - The signed hash is the double-SHA256 of the message from offset 256 (after the four signatures) to the end.
- **B7-CA-02** Sender MUST NOT send it before 6 confirmations. The funding output MUST be P2WSH.
- **B7-CA-03** Receiver:
  - MUST verify all four signatures (on failure SHOULD send a `warning`, MAY close the connection);
  - SHOULD send a `warning` if `node_id_1 >= node_id_2`;
  - MUST ignore it for an unknown `chain_hash` or unknown even features;
  - MUST ignore it if the output is not a P2WSH of the two bitcoin keys or is spent;
  - SHOULD ignore it below 6 confirmations (MAY accept it close to the threshold).
- **B7-CA-04** SHOULD blacklist the node ids of a *different* announcement for the same funding outpoint. SHOULD ignore blacklisted nodes.
- **B7-CA-05** SHOULD store a new announcement and queue it for rebroadcast. SHOULD NOT rebroadcast once the output is spent. SHOULD forget the channel **72 blocks** after the spend (splice-aware delay).

### 1.3 node_announcement (257)
- **B7-NA-01** Fields: `signature`, `features`, `timestamp`, `node_id`, `rgb_color[3]`, `alias[32]` (UTF-8, zero padded), `addresses`. The signature covers the double-SHA256 of everything after it.
- **B7-NA-02** Sender rules:
  - `timestamp` strictly increasing;
  - address descriptors in ascending type order, none of type 0, no port 0;
  - at most one DNS (type 5) descriptor;
  - SHOULD NOT announce Tor v2.
  Address lengths: type 1 IPv4 6 bytes, 2 IPv6 18, 3 Tor v2 12 (deprecated), 4 Tor v3 37, 5 DNS `1 + len + 2`.
- **B7-NA-03** Receiver:
  - `node_id` not a valid point: SHOULD send a `warning`;
  - bad signature or unknown `node_id` (no announced channel): MUST NOT process further;
  - SHOULD ignore it if not newer;
  - SHOULD ignore the first unknown descriptor type and everything after it, and ignore port-0 descriptors;
  - SHOULD send a `warning` for a bad `addrlen`;
  - more than one DNS descriptor: ignore the extra ones and MUST NOT forward;
  - SHOULD queue a newer one for rebroadcast.
- **B7-NA-04** Nodes with unknown even features: SHOULD NOT connect, MUST NOT route through, MUST NOT pay unless the invoice allows.

### 1.4 channel_update (258)
- **B7-CU-01** Sender rules (already implemented for private channels, W1-E):
  - `must_be_one`;
  - `dont_forward` for private channels (and never forward those);
  - `direction` 0 for `node_id_1`;
  - `htlc_maximum_msat` at most the capacity and at most `max_htlc_value_in_flight_msat`, and at least `htlc_minimum_msat`;
  - strictly increasing `timestamp`;
  - SHOULD NOT create redundant updates;
  - SHOULD keep accepting the old parameters for 10 minutes (**B7-CU-01b**).
- **B7-CU-02** Receiver:
  - MUST ignore it for a channel it has no announcement for, unless the channel is one of its own (SHOULD accept those);
  - MUST ignore it once the output is spent, unless `disable` is set;
  - bad signature: SHOULD send a `warning` and close, MUST NOT process;
  - unknown chain: ignore;
  - same timestamp with different fields: MAY blacklist; same timestamp and same fields, or older: SHOULD ignore;
  - MAY discard a timestamp far in the future;
  - otherwise SHOULD queue for rebroadcast.
- **B7-CU-03** Routing: ignore a channel with `htlc_maximum_msat < htlc_minimum_msat` or with a max above the capacity (MAY blacklist the node). SHOULD respect `htlc_maximum_msat`.

### 1.5 Gossip queries (261-265)
- **B7-Q-01** `query_short_channel_ids`:
  - SHOULD NOT be sent to a peer without `gossip_queries`;
  - at most one outstanding query;
  - encoding 0 only (**1 MUST NOT be used**);
  - SHOULD NOT query spent channels;
  - MAY query on a `channel_update` whose announcement is unknown.
- **B7-Q-02** Receiver of `query_short_channel_ids`:
  - bad encoding or flags: MAY `warning` + close;
  - for each known SCID: the `channel_announcement`, then its latest updates, then the `node_announcement`s (or exactly what `query_flags` bits 0-4 ask for), avoiding duplicate `node_announcement`s;
  - SHOULD NOT wait for the flush;
  - then `reply_short_channel_ids_end` with `full_information` = 1 when we keep up-to-date information for the chain, else 0.
- **B7-Q-03** `query_channel_range`: `number_of_blocks >= 1`; at most one outstanding query; optional `query_option` (bit 0 timestamps, bit 1 checksums).
- **B7-Q-04** Receiver of `query_channel_range`: one or more `reply_channel_range`, each fitting one message.
  - First reply: `first_blocknum <= query.first` and `first + number > query.first`.
  - Later replies: non-decreasing `first_blocknum`.
  - Last reply covers `query.first + query.number` and has `sync_complete = 1`; every other reply has `sync_complete = 0`.
  - MAY add `timestamps_tlv` and `checksums_tlv` (CRC32C of the update without its signature and timestamp).
- **B7-Q-05** `gossip_timestamp_filter` (receiver side):
  - SHOULD relay only gossip with `first_timestamp <= ts < first_timestamp + range`;
  - SHOULD send our own gossip regardless of the filter;
  - MUST NOT send a `channel_announcement` without an update, and it goes before its updates and `node_announcement`s;
  - the timestamp of a `channel_announcement` is that of its update(s);
  - SHOULD NOT send spent channels.
- **B7-Q-06** Sender of a filter to a peer that does not offer `gossip_queries`: SHOULD use `0xFFFFFFFF`/0.

### 1.6 Relay, sync, pruning, routing
- **B7-RL-01** MUST NOT send gossip it did not generate itself until the peer sent `gossip_timestamp_filter`. SHOULD flush outgoing gossip every 60 s (staggered). Don't send a message back to the peer we received it from **(inferred from common practice; not a numbered MUST)**. SHOULD NOT forward to a peer whose `init.networks` excludes the chain.
- **B7-PR-01** SHOULD monitor funding outputs. Once spent and 72 blocks deep, SHOULD remove the channel. MAY prune nodes without channels.
- **B7-PR-02** MAY prune (and ignore) a channel whose latest update in both directions is older than 2 weeks (1,209,600 s). This is a local policy and MUST NOT be enforced by forwarding peers.
- **B7-RT-01** Consider fees and `cltv_expiry_delta`. It is highly desirable to add a random CLTV offset (shadow route).
- **B7-FEE-01** SHOULD accept HTLCs paying `fee_base + amt*ppm/1e6`, and the old fee for some time after an update.
- **B4-CU-01 (BOLT 4, overrides the brief)** The origin MUST NOT apply the `channel_update` of a failure to the local graph or relay it. It MAY use it only to retry the failed payment. It is already kept per payment in `RouteConstraints` (`src/NLightning.Application/Payments/Routing/RouteConstraints.cs:10-13`). **The brief's "update graph from channel_update in failures" is therefore not implemented as asked** (D9): failures update mission control, and may trigger a `query_short_channel_ids` to fetch the signed update through gossip.

### 1.7 BOLT 2 / 9 / 11 facts
| Item | Rule |
|---|---|
| `channel_flags` bit 0 | `announce_channel`. The receiver MAY fail the channel if it is 0 but the receiver wants a public one. With `announce_channel` set, MUST NOT use `option_scid_alias` in `channel_type`, and SHOULD set the `channel_ready` alias to a value unrelated to the real SCID. |
| Feature bits | `gossip_queries` 6/7 (no context listed; we set Optional), `gossip_queries_ex` 10/11 (IN), `option_scid_alias` 46/47. `initial_routing_sync` is gone. |
| BOLT 11 `r` | Unchanged. Graph routes combine with hints when the payee is private (G4-T3). |

---

## 2. Current state (verified at `368a057`)

### 2.1 What exists
- **Wire:**
  - `MessageTypes` 256-265 (`src/NLightning.Domain/Protocol/Constants/MessageTypes.cs:64-72`).
  - 256, 257 and 259 are `GossipMessage` subclasses carrying the opaque `GossipPayload` (`Domain/Protocol/Messages/GossipMessage.cs`, `Payloads/GossipPayload.cs`), serialized by `GossipMessageTypeSerializer<T>` and registered with `RegisterGossipSerializer` (`src/NLightning.Infrastructure.Serialization/Factories/MessageTypeSerializerFactory.cs:139-148`).
  - 258 is typed: `ChannelUpdatePayload` is the single codec (`Parse`, `GetSignedData`, `GetSignatureHash`, and unknown trailing bytes are kept for the signature; `Domain/Protocol/Payloads/ChannelUpdatePayload.cs:17-31`). **G0 copies this pattern.**
  - 261-265 are typed (`Query*/Reply*/GossipTimestampFilter*`, with `GossipQueryFieldSerializer`).
- **Signing:**
  - `ILightningSigner.SignNodeMessage(Hash)` and `VerifyNodeMessage(hash, sig, pubkey)` (`src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs:46,57`). Verification normalizes high-S signatures (`src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs:178-192`) and works for any pubkey, so bitcoin keys too.
  - No method signs with the channel's **funding** key over an arbitrary hash.
- **Peer dispatch** (`src/NLightning.Infrastructure/Node/Services/PeerService.cs:320-358`):
  - 263 and 261 are answered by `GossipQueryResponder` (`src/NLightning.Infrastructure/Node/Services/GossipQueryResponder.cs:38-68`: one `reply_channel_range` with `sync_complete=1` and no ids; `reply_short_channel_ids_end` with `full_information=0`; encoding and flag validation, with a warning on bad input);
  - 258 is raised to `OnChannelUpdateReceived` (buffer of 64);
  - 265 is ignored;
  - 256, 257, 259, 262 and 264 are dropped.
- **channel_update service** (`src/NLightning.Application/Gossip/Services/ChannelUpdateService.cs`):
  - always sets `dont_forward` (`:335-340`);
  - uses `RemoteAlias` for scid_alias channels (`:271-290`);
  - checks and keeps the peer's update **in memory only** (`:73`, `:168-220`);
  - resends on every connection (`:151-165`).
  `PeerManager` wires it (`src/NLightning.Application/Node/Managers/PeerManager.cs:642,718,1044`). `PeerOutbox.TryEnqueueGossip` is the send path.
- **Features:** `GossipQueries` defaults to Optional, `ExpandedGossipQueries` to No (`src/NLightning.Domain/Node/Options/FeatureOptions.cs:70,81`). `ScidAlias` defaults to No (`:170`).
- **Channel flags:**
  - `OpenChannelClientHandler` always sends `ChannelFlag.None` (`src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs:110-112`).
  - The validator rejects `announce_channel` together with scid_alias for incoming opens (`src/NLightning.Domain/Channels/Validators/ChannelOpenValidator.cs:182-191`; the flags come from `ChannelOpenMandatoryValidationParameters.cs:42`).
  - The flag is **not stored** on the channel (`ChannelEntity` and `ChannelConfigEntity` have no such column).
  - `OpenChannelIpcRequest` uses keys 0, 2 and 3 (`src/NLightning.Transport.Ipc/Requests/OpenChannelIpcRequest.cs`).
- **SCID:** `ShortChannelId` (`BlockHeight`, 24-bit `TransactionIndex`, `OutputIndex`; `src/NLightning.Domain/Channels/ValueObjects/ShortChannelId.cs:15-19`), persisted with `ShortChannelIdConverter`. Aliases are persisted (NL-209).
- **Chain:**
  - `IBitcoinChainService` has `GetBlockHashAsync(height)`, `GetBlockAsync(height|hash)` (full NBitcoin `Block`) and `GetUnspentOutputAsync(OutPoint)` (`gettxout` with the mempool; `src/NLightning.Infrastructure.Bitcoin/Wallet/Interfaces/IBitcoinChainService.cs:27`, `BitcoinChainService.cs:131-146`).
  - There is no txid-list-by-height call.
  - `BlockchainMonitorService` walks every input of every block (`src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs:882-884`) and raises `OnNewBlockDetected(height, hash)` without the block (`:945-960`), plus `OnBlockDisconnected` (`:1082`).
- **Routing:**
  - `PaymentRoutePlanner.BuildPaths` builds candidate paths only from direct channels and invoice hints (`src/NLightning.Application/Payments/Routing/PaymentRoutePlanner.cs:301-372`). A path is `IReadOnlyList<RoutingInfo>` (node, scid, fee base, ppm, CLTV delta; `src/NLightning.Domain/Models/RoutingInfo.cs`), which has **no htlc_min/max**.
  - Per-payment learning is `RouteConstraints` (exclusions, overrides, liquidity bounds).
- **TODOs:** "Update routing tables" at `Application/Channels/Handlers/ChannelReadyMessageHandler.cs:137-138` and `FundingConfirmedMessageHandler.cs:95-96` (NL-054).
- **Persistence:** the latest migration is `AddAttributionData` (SQLite folder listing; wave 7). No graph tables.
- **IPC:** `ClientCommand` ends at `PendingSweeps = 15` (`src/NLightning.Domain/Client/Enums/ClientCommand.cs:27`), so the next free value is **16** (re-check at implementation time, since wave 7 may add some).
- **Docker:**
  - The LND fixture has alice–bob, bob–alice, carol–alice and carol–bob 10M-sat channels, and david with none (`test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs:87-126`). Whether LNUnit opens them as **public** is **unverified** (LND's default is public).
  - Extra LND flags are added through `Builder.Configuration.LNDNodes…Cmd.Add` (`:97`).
  - The CLN fixture is separate (`test/CLAUDE.md` §CLN interop).
- **Metrics:** none exist in the repo (`System.Diagnostics.Metrics` is not used anywhere).

### 2.2 Gaps that block BOLT 7
| # | Gap | Evidence | NL | Gate |
|---|---|---|---|---|
| GG1 | 256, 257 and 259 are untyped; nothing verifies them | `GossipMessage.cs`, `PeerService.cs:353-358` | NL-099 | G0-T1..T3 |
| GG2 | 259 is a **channel** message (`channel_id`) but is dispatched as peer gossip and dropped: no `ChannelManager` case, no lock | `PeerService.cs:353` | NL-342 (routed in G-A, handler open) | G0-T2, G1-T3 |
| GG3 | No way to request a public channel. The fundee ignores `announce_channel` (not stored), so an LND-opened **public** channel is silently treated as private and LND never gets our `announcement_signatures` | `OpenChannelClientHandler.cs:110-112`; no `ChannelFlags` use in `Application/Channels/Handlers/OpenChannel1MessageHandler.cs` (grep) | NL-236 + NL-341 (storage done in G-A) | G1-T1 |
| GG4 | No funding-key signature API for `bitcoin_signature` | `ILightningSigner.cs` | closed in G-A (`SignChannelAnnouncement`, 709030c) | G1-T2 |
| GG5 | Address descriptors: Tor v3 decodes 36 bytes, DNS length off by one; only IPv4 is tested | `src/NLightning.Infrastructure/Protocol/Tlv/Converters/RemoteAddressTlvConverter.cs:51-54,95-103` | NL-008 (fixed in G-A) | G0-T4 |
| GG6 | Our updates always set `dont_forward`; a public channel needs it clear and the real SCID | `ChannelUpdateService.cs:335-340` | new | G1-T5 |
| GG7 | The peer's updates live in memory only and are never relayed | `ChannelUpdateService.cs:52-53,73` | NL-099 | G2-T4 |
| GG8 | No SCID → funding-output lookup (height → txid list → `gettxout`) | `IBitcoinChainService.cs` | closed in G-A (`FundingOutputLookup`, 79debc3, c1bb630) | G2-T2 |
| GG9 | Query replies are always empty with `full_information=0` | `GossipQueryResponder.cs:11-15` | NL-205 note | G3-T1 |
| GG10 | Path candidates come from hints only; `RoutingInfo` has no HTLC limits | `PaymentRoutePlanner.cs:301-372` | new | G4-T3 |
| GG11 | `init.networks` of the peer is not consulted before forwarding gossip **(unverified: check `PeerService` init handling)** | — | new | G3-T3 |

---

## 3. Design

### 3.1 Layering
- **Domain** (`src/NLightning.Domain/Gossip/`, new, BCL only):
  - **Payload codecs** (in `Domain/Protocol/Payloads/`, the `ChannelUpdatePayload` pattern): `ChannelAnnouncementPayload`, `NodeAnnouncementPayload`, `AnnouncementSignaturesPayload`. Each has `Parse`, `GetBytes`, `GetSignedData`, `GetSignatureHash`, and keeps unknown trailing bytes.
  - **`AddressDescriptor`** (type, bytes, port) with a strict codec (G0-T4). It is shared with `RemoteAddressTlv` (NL-008).
  - **Pure validation:** `GossipValidator` returns `Accept | Ignore(reason) | Warn(reason, close)` for the spec checks that need no crypto or chain (ordering, chain hash, even features, timestamps, `htlc_max` vs `htlc_min`, descriptor rules, the 2-week policy).
  - **Graph read model:** `GraphNode`, `GraphChannel` (scid, node ids, bitcoin keys, capacity, features, spent height), `GraphPolicy` (per direction). Plus `IGraphView`, a read-only snapshot used by pathfinding.
  - **Pathfinding:** `Domain/Routing/Pathfinding/` (pure: `GraphPathfinder`, `PathCostModel`, `LiquidityEstimates`).
  - **Ports:**
    - `IGossipSignatureVerifier` (batch-friendly verify of compact sigs over a hash);
    - `IFundingOutputLookup` (`TryGetAsync(scid) → (amountSat, scriptPubKey, spent?)`);
    - `IGraphDbRepository` (an `IUnitOfWork` property);
    - `IGossipRelay` (enqueue validated messages).
- **Infrastructure.Bitcoin:**
  - `Gossip/FundingOutputLookup` (§3.4);
  - `Signers/LocalLightningSigner.SignChannelAnnouncement` (§3.5);
  - `Gossip/GossipSignatureVerifier` (the high-S normalization of `VerifyNodeMessage`, factored out and shared);
  - `BlockchainMonitorService` raises a new `OnBlockInputs(height, IReadOnlyList<OutPoint>)` from the scan it already does (`:882-884`). It is an allocation-light list, raised after the block's save.
- **Infrastructure:**
  - `PeerService` routes 256/257/258 to a peer-level `OnGossipReceived` event, 259 as a channel message, and 261-265 to the sync service. `GossipQueryResponder` is deleted and replaced by `Application/Gossip/Sync/QueryResponder` (it needs the graph).
- **Application** (`src/NLightning.Application/Gossip/`):
  - `Announcements/ChannelAnnouncementService`: our public channels (G1);
  - `Announcements/NodeAnnouncementService`;
  - `Graph/GossipIngress`: a bounded queue → validate → `GraphStore`;
  - `Graph/GraphStore`: a singleton in-memory graph, persisted write-behind, loaded at startup;
  - `Graph/GraphPruner`;
  - `Sync/GossipSyncManager` (queries, filters, sync peers), `Sync/QueryResponder`;
  - `Relay/GossipRelayScheduler`: 60 s staggered flush, per-peer filter and dedupe;
  - `Routing/GraphPathSource`: bridges `IGraphView` + `MissionControl` → `PaymentRoutePlanner`;
  - `Routing/MissionControl`.
  `ChannelUpdateService` stays the owner of our own updates and gains public mode.
- **Persistence/Repositories:** the tables of §3.6, one migration per wave.
- **Daemon/Client/Transport.Ipc:** `openchannel --public`, `listnodes`, `listgraphchannels`, `describegraph`, `getroute` (§3.9).

### 3.2 Public channel lifecycle (G1)
1. **Open.**
   - As initiator, `openchannel --public` sets `ChannelFlag.AnnounceChannel`, and `ChannelParams.ToChannelType()` drops `option_scid_alias` (BOLT 2). A request with both public and zeroconf is refused.
   - As fundee, `OpenChannel1MessageHandler` stores `open_channel.channel_flags`. `Gossip:AcceptPublicChannels` (default true) decides; if false, we fail with an `error` (BOLT 2 MAY).
   - The flag is persisted as `ChannelConfigEntity.AnnounceChannel` (bool).
2. **`channel_ready`:** unchanged. For a public channel, a `channel_ready` alias is sent only if `ScidAlias` is negotiated (the alias is unrelated to the real SCID by construction, `FundingConfirmedMessageHandler`).
3. **Announce depth:**
   - `ChannelAnnouncementService` subscribes to `OnNewBlockDetected`. For every Open public channel with `tip − scid.BlockHeight + 1 >= 6` (`Gossip:AnnouncementDepth`, fixed at 6 outside regtest), where `channel_ready` was received and no `shutdown` was sent or received, it takes the channel lock and builds the unsigned `channel_announcement` (node and bitcoin key ordering).
   - It asks the signer for `(node_sig, bitcoin_sig)`, persists `LocalAnnouncementSigsSentAt`, and enqueues `announcement_signatures` through `ChannelManager.OnResponseMessageReady` (outbox order).
   - Repeated on every reconnection until the peer's signatures are stored (B7-AS-02), and sent once more as a reply when the peer's signatures arrive.
4. **Receive 259** (`AnnouncementSignaturesMessageHandler`, a channel handler under the lock):
   - checks the state (Open, not shutting down) and the SCID (== `channel.ShortChannelId`, else `warning`);
   - verifies both signatures against the rebuilt announcement hash (bad: `ChannelWarningException { CloseConnection = true }`, the MAY option we pick; the channel is never failed over gossip);
   - stores the peer's signatures (`Channels.RemoteAnnouncementNodeSig/BitcoinSig`);
   - if we are below the announce depth or have not sent `channel_ready`: store and defer (B7-AS-03).
5. **Complete:**
   - With both signature pairs, assemble the full 256.
   - `GraphStore.AddOwnChannel` (no chain lookup needed for our own channel).
   - Queue the 256, our now-public `channel_update` (`dont_forward` = 0, real SCID) and our `node_announcement` for the next flush, sent to every gossip peer regardless of filters (B7-Q-05 own gossip).
6. **Node announcement:**
   - Sent only once we have at least one announced channel (B7-NA-03: others ignore it otherwise).
   - Contents: `Node:Alias` (UTF-8, at most 32 bytes, zero padded, validated at startup), `Node:Color` (hex RGB), `Gossip:AnnounceAddresses` (default empty; `ListenAddresses` are **not** announced automatically, for privacy), and our node features (`FeatureSet` with N-context bits; `GetWireBytes`, NL-112).
   - Re-signed with a new timestamp when any field changes (checked at startup) and at least every 13 days as a keep-alive **(LND/CLN practice, unverified)**, so we are never pruned as stale.
7. **Close:** once the funding output is spent, the channel is marked spent in the graph at the spend height, dropped from relay, and removed at +72 blocks (B7-CA-05, via G2-T5).

### 3.3 Incoming gossip pipeline (G2)
`PeerService` → `GossipIngress.Enqueue(peer, message)` (bounded per peer, §3.8) → worker (N = `Environment.ProcessorCount/2`) → validation stages:

1. **Parse and pure checks** (`GossipValidator`).
2. **Dedupe:** an exact-bytes hash set (a recent-message cache) and "not newer than stored".
3. **Signatures** (`IGossipSignatureVerifier`: 4 for a 256, 1 for 257/258).
4. **Chain** (256 only):
   - `IFundingOutputLookup` → the output exists, is unspent, and its script equals `P2WSH(2 <k1> <k2> 2 OP_CHECKMULTISIG)` with the keys sorted as BOLT 3 does;
   - the amount becomes the capacity;
   - confirmations >= 6 (`scid.BlockHeight <= tip − 5`).
5. **Apply** to `GraphStore` under a single-writer lock (the graph is not channel state, so channel locks are not involved).
   - 258 for an unknown SCID goes to a bounded **orphan** cache (keyed by SCID, max 10,000, TTL 10 min), replayed when the 256 arrives. G3 may also query the SCID (B7-Q-01).
   - 257 for a node without channels is dropped (B7-NA-03).
   - Our own channels' 258 from the peer still go through `ChannelUpdateService.HandleRemoteChannelUpdate` first (private-channel path unchanged), then into the graph if the channel is public.
6. **Result:**
   - `Accepted` → `IGossipRelay.Enqueue(message, originPeer)`.
   - `Warn` → `IPeerService` warning; close when the spec says so. A signature failure closes, per B7-CU-02 and B7-CA-03.
   - A per-peer **misbehaviour score** (invalid signatures, bad encodings) disconnects at a threshold and bans the peer for 1 h (G5-T2).

### 3.4 Funding-output verification (D3)
- `FundingOutputLookup.TryGetAsync(scid)`:
  1. `getblockhash(scid.BlockHeight)`;
  2. `getblock <hash> 1` (verbosity 1: txids only, about 64 hex characters per tx) via `RPCClient.SendCommandAsync`, kept in an LRU cache of txid lists by height (default 256 heights), invalidated on `OnBlockDisconnected`;
  3. `txid = list[scid.TransactionIndex]` (out of range: ignore);
  4. `GetUnspentOutputAsync(txid:vout)` (existing; `null` = spent or unknown).
  No txindex is needed. Concurrency is bounded by `Gossip:ChainLookupConcurrency` (default 4) and rate by `Gossip:ChainLookupsPerSecond` (default 50) to protect bitcoind's `rpcworkqueue` (default 16) **(unverified default)**.
- **Pruned bitcoind:** `getblock` fails for pruned heights. `Gossip:FundingValidation = Full | SkipUnavailable` (default `Full`). With `SkipUnavailable`, an unavailable block marks the channel `Unverified`: kept for routing with a probability penalty, never relayed.
- **Cost (inferred):** on mainnet, roughly 50k public channels over about 35k distinct heights, so an initial sync pulls a few GB of JSON over hours. That is acceptable at the rate limit; the relay waits for validation. Regtest/signet cost is trivial.
- **Spent detection:** `GraphPruner` keeps a `HashSet<OutPoint>` of every graph funding outpoint (rebuilt from the database at startup) and checks `OnBlockInputs` against it. That is O(inputs) per block, with no bitcoind calls and no watch rows (D4).

### 3.5 Signer additions
```csharp
// Signs our half of a channel_announcement. The signer re-parses the unsigned announcement and refuses unless it names
// our node id, the channel's funding pubkey, the channel's real SCID and our chain (no blind hash signing, so a
// remote signer can enforce the same rule later).
(CompactSignature NodeSignature, CompactSignature BitcoinSignature) SignChannelAnnouncement(
    ChannelId channelId, ReadOnlyMemory<byte> unsignedAnnouncement, ShortChannelId shortChannelId);
```
`node_announcement` keeps using `SignNodeMessage` (its hash covers only our own data). Low-S, RFC 6979, and the funding key is wiped after use, like `SignNodeMessage` (`LocalLightningSigner.cs:149-176`).

### 3.6 Data model (migration-owner lane; all three providers)
| Migration (wave) | Changes |
|---|---|
| `AddGossipGraph` (wave G-A) | `GraphNodes` (PK `NodeId` 33 bytes; `Timestamp`, `Features` blob, `Alias` 32 bytes, `Color` 3 bytes, `Addresses` blob, `RawAnnouncement` blob, `ReceivedAt`). `GraphChannels` (PK `ShortChannelId` with `ShortChannelIdConverter`; `NodeId1`, `NodeId2`, `BitcoinKey1`, `BitcoinKey2`, `CapacitySat`, `Features`, `RawAnnouncement`, `Verification` byte (Verified/Unverified/Own), `SpentAtHeight?`, `ReceivedAt`; indexes on `NodeId1`, `NodeId2`, `SpentAtHeight`). `GraphChannelPolicies` (PK `ShortChannelId, Direction`; `Timestamp`, `MessageFlags`, `ChannelFlags`, `CltvExpiryDelta`, `HtlcMinimumMsat`, `HtlcMaximumMsat`, `FeeBaseMsat`, `FeePpm`, `RawUpdate`). `GraphBannedNodes` (PK `NodeId`; `Reason`, `Until`). **Channel columns:** `ChannelConfigs.AnnounceChannel` bool (backfill false); `Channels.RemoteAnnouncementNodeSig?`, `RemoteAnnouncementBitcoinSig?`, `LocalAnnouncementSigsSentAt?`. |
| `AddGossipIndexes` (wave G-D, only if G5 measurements need it) | covering index for `GraphChannelPolicies.Timestamp` (timestamp-filter scans) |

- Raw signed bytes are stored because relay and query replies must forward them byte-exact (unknown trailing fields are signed).
- `ChannelRoundTripTests` must cover the four new channel fields. Postgres and SqlServer round trips must cover the graph tables.
- Mission control stays in memory (D8).

### 3.7 Sync and relay (G3)
- **Sync peers:** up to `Gossip:SyncPeers` (default 3) peers offering `gossip_queries`, channel peers first.
  1. Once per connection: `query_channel_range(0, tip+1)` (with `query_option` timestamps when both sides offer `gossip_queries_ex`).
  2. Collect SCIDs from all replies (enforcing B7-Q-04 on the peer: monotonic, covering; a violation gets a warning and ends the sync).
  3. Diff against the graph (unknown SCIDs, or newer timestamps when known).
  4. `query_short_channel_ids` in batches of 8,000 SCIDs (fits 65,535 bytes: 1 + 8×8000 + header), one outstanding query per peer (B7-Q-01).
  5. Then `gossip_timestamp_filter(now − 2 weeks, 0xFFFFFFFF)`.
  Other gossip peers get `gossip_timestamp_filter(now, 0xFFFFFFFF)` (new gossip only). A peer without `gossip_queries` gets `(0xFFFFFFFF, 0)` (B7-Q-06). Every 20 min one sync peer is rotated and re-runs the range query (historical sync, like LND **(unverified LND interval)**).
- **Serving:** `QueryResponder` answers from the graph snapshot.
  - `reply_channel_range` chunked by `Gossip:MaxScidsPerReply` (default 8,000; each message at most 65,535 bytes), adding `timestamps_tlv`/`checksums_tlv` (CRC32C via `System.Numerics.BitOperations.Crc32C`) when asked and `gossip_queries_ex` is offered.
  - `query_short_channel_ids` replies honour `query_flags`, suppress duplicate node announcements within one reply, are sent directly (not at the flush), then `full_information = 1` once our initial sync with at least one peer completed, else 0.
  - Encoding 1 gets a warning (already) and is never sent.
- **Relay:** `GossipRelayScheduler`.
  - Accepted messages go into a pending set keyed by (type, scid/node, direction), so a newer message replaces an older one.
  - Every 60 s, with a per-peer phase offset (staggered): for each peer that sent `gossip_timestamp_filter`, send the pending messages whose timestamp is in its filter, except to their origin peer. A 256 goes first, and only if an update for it exists.
  - Our own messages go to all peers at the next flush, regardless of filter (B7-Q-05). Peers whose `init.networks` lacks our chain get nothing (GG11).
  - A new filter triggers a one-shot backlog send from the graph (lazy, paced at `Gossip:BacklogMessagesPerSecond` = 1,000 per peer).
  - Sends go through `PeerOutbox.TryEnqueueGossip`. It is bounded: a peer over `Gossip:MaxOutboundQueue` (5,000) gets the rest of the backlog dropped (it can re-query).

### 3.8 DoS limits (G2 enforces the basics, G5 tunes)
| Limit | Default |
|---|---|
| inbound gossip queue per peer | 2,000 messages; excess dropped with a debug log |
| global validation queue | 20,000 |
| channel_update rate per (scid, direction) | 1 accepted per 60 s, burst 4; a keep-alive (same fields) is accepted only if more than 24 h newer **(LND-like, unverified)** |
| node_announcement rate per node | 1 per 10 min |
| orphan channel_updates | 10,000, TTL 10 min |
| graph size | `MaxChannels` 200,000, `MaxNodes` 100,000; beyond that, new channels are rejected (logged, metric) |
| future timestamps | more than 14 days ahead dropped (reuses `ChannelUpdateService.MaxFutureTimestamp`) |
| misbehaviour | 5 invalid signatures or chain mismatches in 10 min → warning, disconnect, 1 h ban (`GraphBannedNodes`) |

### 3.9 IPC (append-only `ClientCommand`)
| Value | Command | Milestone |
|---|---|---|
| (none) | `openchannel --public` (key 4 of `OpenChannelIpcRequest`, optional) | G1-T1 |
| 16 | `ListGraphNodes` (`listnodes [--node <id>]`): node id, alias, color, addresses, features, channel count | G2-T6 |
| 17 | `ListGraphChannels` (`listgraphchannels [--scid] [--node]`): scid, nodes, capacity, both policies, verification, spent | G2-T6 |
| 20 (planned 18) | `DescribeGraph` (`describegraph [--channels] [--nodes] [--limit n] [--offset n]`): counts, memory estimate, pending writes, ingress queues, sync state per connection (relay depths missing, NL-375) | G5-T4 (done G-D) |
| 19 | `GetRoute` (`getroute <node> <amount_msat> [--max-fee] [--cltv]`): the path with per-hop amount/CLTV/fee and the probability estimate | G4-T4 |

`listchannels` gains `Public` and `Announced`. Numbers are assigned at implementation time and never renumbered (root `CLAUDE.md`).

### 3.10 Pathfinding (G4)
- **Graph snapshot:** `IGraphView` is an immutable adjacency snapshot, rebuilt at most every `Gossip:SnapshotInterval` (30 s) or on demand. It merges public channels with **our** channels (local balances from `LocalLiquidityEstimator`) and invoice hint edges (private, added per payment).
- **Algorithm:**
  - Dijkstra **backward from the payee** (as LND, CLN and LDK do), so the amount each hop must receive is exact when the edge is relaxed.
  - Edge cost:
    `fee(amt) + amt × cltv_delta × RiskFactor(15 ppm/block) + ProbabilityPenalty(amt)`, where
    `ProbabilityPenalty = −ln(P) × PenaltyBaseMsat(1,000) + amt × PenaltyPpm(500)/1e6 × (1 − P)`.
  - `P` uses a uniform-liquidity model: known bounds `[min, max)` from `MissionControl` (decayed with a 1 h half-life); otherwise an a-priori 0.6, or 1.0 for our own channels with enough local balance.
- **Constraints:** skip disabled directions (our own channel uses its live state); `htlc_min <= amt <= htlc_max`; `htlc_max <= capacity` (B7-CU-03); nodes with unknown even features (B7-NA-04); banned nodes; spent or stale channels; at most 20 hops; the total CLTV within `Routing.MaxCltvExpiryDistance`; the payment's `RouteConstraints` exclusions and overrides.
- **Shadow CLTV (B7-RT-01):** a random offset (0-144 blocks, only where it stays within the CLTV limit) added to the final hop.
- **k paths:** iterative Dijkstra with the previous best path's most-used edge penalized (cheaper than Yen, D6). It feeds `PaymentRoutePlanner` for MPP splits.

---

## 4. Decisions

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | **Codec in the Domain payload** (`Parse`/`GetSignedData`) for 256/257/259, like `ChannelUpdatePayload` | The signature hash is needed in Domain/Application; one codec keeps the wire, hash and relay byte-identical; unknown trailing bytes are preserved | Separate serializer and domain model (two codecs drift) |
| D2 | **In-memory graph authoritative, persisted write-behind** (a batched save every 5 s or 1,000 changes, in its own scope); raw bytes stored; full reload at startup | Pathfinding needs microsecond lookups; gossip loss on a crash is harmless (re-synced); the 3 providers already exist | Memory only plus a snapshot file (a 4th storage format, no IPC/SQL visibility); DB-only reads (too slow for Dijkstra) |
| D3 | **Funding verification via `getblock <hash> 1` txid list + `gettxout`**, LRU by height, rate-limited | Works without txindex and with any bitcoind; `gettxout` checks unspent, script and amount in one call | `scantxoutset` (seconds per call, global lock); requiring `-txindex` (still needs the index → txid mapping); trusting unverified announcements (spam) |
| D4 | **Spent detection from the monitor's block input scan** (`OnBlockInputs`) against an in-memory outpoint set | Zero extra RPCs; the monitor already walks every input and handles reorgs | One `WatchedOutpointEntity` per public channel (50k rows, per-block DB work) |
| D5 | **No zlib** (encoding 1) | BOLT 7 now says it MUST NOT be used; LND and CLN send encoding 0 **(CLN verified by spec history; LND unverified)** | Implementing zlib for old peers |
| D6 | **Dijkstra backward + probability-weighted cost; iterative penalization instead of Yen** | Exact amounts per hop; the proven design of LND, CLN and LDK; k-diverse paths for MPP at Dijkstra cost | Yen's k-shortest (costly, near-duplicate paths); a min-cost-flow solver (overkill for G4) |
| D7 | **The graph feeds `PaymentRoutePlanner`** as a third candidate source (direct, hints, graph); `RoutingInfo` paths are extended with per-hop HTLC limits | Reuses the tested retry/MPP/fee-limit machinery (W6-C); private payees combine graph + hint | A second payment pipeline |
| D8 | **Mission control in memory**, half-life decay, never persisted | Stale liquidity data after a restart is worse than none; simpler | Persisted MC (LND) |
| D9 | **Failure `channel_update`s are never written to the graph** (BOLT 4 MUST NOT); they stay per payment (existing `RouteConstraints`); a failure may trigger `query_short_channel_ids` for that SCID | Spec compliance; the gossip copy is the authoritative, relayable one | Applying them to the graph as the brief asked |
| D10 | **Invalid 259 → warning + close**, never fail the channel | BOLT 2/7 MAY either; failing a funded channel over announcement data is disproportionate (LND does the same **(unverified)**) | `error` + force close |
| D11 | **Addresses are not announced by default** (`Gossip:AnnounceAddresses` empty) | Privacy; a node_announcement without addresses is valid | Announcing `ListenAddresses` (often 127.0.0.1 or a private IP) |
| D12 | **Mainnet gate:** `Gossip:SyncEnabled`/`RelayEnabled` default false on mainnet until Proof G5; public channels allowed on mainnet only after Proof G1 | Memory, DoS and bitcoind load are unproven at mainnet scale | Enabling everywhere at once |
| D13 | **Own Docker process** (`scripts/run-gossip.sh`, collection with `DisableParallelization`), LND started with `--trickledelay=1000` **(flag name and ms unit to verify for LND 0.20)** | Public channels permanently change the shared LND graph; the default 90 s trickle makes proofs slow | Reusing the `regtest` collection |

---

## 5. Milestones

**Proof conventions:**
- Docker proofs live in `Docker/Gossip/` with their own fixture instance (alice, bob, carol with the LND-LND channels; david without).
- Every proof waits with `ChainSync.MineAndWaitAsync` and polls LND through LNUnit gRPC (`DescribeGraph`, `GetChanInfo`, `GetNodeInfo`, `UpdateChannelPolicy`, `CloseChannel`).
- Assertions filter by the test's own SCIDs and nodes.
- CLN proofs use `ClnClient` (`listchannels`, `listnodes`).
- Each proof logs the LND/CLN version and every value it reads.

### Gossip wave G-A record (status 2026-09-26, `wip/fafo` @ `164289a`)

Five lanes, each with a review step; lane SHAs mapped to `wip/fafo` through the `-x` footers (lane → `wip/fafo`). Integrate commits: b515155 (registers `AddGossipBitcoinServices()` in `AddBitcoinInfrastructure`, binds `FundingOutputLookupOptions` to the `Gossip` section, fixes the `using Domain.Enums;` seam in `GossipFeatures`), 2138eae (O6 (b) Docker victim with `WatchMempool = false`), 164289a (guides).

| Task | Status | `wip/fafo` SHAs (lane) | Notes |
|---|---|---|---|
| G0-T1 typed 256/257 | done | 57bb15b (335056a) | Domain codecs `ChannelAnnouncementPayload`/`NodeAnnouncementPayload` (Parse/TryParse/GetBytes/GetSignedData/GetSignatureHash, trailing bytes signed and kept); `GossipCodecPayloadSerializer<T>`; `GossipMessage`/`GossipPayload` deleted. Address bytes stay raw in the payload (decoded through G0-T4). A key without a 02/03 prefix → warning + close (compliant; G2 may prefer to ignore relayed gossip). |
| G0-T2 typed 259 as a channel message | done | 57bb15b, 0c6a9c3 (335056a, 860594a) | `PeerService` raises it with the channel messages; interim `ChannelManager` outcome (channel-scoped "not supported yet" warning, connection kept; unknown channel → error; Failed → stored error) pinned by tests. Test name: `PeerServiceTests.Given_InitializedPeer_When_AnnouncementSignaturesReceived_Then_RaisedAsChannelMessage`. |
| G0-T3 signature helpers | done | c5c7b5d (ea50cf3) | `IGossipSignatureVerifier` (Domain) + `Infrastructure.Bitcoin/Gossip/GossipSignatureVerifier` (high-S normalized, malformed → false, batch `VerifyAll`); `LocalLightningSigner.VerifyNodeMessage` delegates; internal `GossipSignedRanges` for raw payloads. 256/257 range tests are self-signed (NL-345). |
| G0-T4 address descriptors | done | 70744d6, caf8ee7 (8d408da, 8e1d39a) | NL-008 fixed. Files under `Domain/Gossip/Addresses/` (subfolder instead of the flat layout of §3.1). Vectors inline in the tests (no LND `lnwire` hex copied). |
| G0-T5 captured vectors | done | 7d318b5 (5bd2560) | LND 0.20: 3×256, 3×257, 7×258, 1×259; CLN v26.06.8: 1×256, 1×257, 2×258, 1×259 in `Tests.Utils/Vectors/Bolt7Vectors.cs`; `BOLT7/Bolt7CapturedVectorTests` byte-exact and every 256/257/258 signature verified (259 layout only). Explicit capture tests `Docker/Gossip/Capture/` (run in their own process). Observation: the fixture's LND channels are public and LND 0.20 dumps its graph only after our `gossip_timestamp_filter` (resolves Risk 5's "unverified"). |
| G1-T1 announce flag | partial (storage) | a49e166 (1593811) | `ChannelConfigs.AnnounceChannel`, `ChannelParams`/`ChannelModel.AnnounceChannel`, peer announcement signatures and our send time on `Channels`. Handlers, IPC `--public` and `Gossip:AcceptPublicChannels` are wave G-B (B1). NL-341 partial. |
| G1-T2 `SignChannelAnnouncement` | done | 709030c, 910d085 (6d0bff1, 1dadf00) | Implemented by lane A2 (the waves table had A3). Returns `ChannelAnnouncementSignatures(NodeSignature, BitcoinSignature)`, not a tuple; input is the payload from offset 256. Refuses private channels, other chains, SCID mismatches, unordered node ids, foreign keys, data loss, unknown channels. Not yet checked against a captured 256 (G1-T3/T4). Also NL-067 first half: the signer loads channels from the DB (NL-343 follow-up). |
| G2-T1 graph model + validator | done | 78e5b23, caf8ee7 (c6d05e0, 8e1d39a) | `Domain/Gossip/{Graph,Validation}/`. `MayBlacklist` only with `signaturesVerified: true` and only for differing node ids; `GraphPolicy.ExtraData` compared at the same timestamp. |
| G2-T2 funding output lookup | done | 79debc3, c1bb630 (b163c77, 8cce2eb) | `IFundingOutputLookup` (Domain) + `Infrastructure.Bitcoin/Gossip/FundingOutputLookup`; `IBitcoinChainService.GetBlockTxIdsAsync`, `GetConfirmedUnspentOutputAsync`. Transient statuses (requeue, never score): `BlockNotFound`, `ChainMoved`, `ChainUnavailable`, `OutputSpentInMempool`. No depth check: G2-T4 enforces 6 confirmations. NL-346. |
| G2-T3 `AddGossipGraph` + `IGraphDbRepository` | done | a49e166 (1593811) | Tables `GraphNodes`, `GraphChannels`, `GraphChannelPolicies`, `GraphBannedNodes` on all three providers; storage records in `Domain/Gossip/Persistence` (separate from the A4 read model; G2-T4's `GraphStore` maps them). Postgres seeded upgrade 9/9. |
| G4-T1 pathfinder | done | fd92d5d, 0bf7baf (1645852, 007d39d) | `Domain/Routing/Pathfinding/`. BOLT 7 Routing Example exact; limit-pruned searches rerun ordered by the limit; our stale first hop routes by its live state; 50k channels ≈ 5.6 ms per query (asserted < 50 ms Release). The `HintRouteBuilder.BuildAlong` cross-check moves to G4-T3 (Domain.Tests cannot reference Application). |

Deviations (spec wins, accepted):
- `GossipValidator` accepts a 256 with unknown even features as **not routable** (BOLT 7 only forbids routing through it); §1.2 B7-CA-03 said ignore.
- A malformed `node_announcement` addrlen gives Warn (no close) and is not applied; confirm the policy in G2-T4.
- `SignChannelAnnouncement` returns a record and lives in lane A2's commits; A3's `VerifyNodeMessage` delegation shares the file (merged at integration).

New ledger items: NL-343 (redundant hand registration with the signer), NL-344 (the peer's `remote_addr` stored as its address; undecodable one fails init), NL-345 (self-signed 256/257 range tests), NL-346 (lookup rate limit per lookup; pruned path unproven), NL-347 (Postgres case of the multi-node theory not run).

Next (wave G-B): B1 G1-T1 handlers/IPC (NL-341), G1-T3 handler + `ChannelManager` case (NL-342), G1-T4..T7; B2 G2-T4..T6 (`GossipIngress`, `GraphStore`, `GraphPruner`, `listnodes`/`listgraphchannels`; `ClientCommand` numbers from 17, since 16 is `chainstatus`); B3 Docker Proofs G0/G1/G2 and `scripts/run-gossip.sh`. Carry: NL-343, NL-344, NL-345.

### Gossip wave G-B record (status 2026-09-26, `wip/fafo` @ `5bbfbb5`)

Four lanes (B1 public channels, B2 graph, B3 Docker, M2 BOLT 5 O6-T4 blockers; no migration owner), each with a review step; lane SHAs mapped to `wip/fafo` through the `-x` footers (lane → `wip/fafo`). Integrate commits: 00f6bcf (drops the never-set `PreferredHost`/`PreferredPort` from `IPeerService`/`PeerService` and their dead branches in `PeerManager`, NL-344), 66e773c (`NLightningTestNode` starts the graph like `GossipGraphHostedService`; B3's `TODO(G-B integrator)` markers resolved: `request.IsPublic`, real option names, `GossipGraphProbe` reads IPC 17/18), 717bd97 (`NLightningTestNode.BeforePeersStart` hook so Proof G2 (b) reads the graph before `PeerManager` reconnects to stored peers), 5bbfbb5 (guides). Conflicts at integration: `IOwnGossipSink.cs` (both lanes added it; B2's final commit made it byte-identical to B1's), `NodeServiceExtensions` (both `GossipOptions` and `GossipGraphOptions` bound), the Application/Daemon `CLAUDE.md` files.

| Task | Status | `wip/fafo` SHAs (lane) | Notes |
|---|---|---|---|
| G1-T1 announce flag (handlers/IPC) | done | 4115b34, d77de7f (6c1563c, 5f8de4f) | `OpenChannelClientRequest.IsPublic`, `OpenChannelIpcRequest` `[Key(4)]` (absent = private), CLI `openchannel <node> <sats> [push_sats] [--public]`. `ChannelFactory` (Domain): public channels leave option_scid_alias out of the type (`UseScidAlias` Optional when negotiated), public + zero-conf refused; the fundee stores the flag. `GossipOptions` (`AcceptPublicChannels` true, `AllowPublicChannelsOnMainnet` false, `AnnouncementDepth` 6, only regtest may lower it). A public `open_channel` is refused with `error` when `AcceptPublicChannels` is false and on mainnet unless allowed (d77de7f). NL-341, NL-236 fixed. |
| G1-T3 `AnnouncementSignaturesMessageHandler` + 259 case | done | f6e76c2, 367fdda (598630e, 60cb924) | Unknown → ignored; other peer → error; shutdown/closing → ignored; private → warning; SCID mismatch (before our confirmation only the output index) → warning; bad signature → warning + close (D10); valid → stored, ours replied once per connection (saved before replying); before our channel_ready stored only. A stored half that does not sign the current announcement is forgotten and persisted (`CompleteAnnouncementAsync(channel, uow)`, 367fdda). NL-342 fixed. |
| NL-343 hand signer registration | done | 2555443 (d65c765) | Only when no `IChannelSigningInfoSource` is registered; a source-less signer is re-registered at the funding confirmation (real SCID). |
| G1-T4 block-driven send + reconnect | done | 2cc2ee0 (7c71d43) | `IChannelAnnouncementService.PrepareOwnAnnouncementSignaturesAsync` (saves `LocalAnnouncementSignaturesSentAt` first), `CompleteAnnouncementAsync`, `IsAnnouncementComplete`, static `IsAnnounced`. `ChannelManager` appends our half after a processed `channel_reestablish` and when a message turns the channel Open; `HandleNewBlockDetected` → `AnnouncementRound` under each channel's lock, skipped while not reestablished. **Extra gate:** `CanSendAnnouncementSignatures` is false on mainnet unless `Gossip:AllowPublicChannelsOnMainnet`. Proof: `Gossip/Announcements/AnnouncementHarnessTests` on `TwoNodeHarness(announceChannel: true)` with two real signers (identical 256 bytes, 4 signatures verify; retransmission after a link drop; nothing at 5 confirmations or after shutdown). |
| G1-T5 public `channel_update` | partial | 2cc2ee0 (7c71d43) | `ChannelUpdateService.IsPublic` (flag, SCID, both halves exchanged): dont_forward clear, real SCID even with option_scid_alias negotiated, handed to `OwnGossipPublisher`; `OnChannelAnnounced` re-signs; an announced channel turning ShuttingDown/Negotiating/Closing/Failed gets one disabled update. **Not done:** disable after the peer is offline > `Gossip:DisableAfter` (NL-349). |
| G1-T6 `NodeAnnouncementService` | done | 2cc2ee0 (7c71d43) | Only with ≥ 1 announced channel; `Node:Alias` (≤ 32 UTF-8 bytes), `Node:Color` (default 3399ff), `Gossip:AnnounceAddresses` (none by default, sorted); timestamp = max(now, stored + 1, last + 1), our `GraphNodes` row upserted and saved **before** publishing; unchanged fields only re-published until `NodeAnnouncementRefreshInterval` (13 d). |
| G1-T7 own-gossip relay | done | 7fdc993, dd5c4a1 (7dc02f9, bb2b6bb) | `Application/Gossip/Relay/GossipRelayScheduler`: latest own 256/258/257, flush every `Gossip:OwnGossipFlushInterval` (60 s) to every connected peer whose `init.networks` include our chain (or name none), once per connection, order 256 → 258 → 257, a 256 only once an update for it is queued, our 257 only after one of our 256s went out on that connection (dd5c4a1). Sent with `IPeerService.SendGossipMessageAsync`, not the `PeerOutbox` (NL-351). |
| G2-T4 `GossipIngress` + `GraphStore` | done | 7501ad6, 2635956, 675f54c, 9d288bf (6d5c407, 83b850b, 83408f3, de71ebc) | Bounded queues (2,000 per peer, 20,000 total), duplicate filter → `GossipValidator` → `IGossipSignatureVerifier` → funding lookup at 6 confirmations (deferred/retried when transient; `Gossip:FundingValidation=SkipUnavailable` keeps `BlockUnavailable` as Unverified); bad signature → warning + close; orphan 258/257 replayed; B7-CA-04 ban only when the same funding keys sign other node ids, and the banned nodes' channels are forgotten (never ours). `GraphStore`: in memory, one writer lock, write-behind (5 s or 1,000 changes, one save), load where earlier changes win. `IOwnGossipSink` (B1's contract) is implemented by the ingress (non-blocking queue, replaces `NullOwnGossipSink`); our node row has one writer (`NodeAnnouncementService`). `PeerService`: 256/257 → ingress, 258 → `ChannelUpdateService` and ingress; `gossip_timestamp_filter(0, 0xFFFFFFFF)` after our init to `gossip_queries` peers (never before init, 675f54c). Receive depth `GetAnnouncementDepth(network)`. Dropped messages: SCIDs recorded for G3 (NL-353). NL-344 fixed. |
| G2-T5 `GraphPruner` | done | f252d68, 0e0f85c (8a28fd9, 97a25bb) | `BlockchainMonitorService.OnBlockInputs` (after the block's save, built only with a subscriber). Spent → `SpentAtHeight`, removed at +72 (also ours); stale → removed after `DeleteStaleAfter` (28 d) unless ours; lonely nodes removed (never ours); reorgs `ClearSpentAbove(fork)`; funding blocks reorged out re-checked (another txid or gone → spent, forgotten at +72); spends of the last 6 blocks re-matched (ingress/pruner race). Funding txids are not persisted: looked up once per channel after a restart, after the first block (NL-352). Started by `Daemon/Services/GossipGraphHostedService` before `NltgDaemonService` (ingress, then pruner) and stopped after it. |
| G2-T6 IPC | done | d3dda72 (a671846) | `ClientCommand` `ListNodes = 17`, `ListGraphChannels = 18` (not 16/17: 16 is `chainstatus`; next free 19); CLI `listnodes [node_id]`, `listgraphchannels [BLOCKxTXxOUTPUT] [node_id]`; `invalid_operation` while the graph is disabled. |
| B3 Docker fixture + Proofs G0/G1/G2 | done (G1 (d), G2 (d) not written) | 8ecbb2e, 27f8822, fd71c07 (8cd7e0b, 1968dfc, 2b01a84) | `Docker/Gossip/`, `scripts/run-gossip.sh`: G0 smoke (alice's dump after our filter, all parsed), G1 (a) our public open (bob's `DescribeGraph` has it, `GetNodeInfo` alias/color), (b) alice's public open, (c) our restart at 3 confirmations; G2 (a) LND graph in ours, (b) after a restart without connection, (c) spent at the close height, gone by +72. 16/16 green at integration. G1 (d) (NL-255) and G2 (d) (NL-356) are not written. |

Deviations (accepted):
- Stale deletion (`GraphPruner`) uses the newest update of either direction (B7-PR-02: the latest updates in both directions older than two weeks) and deletes after `DeleteStaleAfter` (28 d); routing exclusion at 14 d is `GraphChannel.IsStale`, which uses the older direction (stricter than the spec's MAY; `GraphPathfinder` already skips spent and stale channels).
- An extra mainnet gate on announcing (`CanSendAnnouncementSignatures`) besides the open-time D12 refusal.
- Own gossip bypasses the `PeerOutbox` until G3-T3 (NL-351).

New ledger items: NL-348 (switch refuses the real SCID of a public channel with option_scid_alias Optional, high), NL-349 (no disable after the peer is offline), NL-350 (reorg moving an announced SCID does not reset the announcement), NL-351 (own gossip bypasses the outbox), NL-352 (graph funding txids not persisted), NL-353 (dropped gossip never re-queried), NL-354 (`GraphPolicy` equality by reference), NL-355 (harness restart drops announcement fields), NL-356 (Proof G2 (d) missing). Fixed: NL-236, NL-341, NL-342, NL-343, NL-344.

Next (wave G-C): NL-348 first (switch owner; it blocks G4 Proof (a) through us); C1 G3-T1..T4 (`QueryResponder`, `GossipSyncManager`, relay of others' gossip with filters, moving own gossip onto the outbox NL-351, re-query of missed SCIDs NL-353); C2 G4-T2..T4 + G3-T5 (`MissionControl`, graph paths in `PaymentRoutePlanner`, `getroute` = `ClientCommand` 19); C3 Proofs G3/G4 and `ClnGossipTests`, plus G1 (d) (NL-255) and G2 (d) (NL-356). Carry: NL-345, NL-346, NL-349, NL-350, NL-352, NL-354, NL-355.

### Gossip wave G-C record (status 2026-09-26, `wip/fafo` @ `4dc0f77`)

Four lanes (C1 sync/relay, C2 routing, C3 Docker proofs, M3 G-B follow-ups as migration owner for `AddGraphFundingTxId`), each with a review/fix step; lane SHAs mapped to `wip/fafo` through the `-x` footers (lane → `wip/fafo`). Lane C3's agent died on a network outage after committing; its review and fix ran as a separate agent (deda1a5, 412f1d5). Integrate commits: b5de7be (G3-T5 seam: `Gossip/Sync/GossipSyncScidRefresher` implements `IGossipScidRefresher` over `IGossipSyncManager.QueryScidAsync` (thread pool, one query per SCID in flight, at most 5 min) and replaces the null default; `PeerManager` implements `IPeerGossipOutbox.TryEnqueueGossip` (current connection only) and is registered, so own and relayed gossip go through the `PeerOutbox`, NL-351), 0325ef3 (`GetRouteProbe` uses the typed `IClientCommandHandler<GetRouteClientRequest, GetRouteClientResponse>`), b334442 and 485a9aa (goal proofs (c) and (e): wait out C2's invoice hint grace period; CLN G3 (d) on N1's recorded wire), 4dc0f77 (N1 nudges CLN's seeker; G-C proofs documented in `test/CLAUDE.md`).

| Task | Status | `wip/fafo` SHAs (lane) | Notes |
|---|---|---|---|
| NL-348 real SCID of a public channel | done | bca66aa (9f60c56) | `HtlcSwitch.ResolveOutgoingChannel` refuses the real SCID only when `UseScidAlias == Compulsory` (option_scid_alias in the channel_type). |
| NL-354, NL-352, NL-355, NL-350, NL-349 (G-B follow-ups, lane M3) | done | 12d3e74, 016a523, b9314d3, 7a4ef7f, 833e8e6, 80fd35f (f207db1, 616ced8, 260d698, c95e340, 2f10074, a8cf213) | `GraphPolicy` value equality; migration `AddGraphFundingTxId` (3 providers, nullable `GraphChannels.FundingTxId`); harness restarts keep the announcement fields; a reorg that moves the SCID resets the announcement state in `FundingReconfirmationHandler` (deviation: there, not in `ChannelAnnouncementService`); G1-T5 offline disable after `Gossip:DisableAfter` (20 min), re-checked under the channel lock, re-enabled only once the link is up after the reestablish (review). |
| G3-T1 `QueryResponder` | done | 7b21464, c16edf0 (e808ec1, 6996d62) | `Application/Gossip/Sync/QueryResponder` replaces `GossipQueryResponder`: `reply_channel_range` chunks from the graph, `query_short_channel_ids` with `query_flags`, `full_information` = 1 after our initial sync; strict codec, CRC32C checksums and `GossipTimestampFilter` in 7b21464. `PeerService` hands 261-265 to `IGossipSyncService` (without one it answers empty, in-process tests only). NL-205. |
| G3-T2 `GossipSyncManager` | done | c16edf0, 4b4f7b6 (6996d62, b2946cd) | Range query → SCID batches (one outstanding) → `gossip_timestamp_filter`; dropped SCIDs re-queried every `MissedScidRetryInterval` (NL-353); review: batches sized to the ingress queue and waiting for it to drain, a timed-out or broken query ends querying on that connection (NL-365), a failed sync peer still gets a live filter. **Deviation (§3.7):** after a sync with `gossip_queries_ex` timestamps the filter starts where the sync started less 600 s (`GossipSyncManager.TimestampSyncFilterMarginSeconds`), not now - 2 weeks; syncs without timestamps keep the 2-week backlog (`GossipSyncOptions.SyncFilterBacklog`). Sync peers are first come, first served (NL-363). |
| G3-T3 relay of others' gossip | done | 607a91f, 4b4f7b6 (4973900, b2946cd) | Per-peer filters, staggered 60 s flushes, origin suppression (`OriginTrackingGossipIngress`, `GossipOriginTracker`), 256 before 258/257, paced backlog (`BacklogMessagesPerSecond` 1,000), `init.networks`; `Gossip:RelayEnabled` unset = on except mainnet (D12). Review: a 256 flushed alone goes out with its later update (edge case left: NL-368), one stalled peer no longer stops the relay (per-connection runs, `RelaySendWait` 2 s). Relay diffs the snapshot every 10 s (NL-366); the backlog now sits in the unbounded outbox (NL-360). |
| NL-351 gossip through the `PeerOutbox` | done (own + relayed) | 607a91f, b5de7be | `IGossipPeerSender` → `PeerGossipSender` → `IPeerGossipOutbox` (`PeerManager`). The sync manager's queries, replies and filters still use the direct send (NL-361). |
| G3-T4 `gossip_queries_ex` | done | 7b21464, 89110b9 (e808ec1, 6d05066) | `ChannelUpdateChecksum` equals CLN v26.06.8's `checksums_tlv` for both directions and our reply equals CLN's field for field (explicit capture `ClnGossipQueryCaptureTests`, `Tests.Utils/Vectors/Bolt7QueryVectors.cs`); `FeatureOptions.ExpandedGossipQueries` is now Optional (bits 10/11 in our init and node_announcement). LND 0.20 does not offer it. |
| G4-T2 `MissionControl` | done | a34c9b5, 2638eff (c70a274, 9784c32) | `Payments/Routing/MissionControl` (memory only, D8; 1 h half-life); review: a node penalty is never kept on our own peers (hop 0). |
| G4-T3 planner integration | done | a34c9b5, f27340c, 2638eff, 5673e78 (c70a274, df22e57, 9784c32, 7c0570b) | `Routing/GraphPathSource` + `GraphRoutingContext`: graph paths when no direct or hint path carries the amount alone (review F1: also when they are depleted, bounded, too small or too dear; direct and hint still tried first; a split may combine all three), graph → hint entry for a private payee, `HopLimit`s, shadow CLTV on graph routes only (B7-RT-01), direction-less `PolicyOverrides` on hint paths only. In process: `GraphRoutePlannerTests`, `GraphPaymentHarnessTests` (Bob → Carol → David → Erin with exact fees, retry with the graph unchanged, MPP over two graph paths, `getroute` = the paid route). `Docker/PaymentRetryFlowTests` runs with `Node:Payments:UseGraph=false`. |
| Invoice hint policy (NL-245) | done | e08e20b, 2638eff (6300e8c, 9784c32) | `Node:Invoices:RouteHints` `Auto`/`Always`/`Never`. **Deviation (review F3):** `Auto` drops the `r` field only once the announced channel has been in our own graph with both policies for `Node:Invoices:PublicChannelGracePeriod` (10 min); proofs that need a hint-free invoice shorten it to 5 s (b334442, 485a9aa). |
| G3-T5 failure-triggered refresh | done | a34c9b5, b5de7be | `PaymentRetryPolicy` → `IGossipScidRefresher.RequestRefresh(scid)` on an intermediate hop's UPDATE failure; bound to `GossipSyncScidRefresher` at integration. The failure's `channel_update` never reaches the graph (D9, B4-CU-01). |
| G4-T4 `getroute` | done | 91cde4c (75d94f3) | `ClientCommand.GetRoute = 19` (next free 20), `IRouteQueryService.QuoteRouteAsync` (one-part plan, no shadow offset); CLI `getroute`. No `--max-cltv` (the limit is `Routing.MaxCltvExpiryDistance`). |
| C3 Docker proofs | done except G4 (b)/(c) | 072be5a, 3dbc8be, 7399b83, 412f1d5, deda1a5 (4dbaa8b, b377163, 15566a0, faafdf2, 281b837) | `PublicPaymentFlowTests` goal proofs (b) = Proof G4 (a), (c), (d) = Proof G4 (d); `GossipSyncFlowTests` G3 (a) sync by queries from bob alone (on `GossipTrafficRecorder`), (b) carol's fee change while we were down, (c) relay to N3 through N2 without echo; `GraphStoreFlowTests` G2 (d) (NL-356); `PublicChannelFlowTests` G1 (d) (NL-255: LND hints through us); `Interop/Cln/ClnGossipTests` Proof G3 (d) + goal proof (e). **Deviation:** CLN v26.06.8 has no `dev-query-scids`; G3 (d) records N1's wire and waits for CLN's own seeker (nudged every 30 s by N1 with an unknown-SCID `channel_update`, 4dc0f77; timing-dependent, NL-357). Proof G4 (b) and (c) are not written (NL-367). |

Gates at `4dc0f77`: Release and Release.Native, net10.0 (SDK 10.0.103) and net10.0 + net11.0 (SDK 11.0.100-rc.1), 0 errors, the 5 baseline CS86xx; format clean. Non-Docker net10.0: **7000** per configuration (Domain 2388, Application 1536, Infrastructure.Bitcoin 871, Integration 648, Serialization 520, Infrastructure 401, Daemon 358, Bolt11 278); Long simulator 1/1. Docker (net10.0, in-container runner): gossip (`scripts/run-gossip.sh`) **24/24** on the second run (the first failed only goal proof (c), fixed by b334442), CLN **22/22** (incl. `ClnGossipTests` 5/5), LND suite 55/56 in a run parallel with CLN (the one failure, `CooperativeCloseFlowTests` simple close, was "Address already in use" from the shared `PortPoolUtil` range, NL-359; the class passed 7/7 alone), `Docker.Utils` 2/2, on-chain 22/22 (2 `Explicit` not run), ABCD 3 × 10/10.

New ledger items: NL-357 (CLN G3 (d) seeker timing), NL-358 (`run-onchain.sh`/`run-abcd.sh` on the host), NL-359 (`PortPoolUtil` collisions), NL-360 (relay backlog in the unbounded outbox), NL-361 (sync messages bypass the outbox), NL-362 (old SCID's own announcement left in the graph), NL-363 (sync peer choice), NL-364 (offline disable: memory-only time, re-enable delay), NL-365 (a query timeout ends querying on the connection), NL-366 (relay diffs the whole snapshot), NL-367 (Proof G4 (b)/(c) missing), NL-368 (258 without its 256 at a filter boundary), NL-369 (implicit `byte[]` conversions in conditionals). Fixed: NL-205 (graph answers), NL-245 (hint policy), NL-255, NL-348..NL-356.

Next (wave G-D): D1 G5-T1 memory limits and accounting (with NL-360, NL-366 and a signet-sized sync for NL-353/NL-365), G5-T2 spam protection; D2 G5-T3 persistence performance (`AddGossipIndexes` if needed; migration owner) and G5-T4 `describegraph` (`ClientCommand` 20) + `Meter("NLightning.Gossip")`; D3 G5-T5 mainnet gate defaults and the 24 h signet/Mutinynet soak; the BOLT 5 O6-T4 gate decision (re-run N9 `ChannelSafetyFlowTests`, ABCD and the LND/CLN normal-operation suites). Carry: NL-345, NL-346, NL-347, NL-357..NL-369, B7-CU-01b.

### Gossip wave G-D record (status 2026-09-26, `wip/fafo` @ `48a8951`)

Four lanes (D1 limits/spam/metrics, D2 persistence performance + `describegraph`, D3 gate/soak/scripts, M4 the BOLT 5 O6-T4 mainnet HTLC gate), each with a review/fix step; lane SHAs mapped to `wip/fafo` through the `-x` footers (lane → `wip/fafo`), cherry-picked on `55c0abc` in the order D2, D1, M4, D3. No lane shipped a migration (D2 measured no need for `AddGossipIndexes`). Integrate commits: d31cd2f (`GraphStore` takes `GossipMetrics?` as an optional last constructor argument: histogram `nlightning.gossip.store.duration` with `operation` load/flush and `outcome`, queue depth `graph_write_behind`) and 48a8951 (O6-T4 integration record, container runner scripts in root `CLAUDE.md`).

| Task | Status | `wip/fafo` SHAs (lane) | Notes |
|---|---|---|---|
| G5-T1 graph caps | done | 97cdc27 (63c12ed) | `MaxChannels` 200,000 / `MaxNodes` 100,000 enforced in the ingress through `IGraphStore.ChannelCount`/`NodeCount`, before the signature check and chain lookup; own gossip bypasses; logged at the first and every 1,000th refusal, counted as `rejected{reason=graph_full}`. |
| G5-T1 memory estimate | done | 66b4dbc (b3edb96) | `IGraphStore.GetMemoryEstimate()` → `GraphMemoryEstimate`, O(1), constants in `GraphMemoryAccounting` calibrated on SQLite loads; `IGraphStore.PolicyCount`. |
| G5-T1 interned node ids, `Gossip:MaxMemoryMb` | **open** | — | Measured 475 MiB store (2.5 KB per channel) + 39 MiB per snapshot at 200k channels; about 553 MiB with a second snapshot during a rebuild, above the planned 512 MiB budget (D2 proposes 768). Nothing enforces the budget yet (NL-373). Snapshot rebuild under the writer lock: NL-374. |
| G5-T2 spam protection | done | 97cdc27, ff09f96 (63c12ed, e706093) | `GossipRateLimiter` (§3.8 limits; the signature is checked first, a token is spent only on an accepted message, the newest refused validly signed message per key is kept (`MaxRateLimited` 10,000) and replayed when allowed); `GossipMisbehaviourTracker` (invalid signatures, bad encodings, funding mismatches; 5 in 10 min → warning with the offence text, disconnect, 1 h ban; persisted in `GraphBannedNodes` only for graph nodes; in-memory bans capped by `MaxMisbehaviourBans` 10,000 and pruned); future timestamps (14 days) dropped; queue/orphan caps now counted; relay backlog `Gossip:MaxRelayPendingPerPeer` (5,000, evicted by channel group, a dropped 256 re-sent before its later 258; NL-360 partial). Follow-ups NL-370 (peer ban in memory only), NL-371 (funding mismatches may ban unchecking relayers), NL-372 (expired bans never pruned). |
| G5-T3 persistence performance | done | 66b4dbc, 2abbec2 (b3edb96, a0420f1) | `GraphStore.FlushAsync` in batches of `WriteBatchSize` (5,000) rows, each in its own unit of work, with bulk repository reads (500 keys per read); `LoadAsync` streams the tables and applies them in batches of `LoadBatchSize` (10,000) (in-memory changes win). 200,000 channels + 400,000 policies + 50,000 nodes: load 1.55 s (target 10 s), batched flush 19-29 s, first snapshot 69-105 ms (`GraphStoreLoadPerformanceTests`, `Explicit`, `Category=Long`; a 5,000-channel variant runs by default). `AddGossipIndexes` not needed. SQL Server bulk paths unproven locally (NL-377). |
| G5-T4 `describegraph` | done | 4860494, 2abbec2 (8fb5ded, a0420f1) | `ClientCommand.DescribeGraph = 20` (next free 21); counts, capacity of unspent verified/own channels, pending writes, memory estimate, ingress queues, per-connection sync state, paged `--channels`/`--nodes` (`--offset` with both is refused). Relay queue depths not reported (NL-375). |
| G5-T4 metrics | done | 97cdc27, b77d1c1, d31cd2f (63c12ed, 7d9db1c) | `Meter("NLightning.Gossip")` (`Gossip/Metrics/GossipMetrics`): received, accepted, rejected{type,reason}, orphaned, dropped{reason}, relayed{type,path}, chain.lookups{status}, peers.banned, sync.duration, store.duration, observable queue.depth (ingress, orphans, retries, relay_pending, rate_limited, graph_write_behind); enum tag values in snake_case. Asserted with `MeterListener` in `GossipMetricsTests` and the ingress limit tests. |
| G5-T5 template gate | done | 1d561f2, aadb9d4 (988bbde, 0444045) | `CreateDefaultConfigJson` writes a `Gossip` section: `Enabled`/`SyncEnabled`/`RelayEnabled` false on mainnet and true elsewhere, `AllowPublicChannelsOnMainnet` false, `AcceptPublicChannels` left at its default; `Node:EnableHtlcs` is `null` on mainnet/testnet (follows the code default). Options test per network (`NodeServiceExtensionsTests.Given_DefaultConfigJson_When_Bound_Then_GossipMainnetGateIsExplicit`). |
| G5-T5 24 h soak | **partial** | 1d561f2, 70411ac, e7c628e (988bbde, a47d8ea, 26232d1) | `scripts/mutinynet/soak-gossip.sh` (`start`, `run`, `stop`, `status`); started 2026-09-26 19:10:53 UTC. First 20 min in `MUTINYNET.md`: 895 channels / 194 nodes in under 5 min, ~3,600 RPCs for the initial sync plus 1,386-block catch-up, then 3.2 RPC/min, bitcoind CPU < 1 %, graph reloaded after a restart, RSS 178 → 207 MB flattening, 0 errors; WAL growing with no checkpoint seen. The 24 h evaluation and a second sync peer are open (NL-376). |
| Docker runner scripts, per-process ports | done | 1d561f2, aadb9d4 (988bbde, 0444045) | `run-onchain.sh` and `run-abcd.sh` use the in-container `--network host` runner like `run-gossip.sh` (NL-358); `PortPoolUtil` per-process ranges (NL-359); NL-276 partial. |
| BOLT 5 O6-T4 (lane M4) | done | a140940, 04aab92, 6de56ad, 44767d3, 48a8951 (c4e94e8, ea24d95, e9941ea, 4af0f5f) | HTLCs on for every network by default; NL-315 fixed; see `BOLT5_ONCHAIN_PLAN.md` "O6-T4 decision" and "O6-T4 integration record". |

Gates at `48a8951`: Release and Release.Native (SDK 10, and SDK 11 with net10.0 + net11.0), 0 errors, the 5 baseline CS86xx; format clean. Non-Docker net10.0: **7104** per configuration (Domain 2392, Application 1598, Integration 668, Serialization 520, Infrastructure 401, Infrastructure.Bitcoin 871, Bolt11 278, Daemon 376); Long simulator 1/1. Docker (net10.0, in-container runner, one process at a time): gossip `scripts/run-gossip.sh 3` **3 x 24/24** (Proof G5's Docker part), on-chain 24/24 with both `Explicit` O5 variants, ABCD 3 x 10/10, LND suite 58/58 (SqlServer and the multi-node server-database theory excluded, NL-347), CLN 22/22.

New ledger items: NL-370 (misbehaviour peer ban in memory only), NL-371 (funding mismatches count toward the ban), NL-372 (expired bans never pruned), NL-373 (graph memory: interning and `Gossip:MaxMemoryMb`), NL-374 (snapshot rebuild under the writer lock), NL-375 (`describegraph` lacks relay depths), NL-376 (24 h soak evaluation, RSS/WAL trend), NL-377 (SQL Server bulk graph paths unproven), NL-378 (runner scripts print totals only). Fixed: NL-358, NL-359 (and NL-315, NL-094 on the BOLT 5 side); partial: NL-360 (the relay's own backlog is bounded, the `PeerOutbox` depth is not), NL-276.

Next: close G5 (NL-373 interning + `Gossip:MaxMemoryMb` enforcement and a re-measurement; the 24 h soak evaluation with a second sync peer, NL-376; the outbox depth half of NL-360), then decide D12 for mainnet. Carry: NL-345, NL-346, NL-357, NL-361..NL-369, NL-370..NL-372, NL-374, NL-375, NL-377, NL-378, B7-CU-01b, Proof G4 (b)/(c) (NL-367).

### G0: Wire completeness (no behaviour change except typing)
| Task | Files | Acceptance |
|---|---|---|
| **G0-T1** Typed `channel_announcement` + `node_announcement` | `Domain/Protocol/Payloads/{ChannelAnnouncement,NodeAnnouncement}Payload.cs`, `Messages/{ChannelAnnouncement,NodeAnnouncement}Message.cs` (now `BaseMessage` with a typed payload), `Serialization/Payloads/*PayloadSerializer.cs` + `Messages/Types/*MessageTypeSerializer.cs`, both factory dictionaries (`PayloadSerializerFactory`, `MessageTypeSerializerFactory.cs:139-148`) | Round trip byte-exact incl. trailing bytes; `GetSignatureHash` = dSHA256(bytes[256..]) for 256 and dSHA256(bytes[64..]) for 257; malformed (short, len overflow) → `PayloadSerializationException` (the peer gets warning + close, NL-207) |
| **G0-T2** Typed `announcement_signatures` as a **channel message** (GG2) | `AnnouncementSignaturesPayload : IChannelMessagePayload`, `AnnouncementSignaturesMessage : BaseChannelMessage`; `IMessageFactory.CreateAnnouncementSignaturesMessage`; `PeerService` routes it with the channel messages | Serialization tests; `PeerServiceTests.Given_259_Then_RaisedAsChannelMessage` |
| **G0-T3** Signature helpers | Domain port `IGossipSignatureVerifier` (+ batch overload); `Infrastructure.Bitcoin/Gossip/GossipSignatureVerifier.cs` (logic moved out of `LocalLightningSigner.VerifyNodeMessage`, which delegates) | Existing `LocalLightningSignerNodeMessageTests` stay green; high-S accepted; invalid point → false |
| **G0-T4** Address descriptors (resolves NL-008) | `Domain/Gossip/AddressDescriptor.cs` + `AddressDescriptorCodec` (types 1-5, strict lengths, stop at the first unknown type, port 0 ignored, one DNS); `RemoteAddressTlvConverter` uses it | All 5 types round trip; Tor v3 35+2; DNS 1+len+2; vectors from LND `lnwire` tests **(copy the hex into `Tests.Utils/Vectors/Bolt7Vectors.cs`)** |
| **G0-T5** Captured vectors | `test/NLightning.Tests.Utils/Vectors/Bolt7Vectors.cs`: a 256 + 257 + 258 captured from LND 0.20 and CLN v26.06.8 in Docker (written by a one-off `Explicit` capture test that logs raw inbound bytes) | Each parses, re-serializes byte-exact, and all its signatures verify |

**Proof G0:** unit and vector tests. Docker smoke: connect to alice for 60 s; every 256/257 alice sends parses (0 parse warnings in our log); the connection stays up.

### G1: Public channels
| Task | Files | Acceptance |
|---|---|---|
| **G1-T1** Announce flag end to end (resolves NL-236, GG3) | `OpenChannelClientRequest.IsPublic`, `OpenChannelIpcRequest` key 4, CLI `--public`; `OpenChannelClientHandler.cs:110-112` sets the flag and `ChannelParams` drops scid_alias when public; `OpenChannel1MessageHandler` stores the peer's flag (`Gossip:AcceptPublicChannels`); `ChannelModel.AnnounceChannel` + `ChannelConfigEntity.AnnounceChannel` (migration G-A) | Handler tests: public open has no scid_alias in the type; fundee stores flag=1; `AcceptPublicChannels=false` → error; `ChannelRoundTripTests` covers the field |
| **G1-T2** Signer `SignChannelAnnouncement` (§3.5, GG4) | `ILightningSigner`, `LocalLightningSigner` | Refuses a foreign funding key, node id, SCID or chain; both signatures verify under `GossipSignatureVerifier`; low-S |
| **G1-T3** `AnnouncementSignaturesMessageHandler` + `ChannelManager` case | `Application/Channels/Handlers/AnnouncementSignaturesMessageHandler.cs`, `ChannelManager.DispatchChannelMessageAsync` | SCID mismatch → warning; bad signature → warning + close; early → stored and deferred; valid + depth → announcement assembled; replayed → no-op |
| **G1-T4** `ChannelAnnouncementService` (§3.2 steps 3, 5, 7) | `Application/Gossip/Announcements/ChannelAnnouncementService.cs`; reconnect hook from `ChannelManager.OnPeerConnectedAsync`; shutdown suppresses sending | Unit over `TwoNodeHarness` (two real signers): both nodes assemble **identical** 256 bytes whose 4 signatures verify; retransmission after a link drop; nothing after `shutdown`; nothing below 6 confirmations |
| **G1-T5** Public `channel_update` mode (GG6) | `ChannelUpdateService.BuildUnsignedUpdate`: `dont_forward = !channel.AnnounceChannel`; SCID = real for public; re-sign when a channel becomes announced; `SendChannelUpdateAsync(disabled: true)` on close/shutdown/peer offline for more than `Gossip:DisableAfter` (20 min) **(LND/CLN-like policy, unverified)** | Existing tests stay green; public channel → flag clear, real SCID; timestamp strictly increasing |
| **G1-T6** `NodeAnnouncementService` | `Application/Gossip/Announcements/NodeAnnouncementService.cs`; `NodeOptions.Alias`, `Color`; `GossipOptions.AnnounceAddresses` | Sent only once we have at least 1 announced channel; alias validation; addresses sorted; timestamp persisted (in `GraphNodes` for our own id) and increasing across restarts |
| **G1-T7** Relay of our own gossip (minimal, before G3) | `GossipRelayScheduler` own-message path: send 256 → 258 → 257 to every connected gossip peer at the next flush | Order test |

**Proof G1 (Docker):**
- (a) We `openchannel --public` to alice (1M sat), mine 6: alice's `GetChanInfo(scid)` shows both policies incl. ours; `DescribeGraph` on **bob** (relayed by alice) contains the channel; `GetNodeInfo(our id)` on bob shows our alias and color.
- (b) Alice opens a public channel to us (`OpenChannel private=false`): we send `announcement_signatures` after 6 blocks and bob sees the channel.
- (c) Restart our node between confirmation 3 and 6: announcement still completes.
- (d) NL-255 re-check: david's `addinvoice --private` over a private channel to our node now hints through us (because our node is public) **(unverified LND behaviour; record the result)**.

### G2: Validation and graph store
| Task | Files | Acceptance |
|---|---|---|
| **G2-T1** Domain graph model + `GossipValidator` | `Domain/Gossip/{GraphNode,GraphChannel,GraphPolicy,IGraphView,GossipValidator}.cs` | Table tests: every B7-CA-03, B7-NA-02/03 and B7-CU-02/03 row |
| **G2-T2** `FundingOutputLookup` (§3.4, GG8) | `Infrastructure.Bitcoin/Gossip/FundingOutputLookup.cs`, `IBitcoinChainService.GetBlockTxIdsAsync(height)` (`getblock` verbosity 1), LRU, rate limiter; invalidated by `OnBlockDisconnected` | Fake chain tests: index out of range, spent, wrong script, wrong amount, reorged height; Docker smoke against regtest bitcoind |
| **G2-T3** Migration `AddGossipGraph` (migration owner, §3.6) + `GraphDbRepository` | Persistence entities/configs/DbContext, 3 providers, `IGraphDbRepository` on `IUnitOfWork`, mocks + `CrashingUnitOfWork` | `HasPendingModelChanges() == false` ×3; SQLite round trip; Postgres/SqlServer container round trips |
| **G2-T4** `GossipIngress` + `GraphStore` | `Application/Gossip/Graph/{GossipIngress,GraphStore,OrphanUpdateCache,RecentMessageCache}.cs`; `PeerService` raises 256/257/258 to ingress; `ChannelUpdateService` hands public updates on | Pipeline tests with the captured vectors: accept, ignore, warn outcomes; orphan update replayed; our own channel with no chain lookup; restart reload equals the pre-restart snapshot (SQLite) |
| **G2-T5** `GraphPruner` | `OnBlockInputs` in `BlockchainMonitorService` (after the block's save, `:945-960`); spent → `SpentAtHeight`, removed at +72; stale (2 weeks, `Gossip:StaleAfter`, `TimeProvider`) → excluded from routing, deleted after `Gossip:DeleteStaleAfter` (4 weeks); nodes without channels removed; never prune our own channels | Fake-clock and fake-block tests incl. reorg of the spend; a node with only stale channels is pruned |
| **G2-T6** IPC `listnodes`, `listgraphchannels` | `ClientCommand` 17/18 (done: 16 is `chainstatus`), DTOs, Daemon handlers, CLI printers | IPC round trips (`Daemon.Tests`) |

**Proof G2 (Docker):**
- (a) Connect to alice: within 2 min (after G3; before G3 through alice's full dump **(LND dumps only after a filter: verify)**) `listgraphchannels` has alice–bob, alice–carol and bob–carol with both policies, and `listnodes` has alice, bob and carol.
- (b) Restart our node **without** connecting (a test hook starts `PeerManager` after the IPC read): the same channels and nodes are present.
- (c) LND closes alice–carol cooperatively; after 1 block the channel is marked spent and excluded from `getroute`; after 72 blocks it is gone from `listgraphchannels`.
- (d) Stale: our node with `Gossip:StaleAfter=00:02:00`; stop carol; after 2 min without carol's updates, carol's channels are excluded (a mocked-time unit test covers the real 2-week value).

### G3: Gossip sync and relay
| Task | Files | Acceptance |
|---|---|---|
| **G3-T1** `QueryResponder` (replaces `GossipQueryResponder`, GG9) | `Application/Gossip/Sync/QueryResponder.cs`; `PeerService` delegates | Chunking at 65,535 bytes and monotonic ranges; `query_flags` bits 0-4; no duplicate 257 per reply; `full_information` semantics; encoding 1 → warning |
| **G3-T2** `GossipSyncManager` | sync-peer selection, range query → diff → SCID batches (one outstanding), timestamp filters (§3.7), 20-min rotation; validates peers' replies (B7-Q-04) | State-machine tests with a fake peer; a bad reply ends the sync with a warning |
| **G3-T3** Relay with filters (GG11) | `GossipRelayScheduler`: per-peer filter, 60 s staggered flush, origin suppression, 256-before-258/257, backlog pacing, `init.networks` | Order, filter-boundary (`>=` / `<`) and suppression tests; no relay before a filter (B7-RL-01) |
| **G3-T4** `gossip_queries_ex` answers (timestamps + CRC32C checksums) | `QueryResponder`; `FeatureOptions.ExpandedGossipQueries` stays No until this passes, then Optional | Checksum test vector: CRC32C of an update without signature and timestamp (compute with LND/CLN's value from a captured `reply_channel_range` **(capture in G0-T5)**) |
| **G3-T5** Failure-triggered refresh (D9) | `PaymentRetryPolicy` → `GossipSyncManager.QueryScid(scid)` on UPDATE failures | The graph updates only through the gossip reply, never from the failure onion |

**Proof G3 (Docker):**
- (a) Initial sync from bob only (not a channel peer) via queries: graph complete in under 60 s with `--trickledelay=1000`.
- (b) Disconnect; carol changes her fee on carol–bob (`UpdateChannelPolicy`); reconnect: within 2 flushes our graph has the new policy (timestamp filter or re-query).
- (c) Relay: we are connected to alice and to an NLightning node N2 (`NLightningTestNode`, a gossip-only peer); N2 learns alice's graph through us; N2 never receives a message back that it sent us (log check).
- (d) CLN interop: `ClnGossipTests` open a public channel both ways with CLN. CLN's `listchannels` shows our channel with our policy and `listnodes` our alias; we store CLN's 256/257/258; CLN queries us (`dev-query-scids` **(dev command availability to verify)**) and gets `full_information=1`.

### G4: Pathfinding
| Task | Files | Acceptance |
|---|---|---|
| **G4-T1** Pure pathfinder (§3.10) | `Domain/Routing/Pathfinding/{GraphPathfinder,PathCostModel,LiquidityEstimates}.cs` | Table tests: fee/CLTV exactness vs `HintRouteBuilder.BuildAlong` for the same path; disabled edge, htlc min/max, unknown even features, max hops, CLTV limit, exclusions; deterministic with a fixed RNG; 50k-channel synthetic graph < 50 ms per query (Release) |
| **G4-T2** `MissionControl` | `Application/Gossip/Routing/MissionControl.cs`: bounds from `PaymentRetryPolicy` outcomes (success → lower bound on each hop; `temporary_channel_failure` at hop i → upper bound; node failure → node penalty), 1 h half-life | Unit tests; state never persisted |
| **G4-T3** Planner integration (GG10) | `PaymentRoutePlanner.BuildPaths` adds graph candidates (`GraphPathSource`: k = 3 paths from each usable first hop, plus graph → hint entry node when the payee is private); `RoutingInfo` paths carry `HopLimits` (min/max); shadow CLTV | `PaymentHarnessTests` + a new `GraphPaymentHarnessTests` (Bob–Carol–David public, no hints): pays via the graph; MPP over two graph paths; retry around a failing hop |
| **G4-T4** `getroute` IPC | `ClientCommand` 19 | IPC test |

**Proof G4 (Docker):**
- (a) Our node has one public channel to alice. Carol creates an invoice **without** hints; `payinvoice` succeeds over us → alice → carol (2 LND forwarding hops' worth of policy).
- (b) Alice disables alice→carol (`UpdateChannelPolicy` with the disable flag, or `UpdateChanStatus` **(verify the RPC)**); the second payment goes us → alice → bob → carol.
- (c) Alice raises her fee mid-test without us re-syncing: the first attempt fails `fee_insufficient`, the retry succeeds (per-payment override), and our **graph** still has the old policy until the gossip update arrives (D9).
- (d) `getroute` agrees with the route LND reports.

### G5: Hardening
| Task | Files | Acceptance |
|---|---|---|
| **G5-T1** Memory limits and accounting (§3.8). **Partial** (G-D: caps and the estimate done; interning and `MaxMemoryMb` open, NL-373) | `GossipOptions` limits; interned node ids; `GraphStore` memory estimate | 200k-channel synthetic load under `Gossip:MaxMemoryMb` (default 512) **(measure; set the budget from the measurement)** |
| **G5-T2** Spam protection. **Done** (G-D) | rate limiters, misbehaviour score, `GraphBannedNodes`, per-peer queues | Fuzz-style test: a peer flooding invalid signatures is disconnected and banned; valid traffic from others unaffected |
| **G5-T3** Persistence performance. **Done** (G-D; 200k load 1.55 s, no indexes needed) | write-behind batching, bulk load at startup, optional `AddGossipIndexes` | Startup load of 200k channels < 10 s on SQLite **(measure)** |
| **G5-T4** `describegraph` + metrics. **Done** (G-D; IPC 20) | next free `ClientCommand` (18 is `listgraphchannels`, 19 goes to `getroute`); `Meter("NLightning.Gossip")` counters (received/accepted/rejected by reason, relayed, queue depth, chain lookups, sync durations) | IPC test; counters asserted in ingress tests |
| **G5-T5** Mainnet gate (D12). **Partial** (G-D: template done; 24 h soak running, NL-376) | defaults per network in `NodeConfigurationExtensions` template | Options test; opened only after a Mutinynet/signet soak (`docs/agents/MUTINYNET.md`) of 24 h with graph sync on and bitcoind load logged |

**Proof G5:** the synthetic load tests above; a 24 h signet or Mutinynet soak (graph size, RSS, RPC rate logged); the Docker suite (G1-G4) green three runs in a row (`scripts/run-gossip.sh 3`).

### Waves and lanes (for the multi-agent wave workflow)
| Wave | Lanes (file ownership) | Migration owner |
|---|---|---|
| **G-A** | **A1 wire** (G0-T1, T2, T5: `Domain/Protocol/{Payloads,Messages}`, `Infrastructure.Serialization`, `Tests.Utils/Vectors/Bolt7Vectors.cs`); **A2 schema** (G2-T3: `Infrastructure.Persistence*`, `Infrastructure.Repositories`, plus the channel columns for G1-T1); **A3 crypto + chain** (G0-T3, G1-T2, G2-T2: `Infrastructure.Bitcoin/{Gossip,Signers}`, `IBitcoinChainService`); **A4 addresses + pure** (G0-T4, G2-T1, G4-T1: `Domain/Gossip`, `Domain/Routing`, `RemoteAddressTlvConverter`) | A2 (`AddGossipGraph`) |
| **G-B** | **B1 public channels** (G1-T1, T3..T7: `Application/Gossip/Announcements`, `Application/Channels/Handlers`, `ChannelManager` case, Daemon/Client/IPC open); **B2 graph** (G2-T4..T6: `Application/Gossip/Graph`, `PeerService` gossip dispatch, `BlockchainMonitorService.OnBlockInputs`, IPC 17/18); **B3 Docker** (fixture, `scripts/run-gossip.sh`, Proofs G0/G1/G2) | none (schema landed in G-A) |
| **G-C** | **C1 sync/relay** (G3-T1..T4: `Application/Gossip/{Sync,Relay}`, `PeerService` query dispatch, `FeatureOptions`); **C2 routing** (G4-T2..T4 + G3-T5: `Payments/Routing`, `Payments/Send/PaymentRetryPolicy`, `Application/Gossip/Routing`, IPC 19); **C3 proofs** (G3 and G4 Docker, `ClnGossipTests`) | none |
| **G-D** | **D1 limits/spam** (G5-T1, T2); **D2 perf + metrics + `describegraph` IPC** (G5-T3, T4; migration owner if indexes are needed); **D3 gate + soak** (G5-T5) | D2 (only `AddGossipIndexes`) |

Seams to reconcile at integration:
- `PeerService` is touched by A1 (259 routing), B2 (256-258) and C1 (261-265): each lane edits only its `else if` arm.
- `ChannelUpdateService` is touched by B1 (public mode) and B2 (graph hand-off).
- `ClientCommand` numbers are assigned by the integrator.

---

## 6. Requirements traceability matrix

Status is **MISSING** at `368a057` unless noted; updated at `164289a` after wave G-A (PARTIAL = library code with tests, not wired) at `5bbfbb5` after wave G-B and at `4dc0f77` after wave G-C. Test prefixes: `DT/` Domain.Tests, `AT/` Application.Tests, `BT/` Infrastructure.Bitcoin.Tests, `ST/` Serialization.Tests, `IT/` Integration.Tests, `DK/` `IT/Docker/Gossip`, `CLN/` `IT/Docker/Interop/Cln/ClnGossipTests`.

### 6.1 Announcements
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B7-AS-01 | send 259 at 6 confirmations after channel_ready; not if private or after shutdown | DONE (G-B; not on mainnet unless allowed) | G1-T4 | `AT/Gossip/ChannelAnnouncementServiceTests`, DK G1 (a)(b) |
| B7-AS-02 | retransmit on reconnect; reply with ours | DONE (G-B) | G1-T4 | harness link-drop test, DK G1 (c) |
| B7-AS-03 | SCID mismatch warning; bad signatures; defer | DONE (G-B) | G1-T3 | `AT/Channels/Handlers/AnnouncementSignaturesMessageHandlerTests` |
| B7-AS-04 | queue 256 once both signature pairs are in | DONE (G-B) | G1-T4 | identical-bytes harness test |
| B7-CA-01 | layout, ordering, hash from offset 256 | DONE (G-B; DK G1 (a)) | G0-T1, G1-T4 | `ST/…ChannelAnnouncementPayloadTests`, vectors |
| B7-CA-02 | not before 6 confirmations; P2WSH | DONE (G-B) | G1-T4, G2-T2 | depth test |
| B7-CA-03 | receiver checks (sigs, chain, features, unspent P2WSH, depth) | DONE (G-B ingress) | G2-T1, G2-T2, G2-T4 | `DT/Gossip/GossipValidatorTests`, `BT/Gossip/FundingOutputLookupTests` |
| B7-CA-04 | blacklist conflicting announcements | DONE (G-B ingress; spam scoring and ban G-D) | G2-T4, G5-T2 | ingress test |
| B7-CA-05 | rebroadcast; stop when spent; forget at +72 | DONE (own 256 and spent/+72 pruning G-B; relay of others G-C, spent never relayed) | G2-T5, G3-T3 | pruner tests, `AT/Gossip/Relay/GossipRelayOthersTests`, DK G2 (c), DK G3 (c) |
| B7-NA-01/02 | node_announcement fields and sender rules | DONE (G-B, DK G1 (a)) | G0-T1, G0-T4, G1-T6 | codec tests, DK G1 (a) |
| B7-NA-03 | receiver rules | DONE (G-B ingress) | G2-T1, G2-T4 | validator table |
| B7-NA-04 | unknown even features: no connect/route/pay | PARTIAL (not routed through: pathfinder G-A, used by payments since G-C; connect/pay refusal not checked) | G4-T1, G4-T3 | pathfinder table |

### 6.2 channel_update
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B7-CU-01 | sender rules | DONE (public mode G-B; disable when the peer is offline > `Gossip:DisableAfter` G-C, NL-349) | G1-T5 | `ChannelUpdateServiceTests` + public and offline cases |
| B7-CU-01b | accept old fee for 10 min | MISSING **(check `HtlcForwardingPolicy`)** | G1-T5 | forwarding-policy test |
| B7-CU-02 | receiver rules incl. spent/disable, same-timestamp blacklist | DONE (G-B ingress) | G2-T4 | ingress tests |
| B7-CU-03 | min/max/capacity in routing | DONE (pathfinder G-A; planner `HopLimit`s G-C) | G4-T1, G4-T3 | pathfinder table, `AT/Payments/Routing/GraphRoutePlannerTests` |

### 6.3 Queries, relay, pruning, routing
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B7-Q-01 | querier rules (one outstanding, encoding 0, not for spent) | DONE (G-C; after a timeout the connection is not queried again, NL-365) | G3-T2 | `AT/Gossip/Sync/GossipSyncManagerTests`, DK G3 (a) |
| B7-Q-02 | `query_short_channel_ids` replies, flags, `full_information` | DONE (G-C, from the graph; NL-205) | G3-T1 | `AT/Gossip/Sync/QueryResponderTests`, CLN G3 (d) |
| B7-Q-03/04 | range query and reply rules; timestamps/checksums | DONE (G-C; checksums equal CLN's captured reply) | G3-T1, G3-T4 | `QueryResponderTests`, `RangeReplyCollectorTests`, `Bolt7QueryVectors` |
| B7-Q-05 | timestamp filter semantics, own gossip, ordering | DONE (G-C: filters sent after the sync, peers' filters applied by the relay; own gossip regardless of filters, G-B) | G3-T3, G1-T7 | relay tests, DK G3 (b) |
| B7-Q-06 | filter to a peer without `gossip_queries` | DONE (G-C, `(0xFFFFFFFF, 0)`) | G3-T2 | `GossipSyncManagerTests` |
| B7-RL-01 | no relay before filter; 60 s staggered flush; origin suppression; networks | DONE (G-C; edge case NL-368) | G3-T3 | `GossipRelayOthersTests`, DK G3 (c) |
| B7-PR-01 | spent + 72 removal; node pruning | DONE (G-B, DK G2 (c)) | G2-T5 | DK G2 (c) |
| B7-PR-02 | 2-week stale MAY prune | DONE (G-B unit tests; DK G2 (d) G-C, NL-356) | G2-T5 | mocked-clock test, DK G2 (d) |
| B7-RT-01 | fees + CLTV + random offset | DONE for graph routes (G-C; the shadow offset is not added to direct/hint routes) | G4-T1, G4-T3 | pathfinder tests, `GraphRoutePlannerTests`, DK goal proofs (b)(d) |
| B7-FEE-01 | fee formula | DONE (`HtlcForwardingPolicy`) | — | existing |
| B4-CU-01 | failure update not applied to the graph | DONE (per payment, `RouteConstraints.GraphPolicyOverrides`; G3-T5 refreshes through gossip) | G3-T5 keeps it | `GraphPaymentHarnessTests` retry; DK G4 (c) not written (NL-367) |

### 6.4 Interop proofs
| Proof | Peer | What it proves |
|---|---|---|
| DK G1 (a)(b) | LND 0.20 | LND validates and relays our 256/257/258 (both open directions) |
| DK G2 (a)(b) | LND | we validate LND's graph; persistence across restart |
| DK G3 (a)-(c) | LND + NLightning N2 | query sync, filter catch-up, relay without echo (G-C, green) |
| CLN G3 (d) + goal (e) | CLN v26.06.8 | CLN accepts our announcements; its own seeker's queries answered completely; payments through CLN and from CLN without hints (G-C, green) |
| DK G4 (a)-(d) | 3 LND | multi-hop payment without hints (goal proofs (b)(c)); `getroute` equals LND's route (goal (d)); disabled-flag avoidance and failure retry without graph pollution not written (NL-367) |

---

## 7. Mapping to other plans
| This plan | Other plan | Relation |
|---|---|---|
| G1-T1 | BOLT2 N1 open flow, NL-217/NL-236 | the public option; validator rule at `ChannelOpenValidator.cs:182-191` unchanged |
| G1-T5 | ABCD W1-E `ChannelUpdateService` | extended, not replaced; private channels unchanged |
| G2-T5 | BOLT5 O0 (monitor scan, header ring, `OnBlockDisconnected`) | reuses the per-block scan; reorg of a spend un-marks `SpentAtHeight` |
| G2-T2 | BOLT5 O6-T3 (`GetUnspentOutputAsync`, NL-293) | reuses the `gettxout` call |
| G4-T3 | ABCD W6-C `PaymentRoutePlanner`/`PaymentRetryPolicy` | graph is a third candidate source; retries unchanged |
| G3-T5 | ONION M3 `FailureInterpreter`, BOLT 4 | failure updates stay per payment (D9) |
| NL-054 | channel_ready TODOs | replaced by G1-T4 subscription + `GraphStore.AddOwnChannel` |
| NL-255 | ABCD roadmap §1.1 option A | closed by G1-T6 + Proof G1 (d) |

---

## 8. Risks
1. **DoS through gossip.**
   - *Threat:* invalid-signature floods, many valid-but-useless channels (cheap on signet/regtest), update storms.
   - *Mitigation:* bounded queues, per-channel and per-node rate limits, misbehaviour bans, graph caps (§3.8), relay only after validation.
   - *Residual:* on mainnet, real funded channels are the spam bound, which is why D12 gates mainnet until G5.
2. **bitcoind load.** Initial mainnet validation means tens of thousands of `getblock` verbosity-1 calls.
   - *Mitigation:* LRU by height, rate limit, concurrency cap, and `SkipUnavailable` for pruned nodes.
   - *Watch out:* `rpcworkqueue` saturation starving the chain monitor's own RPCs. Put gossip lookups on a separate `RPCClient` instance with its own concurrency; the monitor stays first.
3. **Privacy of public channels.** Announcing links our node id to on-chain UTXOs, and addresses reveal IPs.
   - *Mitigation:* private by default, no addresses by default (D11), per-channel opt-in, documented in the CLI help.
   - A public channel cannot be made private later (the announcement is permanent until close).
4. **Mainnet gate.** Public channels on mainnet also expose our forwarding to probing. Keep `EnableHtlcs` (BOLT5 O6-T4) and D12 independent: both must be open for a routing node.
5. **LND/CLN behaviours (unverified):**
   - LNUnit fixture channels are public;
   - `--trickledelay` flag and units;
   - LND only dumps gossip after `gossip_timestamp_filter`;
   - LND's keep-alive and rate-limit thresholds;
   - LND hints through public nodes only (NL-255);
   - `UpdateChanStatus` availability;
   - CLN `dev-query-scids`.
   Each proof logs versions and values; adjust the proofs, not the spec rules.
6. **Reorgs:**
   - An announced channel whose funding block is reorged: the SCID moves (BOLT5 O6-T3 `FundingReconfirmationHandler`), our old announcement is invalid, and we must re-run announcement_signatures for the new SCID. G1-T4 handles an SCID change by resetting the stored signatures (test required). A reorg deeper than 6 after announcing is accepted as rare.
   - Incoming channels whose funding block is reorged out are rechecked lazily at the next routing failure, or by the pruner on `OnBlockDisconnected`.
7. **Write-behind loss:** a crash loses up to 5 s of gossip. Harmless (re-sync), except our own `node_announcement` timestamp: that one is persisted synchronously before sending (monotonic timestamp).
8. **Memory:** raw bytes plus the in-memory graph at 200k channels is about 150-250 MB **(estimate; measure in G5-T1)**. Measured in G-D: 475 MiB store + 39 MiB per snapshot at 200k channels, about 130 MiB estimated for a mainnet-sized graph (NL-373). Mitigation: caps, interning, and keeping raw bytes only in the database (loaded on demand for relay and queries) if the measurement demands it.
9. **Pathfinding quality:** a naive a-priori probability may choose poor routes on mainnet. Mission control, MPP and retries bound the damage; tune the constants after the Mutinynet soak.
10. **Concurrency:** the graph has its own single-writer lock and never takes channel locks. Only G1 (our announcements) runs under channel locks and only enqueues there (root `CLAUDE.md` ordering rules). `GraphStore` readers use immutable snapshots.

### Critical Files for Implementation
- /Users/ms/nlightning/src/NLightning.Infrastructure/Node/Services/PeerService.cs
- /Users/ms/nlightning/src/NLightning.Application/Gossip/Services/ChannelUpdateService.cs
- /Users/ms/nlightning/src/NLightning.Application/Payments/Routing/PaymentRoutePlanner.cs
- /Users/ms/nlightning/src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs (plus `Wallet/BitcoinChainService.cs` for the SCID lookup)
- /Users/ms/nlightning/src/NLightning.Domain/Protocol/Payloads/ChannelUpdatePayload.cs (the codec pattern for 256/257/259)
- Also: /Users/ms/nlightning/src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs, /Users/ms/nlightning/src/NLightning.Infrastructure/Node/Services/GossipQueryResponder.cs, /Users/ms/nlightning/src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs, /Users/ms/nlightning/test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs, /Users/ms/nlightning/src/NLightning.Infrastructure.Serialization/Factories/MessageTypeSerializerFactory.cs
