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
- **Status (2026-09-26, `wip/fafo` @ `368a057`, ABCD wave 7 in progress, with uncommitted W7 changes in the main checkout):**
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
| GG2 | 259 is a **channel** message (`channel_id`) but is dispatched as peer gossip and dropped: no `ChannelManager` case, no lock | `PeerService.cs:353` | new | G0-T2, G1-T3 |
| GG3 | No way to request a public channel. The fundee ignores `announce_channel` (not stored), so an LND-opened **public** channel is silently treated as private and LND never gets our `announcement_signatures` | `OpenChannelClientHandler.cs:110-112`; no `ChannelFlags` use in `Application/Channels/Handlers/OpenChannel1MessageHandler.cs` (grep) | NL-236 + new | G1-T1 |
| GG4 | No funding-key signature API for `bitcoin_signature` | `ILightningSigner.cs` | new | G1-T2 |
| GG5 | Address descriptors: Tor v3 decodes 36 bytes, DNS length off by one; only IPv4 is tested | `src/NLightning.Infrastructure/Protocol/Tlv/Converters/RemoteAddressTlvConverter.cs:51-54,95-103` | NL-008 | G0-T4 |
| GG6 | Our updates always set `dont_forward`; a public channel needs it clear and the real SCID | `ChannelUpdateService.cs:335-340` | new | G1-T5 |
| GG7 | The peer's updates live in memory only and are never relayed | `ChannelUpdateService.cs:52-53,73` | NL-099 | G2-T4 |
| GG8 | No SCID → funding-output lookup (height → txid list → `gettxout`) | `IBitcoinChainService.cs` | new | G2-T2 |
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
| 18 | `DescribeGraph`: counts, last sync per peer, orphans, queue depths | G5-T4 |
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
| **G2-T6** IPC `listnodes`, `listgraphchannels` | `ClientCommand` 16/17, DTOs, Daemon handlers, CLI printers | IPC round trips (`Daemon.Tests`) |

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
| **G5-T1** Memory limits and accounting (§3.8) | `GossipOptions` limits; interned node ids; `GraphStore` memory estimate | 200k-channel synthetic load under `Gossip:MaxMemoryMb` (default 512) **(measure; set the budget from the measurement)** |
| **G5-T2** Spam protection | rate limiters, misbehaviour score, `GraphBannedNodes`, per-peer queues | Fuzz-style test: a peer flooding invalid signatures is disconnected and banned; valid traffic from others unaffected |
| **G5-T3** Persistence performance | write-behind batching, bulk load at startup, optional `AddGossipIndexes` | Startup load of 200k channels < 10 s on SQLite **(measure)** |
| **G5-T4** `describegraph` + metrics | `ClientCommand` 18; `Meter("NLightning.Gossip")` counters (received/accepted/rejected by reason, relayed, queue depth, chain lookups, sync durations) | IPC test; counters asserted in ingress tests |
| **G5-T5** Mainnet gate (D12) | defaults per network in `NodeConfigurationExtensions` template | Options test; opened only after a Mutinynet/signet soak (`docs/agents/MUTINYNET.md`) of 24 h with graph sync on and bitcoind load logged |

**Proof G5:** the synthetic load tests above; a 24 h signet or Mutinynet soak (graph size, RSS, RPC rate logged); the Docker suite (G1-G4) green three runs in a row (`scripts/run-gossip.sh 3`).

### Waves and lanes (for the multi-agent wave workflow)
| Wave | Lanes (file ownership) | Migration owner |
|---|---|---|
| **G-A** | **A1 wire** (G0-T1, T2, T5: `Domain/Protocol/{Payloads,Messages}`, `Infrastructure.Serialization`, `Tests.Utils/Vectors/Bolt7Vectors.cs`); **A2 schema** (G2-T3: `Infrastructure.Persistence*`, `Infrastructure.Repositories`, plus the channel columns for G1-T1); **A3 crypto + chain** (G0-T3, G1-T2, G2-T2: `Infrastructure.Bitcoin/{Gossip,Signers}`, `IBitcoinChainService`); **A4 addresses + pure** (G0-T4, G2-T1, G4-T1: `Domain/Gossip`, `Domain/Routing`, `RemoteAddressTlvConverter`) | A2 (`AddGossipGraph`) |
| **G-B** | **B1 public channels** (G1-T1, T3..T7: `Application/Gossip/Announcements`, `Application/Channels/Handlers`, `ChannelManager` case, Daemon/Client/IPC open); **B2 graph** (G2-T4..T6: `Application/Gossip/Graph`, `PeerService` gossip dispatch, `BlockchainMonitorService.OnBlockInputs`, IPC 16/17); **B3 Docker** (fixture, `scripts/run-gossip.sh`, Proofs G0/G1/G2) | none (schema landed in G-A) |
| **G-C** | **C1 sync/relay** (G3-T1..T4: `Application/Gossip/{Sync,Relay}`, `PeerService` query dispatch, `FeatureOptions`); **C2 routing** (G4-T2..T4 + G3-T5: `Payments/Routing`, `Payments/Send/PaymentRetryPolicy`, `Application/Gossip/Routing`, IPC 19); **C3 proofs** (G3 and G4 Docker, `ClnGossipTests`) | none |
| **G-D** | **D1 limits/spam** (G5-T1, T2); **D2 perf + metrics + IPC 18** (G5-T3, T4; migration owner if indexes are needed); **D3 gate + soak** (G5-T5) | D2 (only `AddGossipIndexes`) |

Seams to reconcile at integration:
- `PeerService` is touched by A1 (259 routing), B2 (256-258) and C1 (261-265): each lane edits only its `else if` arm.
- `ChannelUpdateService` is touched by B1 (public mode) and B2 (graph hand-off).
- `ClientCommand` numbers are assigned by the integrator.

---

## 6. Requirements traceability matrix

Status is **MISSING** at `368a057` unless noted. Test prefixes: `DT/` Domain.Tests, `AT/` Application.Tests, `BT/` Infrastructure.Bitcoin.Tests, `ST/` Serialization.Tests, `IT/` Integration.Tests, `DK/` `IT/Docker/Gossip`, `CLN/` `IT/Docker/Interop/Cln/ClnGossipTests`.

### 6.1 Announcements
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B7-AS-01 | send 259 at 6 confirmations after channel_ready; not if private or after shutdown | MISSING | G1-T4 | `AT/Gossip/ChannelAnnouncementServiceTests`, DK G1 (a)(b) |
| B7-AS-02 | retransmit on reconnect; reply with ours | MISSING | G1-T4 | harness link-drop test, DK G1 (c) |
| B7-AS-03 | SCID mismatch warning; bad signatures; defer | MISSING (dropped, GG2) | G1-T3 | `AT/Channels/Handlers/AnnouncementSignaturesMessageHandlerTests` |
| B7-AS-04 | queue 256 once both signature pairs are in | MISSING | G1-T4 | identical-bytes harness test |
| B7-CA-01 | layout, ordering, hash from offset 256 | MISSING | G0-T1, G1-T4 | `ST/…ChannelAnnouncementPayloadTests`, vectors |
| B7-CA-02 | not before 6 confirmations; P2WSH | MISSING | G1-T4, G2-T2 | depth test |
| B7-CA-03 | receiver checks (sigs, chain, features, unspent P2WSH, depth) | MISSING | G2-T1, G2-T2, G2-T4 | `DT/Gossip/GossipValidatorTests`, `BT/Gossip/FundingOutputLookupTests` |
| B7-CA-04 | blacklist conflicting announcements | MISSING | G2-T4, G5-T2 | ingress test |
| B7-CA-05 | rebroadcast; stop when spent; forget at +72 | MISSING | G2-T5, G3-T3 | pruner tests, DK G2 (c) |
| B7-NA-01/02 | node_announcement fields and sender rules | MISSING | G0-T1, G0-T4, G1-T6 | codec tests, DK G1 (a) |
| B7-NA-03 | receiver rules | MISSING | G2-T1, G2-T4 | validator table |
| B7-NA-04 | unknown even features: no connect/route/pay | MISSING | G4-T1 | pathfinder table |

### 6.2 channel_update
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B7-CU-01 | sender rules | PARTIAL (W1-E, private only) | G1-T5 | `ChannelUpdateServiceTests` + public cases |
| B7-CU-01b | accept old fee for 10 min | MISSING **(check `HtlcForwardingPolicy`)** | G1-T5 | forwarding-policy test |
| B7-CU-02 | receiver rules incl. spent/disable, same-timestamp blacklist | PARTIAL (own channels, memory only) | G2-T4 | ingress tests |
| B7-CU-03 | min/max/capacity in routing | MISSING | G4-T1 | pathfinder table |

### 6.3 Queries, relay, pruning, routing
| ID | Requirement | Status | Task | Test |
|---|---|---|---|---|
| B7-Q-01 | querier rules (one outstanding, encoding 0, not for spent) | MISSING (we never query) | G3-T2 | sync state-machine tests |
| B7-Q-02 | `query_short_channel_ids` replies, flags, `full_information` | PARTIAL (empty, NL-205) | G3-T1 | `AT/Gossip/Sync/QueryResponderTests` |
| B7-Q-03/04 | range query and reply rules; timestamps/checksums | PARTIAL (one empty reply) | G3-T1, G3-T4 | chunking tests, CRC32C vector |
| B7-Q-05 | timestamp filter semantics, own gossip, ordering | MISSING (ignored) | G3-T3, G1-T7 | relay tests, DK G3 (b) |
| B7-Q-06 | filter to a peer without `gossip_queries` | MISSING | G3-T2 | unit |
| B7-RL-01 | no relay before filter; 60 s staggered flush; origin suppression; networks | MISSING | G3-T3 | relay tests, DK G3 (c) |
| B7-PR-01 | spent + 72 removal; node pruning | MISSING | G2-T5 | DK G2 (c) |
| B7-PR-02 | 2-week stale MAY prune | MISSING | G2-T5 | mocked-clock test, DK G2 (d) |
| B7-RT-01 | fees + CLTV + random offset | MISSING | G4-T1, G4-T3 | pathfinder tests, DK G4 |
| B7-FEE-01 | fee formula | DONE (`HtlcForwardingPolicy`) | — | existing |
| B4-CU-01 | failure update not applied to the graph | DONE (per payment, `RouteConstraints`) | G3-T5 keeps it | DK G4 (c) |

### 6.4 Interop proofs
| Proof | Peer | What it proves |
|---|---|---|
| DK G1 (a)(b) | LND 0.20 | LND validates and relays our 256/257/258 (both open directions) |
| DK G2 (a)(b) | LND | we validate LND's graph; persistence across restart |
| DK G3 (a)-(c) | LND + NLightning N2 | query sync, filter catch-up, relay without echo |
| CLN G3 (d) | CLN v26.06.8 | CLN accepts our announcements; queries against us |
| DK G4 (a)-(d) | 3 LND | multi-hop payment without hints; disabled-flag avoidance; failure retry without graph pollution |

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
8. **Memory:** raw bytes plus the in-memory graph at 200k channels is about 150-250 MB **(estimate; measure in G5-T1)**. Mitigation: caps, interning, and keeping raw bytes only in the database (loaded on demand for relay and queries) if the measurement demands it.
9. **Pathfinding quality:** a naive a-priori probability may choose poor routes on mainnet. Mission control, MPP and retries bound the damage; tune the constants after the Mutinynet soak.
10. **Concurrency:** the graph has its own single-writer lock and never takes channel locks. Only G1 (our announcements) runs under channel locks and only enqueues there (root `CLAUDE.md` ordering rules). `GraphStore` readers use immutable snapshots.

### Critical Files for Implementation
- /Users/ms/nlightning/src/NLightning.Infrastructure/Node/Services/PeerService.cs
- /Users/ms/nlightning/src/NLightning.Application/Gossip/Services/ChannelUpdateService.cs
- /Users/ms/nlightning/src/NLightning.Application/Payments/Routing/PaymentRoutePlanner.cs
- /Users/ms/nlightning/src/NLightning.Infrastructure.Bitcoin/Wallet/BlockchainMonitorService.cs (plus `Wallet/BitcoinChainService.cs` for the SCID lookup)
- /Users/ms/nlightning/src/NLightning.Domain/Protocol/Payloads/ChannelUpdatePayload.cs (the codec pattern for 256/257/259)
- Also: /Users/ms/nlightning/src/NLightning.Daemon/Handlers/OpenChannelClientHandler.cs, /Users/ms/nlightning/src/NLightning.Infrastructure/Node/Services/GossipQueryResponder.cs, /Users/ms/nlightning/src/NLightning.Domain/Bitcoin/Interfaces/ILightningSigner.cs, /Users/ms/nlightning/test/NLightning.Integration.Tests/Fixtures/LightningRegtestNetworkFixture.cs, /Users/ms/nlightning/src/NLightning.Infrastructure.Serialization/Factories/MessageTypeSerializerFactory.cs
