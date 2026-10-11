# Migrating an LND or CLN node to NLightning: feasibility

> **Research only, not planned work.** The project owner decided on 2026-09-26 that no migration work will be done. This file keeps the feasibility findings for reference. Do not treat its staged plan as a roadmap item.

Research date 2026-09-26, at `wip/fafo` f0c2c28. Read-only; nothing in the repo was changed.

Labels: **[V]** checked against source or spec during this research, **[M]** from memory and not re-checked, **[I]** inference.

## TL;DR

| Level | Feasible? | Effort | Risk |
|---|---|---|---|
| a. Node identity only (close channels first, move the node key) | Yes | S (days) | Low |
| b. On-chain funds | Yes: sweep to NLightning (trivial); importing the wallet derivation is not worth it | XS / M | Low |
| d. SCB-style recovery (same node id, ask peers to force close, sweep our `to_remote`) | Yes for static_remotekey and anchor channels; no for LND simple-taproot channels | M (2-4 weeks per source implementation) | Medium, bounded: funds wait on the peer's force close, and in-flight HTLCs are lost |
| c. Live channel import (keep channels open) | Technically possible, but blocked today by anchors (O7), and each implementation needs its own state exporter | L-XL (months) | High: one mistake broadcasts a revoked state and the peer takes the whole channel |

Recommendation: build **a + d** (identity plus "recover from backup") for LND first, then CLN. Treat **c** as a research item. Consider it only after O7 anchors lands, only for quiescent channels (no HTLCs), and only behind a one-way import flag.

## 1. Key material and derivation

### LND
- **Seed.** aezeed (24 words; AEZ-encrypted entropy plus birthday and version) decodes to entropy that LND uses as the BIP32 master seed [M]. `chantools showrootkey` prints that root xprv [V: chantools README], so NLightning does not need an aezeed implementation to use it.
- **Key families.** Paths are `m/1017'/coin'/family'/0/index`, with `BIP0043Purpose = 1017` [V: `keychain/derivation.go`]. The families: 0 MultiSig (funding key), 1 RevocationBase, 2 HtlcBase, 3 PaymentBase, 4 DelayBase, 5 RevocationRoot, 6 NodeKey, 7 BaseEncryption (SCB encryption key), 8 TowerSession, 9 TowerID [V].
- **Node key.** Family 6, index 0: `m/1017'/coin'/6'/0/0` [M, consistent with `derivekey`]. It can be derived from the seed alone.
- **Per-channel keys.** Each basepoint comes from `DeriveNextKey(family)`, which advances a per-family counter, and the resulting `KeyLocator` (family, index) is stored per channel [V/I]. The keys are seed-derivable, but you must know the indices. They are stored in `channel.db` and in the SCB (`LocalChanCfg` key descriptors [V: `chanbackup/single.go`]). Without either, the indices have to be brute-forced; `chantools rescueclosed` does this kind of search [V: README].
- **Per-commitment secrets.** The shachain root is `ECDH(revocation-root key at family 5 index i, our multisig pubkey)`, and `shachain.NewRevocationProducer(root)` builds on it [V: `lnwallet/revocation_producer.go`]. Index 0 is skipped [V]. The algorithm is the BOLT 3 shachain, so our secrets are seed-derivable if you have `ShaChainRootDesc` (also in the SCB) [V].
- **Only in `channel.db`:** commitment heights on both sides; both current commitments with the peer's signatures; pending HTLCs and the update log; the peer's next and current per-commitment points; the **peer's revocation secrets** (compact shachain store); the revocation log with per-state HTLC data for penalties; the switch circuits, invoices and preimages; the fee rate; and the negotiated channel type. None of this can be derived.

### CLN
- **Secret.** `hsm_secret` is 32 raw bytes (optionally encrypted with `hsmtool encrypt`). Since v25.12, new nodes start from a BIP39 mnemonic [V: CLN docs]. `hsmtool generatehsm` also builds it from BIP39 words [V].
- **Node key.** `HKDF-SHA256(hsm_secret, salt=0,1,…, info="nodeid")` until a valid key results [V: `hsmd/libhsmd.c`].
- **Wallet.** `HKDF(…, "bip32 seed")` gives the BIP32 master, and wallet keys sit at `m/0/0/i` [V]. `hsmtool dumponchaindescriptors` exports it [V].
- **Per-channel keys.** `channel_base = HKDF(hsm_secret, "peer seed")`, then `channel_seed = HKDF(channel_base, salt = peer_node_id ‖ dbid, info "per-peer seed")` [V]. From that, `derive_keys` = `HKDF(seed, "c-lightning")` fills funding, revocation, htlc, payment, delayed and shaseed **in that order** [V: `common/derive_basepoints.c`]. Per-commitment secrets are `shachain_from_seed(shaseed, index)` [V].
- **What you need besides the secret.** Only the peer id and the small integer `dbid`. `hsmtool guesstoremote` brute-forces the dbid [V]. `derivetoremote` and `dumpcommitments` export the keys and secrets per channel [V].
- **`lightningd.sqlite3`** holds everything stateful, the same list as for LND.
- **`emergency.recover`.** Every `scb_chan` has id (dbid), cid, node_id, addr, funding outpoint, funding sats and channel_type, plus TLVs for the peer's shachain, the peer's basepoints, the opener and `remote_to_self_delay` [V: `common/scb_wire.csv`]. It is encrypted with a key from `hsm_secret` [M]. `hsmtool getemergencyrecover` decodes it [V].

### NLightning today
- **Node key.** The node key *is* the master private key (`SecureKeyManager.GetNodeKeyPair`). `GetMasterKey()` rebuilds the master as `ExtKey(privkey, chaincode = genesis hash)`, which throws away the BIP32 chain code from the mnemonic (`src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs:557-560`). This is already tracked as **NL-159** (non-standard master derivation). Two consequences: (1) you cannot put an arbitrary node key under an unrelated channel root, and (2) NLightning's own seed cannot be restored in other wallets.
- **Channel keys.** Channel key `m/6425'/0'/0'/0/i` with hardened children 0'-5' (funding, revocation, payment, delayed, htlc, per-commitment seed). The only per-channel key identity is `ChannelSigningInfo.ChannelKeyIndex` (`LocalLightningSigner.cs:32-37, 118-139`).
- **Seams already in place.** The basepoint secrets come from the protected virtual `Get{Revocation,Payment,DelayedPayment,Htlc}BasepointSecret` (tests override them with Appendix C secrets). Funding and per-commitment derivation go through `GenerateFundingPrivateKey(index)` and `DerivePerCommitmentSecret(index, n)`.

**Compatibility mode [I].** A seed-driven "LND mode" or "CLN mode" is feasible, since both schemes are a few HKDF or BIP32 calls. A per-channel **imported key record** is the more general design, and it works for both: `ChannelKeySource = Native(index) | Imported(funding, revocation, payment, delayed, htlc secrets, shaseed or LND shachain root)`, encrypted at rest with the key-file password. The signer would branch on the source instead of on `ChannelKeyIndex`. VLS (Validating Lightning Signer) is prior art: it signs for CLN (native derivation) and LDK and has an LND-style derivation mode [M; I could not re-fetch its source].

## 2. Levels of migration

### a. Node identity only
- **Steps.** Close every channel on the old node (cooperative if possible), stop it for good, and take the node private key from it:
  - LND: `chantools derivekey --path m/1017'/0'/6'/0/0 --neuter=false`
  - CLN: derive it with the `nodeid` HKDF from `hsm_secret` (≈20 lines of C#), or read it from `hsmtool` output.
- **What NLightning needs.** A key-file variant that holds the **node key separately** from the channel/wallet root. Today the node key equals the master, so importing a foreign node key as the "master" would work but would tie all future channel keys to it. Fix NL-159 in the same change: a key file v3 with an optional `ImportedNodeKey` and a normal BIP32 root for channels and wallet.
- **Gossip.** Our first `node_announcement` and `channel_update`s need timestamps newer than the old node's last ones [I]. Peers keep the old addresses until the new announcement spreads.
- **Risk.** Low. Once no channels are left, the identity alone carries no funds.

### b. On-chain funds
The simplest path is to send the old wallet's balance to an NLightning deposit address. Importing LND's BIP84/86 accounts (`m/84'/0'/0'`, `m/86'/0'/0'` plus LND's internal `m/1017'` keys) or CLN's `m/0/0/i` as watched descriptors needs a multi-root wallet, which the UTXO repository and `IBlockchainMonitor` do not model today. It is not worth the work.

### c. Live channel import (keep channels open)
- **Required state, per channel.** Funding outpoint and SCID; channel_type and negotiated features (static_remotekey, anchors, taproot, scid_alias); both sides' params (dust, reserve, `to_self_delay`, max HTLCs, `max_htlc_value_in_flight`); feerate; our and their commitment numbers; our current commitment and the peer's signature on it plus HTLC signatures; the peer's current and next per-commitment points; our shachain position and root; the peer's revocation secrets store; the revocation log (for penalties on every old state); the pending update logs (adds, fulfills, fails not yet irrevocable); and HTLC origins and preimages for any in-flight forward or payment. In NLightning this maps to `ChannelModel` + `ChannelCommitments` snapshot + `RevokedCommitments` + peer shachain (NL-136) + `HtlcOrigin`/circuits.
- **Exporters.**
  - LND: `chantools dumpchannels` prints channel.db as text [V: README], which is debug output rather than a stable format. A real exporter would have to read bbolt/SQL `channeldb` with LND's own Go code, i.e. a small Go tool against `channeldb.OpenChannel`.
  - LND: `lncli exportchanbackup` gives the SCB, which does not contain the state [V].
  - CLN: `listpeerchannels` gives no secrets or signatures. `hsmtool dumpcommitments` gives our secrets [V]. The rest has to come from SQL on `channels`, `channel_htlcs`, `shachains`/`shachain_known`, `channel_funding_inflights`… [M].
  - I found no existing tool, for any implementation pair, that moves a live channel across implementations.
- **Hard blockers in NLightning [V].**
  1. Anchors: most LND and CLN channels opened in recent years are `option_anchors_zero_fee_htlc_tx` [M]. NLightning has anchor transaction building (Appendix F vectors) but no O7 CPFP and bumping, and the feature is experimental-gated (`BOLT_COVERAGE.md` line 82). An imported anchor channel could not be force-closed safely under fee pressure.
  2. LND simple-taproot channels (SCB version `SimpleTaprootVersion` [V]) need MuSig2 and taproot scripts, which NLightning does not have.
  3. `ChannelDbRepository` refuses legacy HTLC shapes (NL-025). An import would have to write a fresh engine snapshot through `IChannelStateDbRepository`, so it must reproduce `ChannelCommitments` exactly, including the `SentCommitDiff` for retransmission.
- **Why it is dangerous.** If the old node ever runs again, or an old backup of it does, or a watchtower client session of the old node keeps going, it can broadcast a commitment NLightning has since revoked. The peer then sweeps the **whole channel** with the penalty (BOLT 5). The reverse also holds: if the import is one state behind (a commitment_signed in flight during the export), NLightning's first `channel_reestablish` is "behind". A correct NLightning then declares data loss and cannot broadcast. An incorrect one broadcasts a revoked state and loses everything. LND's guidance says it outright: never run two nodes on the same seed, and never restore an old channel.db [V: LND migrating doc; chantools README].

### d. The safe middle path: SCB-style recovery
The node connects **as the same node id** and sends an all-zero `channel_reestablish` (`next_commitment_number = 0`). `chantools triggerforceclose` does exactly this, and falls back to a crafted error for some CLN versions [V: `cmd/chantools/triggerforceclose.go:426`]. Per BOLT 2, a receiver that gets `next_commitment_number = 0` after sending a commitment_signed fails the channel and broadcasts its latest commitment [V, paraphrased; NLightning's own reestablish handler implements that receiver rule]. The peer's commitment pays our `to_remote` to the **payment basepoint**: P2WPKH for static_remotekey, or P2WSH `<key> CHECKSIGVERIFY 1 CSV` for anchors. With static_remotekey neither side tweaks that key, so the key comes from the SCB key locator (LND) or from `hsm_secret` + dbid (CLN).

**What NLightning already has [V]:**
- Data-loss detection: `ReestablishPlanner`, `ChannelModel.DataLossDetected`, and the signer's `MarkDataLoss`, which refuses all commitment signing afterwards.
- `RemoteCommitResolver.AddDataLossRowsAsync`, which finds and sweeps `to_remote` of a commitment it cannot rebuild. It handles anchors (`FindPaymentToRemote(..., OptionAnchorOutputs)`).
- `SignSweepInput` with `SweepKeyKind.Payment` for both P2WPKH and the anchor script.
- The watcher, the executor and the sweep RBF scheduler.

**What is missing [I]:**
1. A backup decoder:
   - LND SCB: chacha20poly1305, key = SHA256 of the family-7 key [V]. The key can come from the aezeed root xprv, or the user can run `chantools dumpbackup` and pass the JSON.
   - CLN `emergency.recover`: or run `hsmtool getemergencyrecover`.
2. A "recovered channel" import that creates a `ChannelModel` in a new terminal-ish state (for example `Recovering`), with `DataLossDetected = true` persisted from the start. It carries the peer id and addresses, the funding outpoint and capacity, the channel type, the local payment basepoint, and for CLN the peer's basepoints and shachain from the TLVs (which make a penalty possible if the peer broadcasts an old state). It also needs a funding watch.
3. An imported-key source in the signer. Its first use is the payment basepoint secret; the others follow later.
4. Sending the zero `channel_reestablish`. `PeerManager` would dial the peer with the stored addresses; the reestablish planner would get a "recovered" branch.
5. Graceful handling of a peer that is gone. The funds then stay locked until the peer comes back; `chantools zombierecovery` is the LND fallback.

The old node must be **dead before** NLightning connects as its identity: two live connections with the same node id fight over the peer's connection, and the old node could still update the channel.

**Losses under path d.** HTLCs in flight at the time, which are only claimable with preimages or state we do not have. The peer's commitment fee and CSV delays. For anchors, our anchor output: sweeping it needs the funding key, which is in the SCB too.

**Taproot channels are not covered.**

## 3. Prior art

- **chantools** (lightninglabs) [V]: `dumpchannels`, `dumpbackup`, `showrootkey`, `derivekey`, `rescueclosed`, `sweepremoteclosed`, `triggerforceclose`, `zombierecovery`, `fakechanbackup`, `scbforceclose` ("EXTREMELY DANGEROUS"), `signrescuefunding`. This is the reference for recovering funds from an LND node without its own software.
- **CLN hsmtool** [V]: `getnodeid`, `dumponchaindescriptors`, `derivetoremote`, `guesstoremote`, `dumpcommitments`, `getemergencyrecover`, `generatehsm` (BIP39), `getcodexsecret`. CLN's `emergencyrecover` RPC plus `emergency.recover` and peer storage (it backs the encrypted emergency backup up with peers, still experimental) [V: CLN docs].
- **LND** [V/M]: SCB `lncli restorechanbackup` uses the same "restored channel, data-loss reestablish, wait for the peer's close, sweep `to_remote`" flow as path d. LND's own migration guide covers only LND→LND by moving `~/.lnd` whole, and says never to run two nodes with one seed [V].
- **Cross-implementation.** I found no tool, public write-up or protocol work that moves open channels between LND, CLN, Eclair or LDK [V: searches came back empty]. The community approach is "close channels, restore the seed on the new implementation, reopen" [V]. BOLT peer storage (`option_provide_storage`, bits 42/43) is the standard route for portable encrypted blobs, but the blob format belongs to each implementation. NLightning has the feature bit but no messages (NL-010).
- **VLS** [M]: multiple derivation styles for one signer. It is the closest model for a compatibility mode.

## 4. Safety: ways to lose funds, and guardrails

**How funds get lost:**
1. The old node restarts after the import, or an old backup of it is restored: a revoked state is broadcast and the peer takes the penalty (level c). Under level d it only makes the recovery messier.
2. An import that is off by one state, or a double sign (both nodes sign different commitment N+1): a later broadcast of either is either revoked or not what the peer has. A mismatch can make NLightning broadcast a revoked commitment.
3. The old node's watchtower sessions keep "protecting" a state the new node has moved past. That is harmless for us but wastes effort; the reverse, no tower for the new node, is a monitoring gap [I].
4. Fee, feature or type mismatches: anchors without CPFP, taproot unsupported, a feerate or dust limit imported wrong gives a commitment the peer's signature does not match. NLightning's verification would catch that last case at `VerifyLocalCommitment`; the others it would not.
5. HTLC origins or preimages lost during the import: an incoming HTLC we fulfilled downstream but cannot claim upstream.
6. An address or UTXO clash if both wallets stay live on the same keys (level b with a derivation import).

**Guardrails NLightning would need [I]:**
- A persisted, one-way `ImportedFrom { Implementation, SourceNodeId, ImportedAt, SourceStateHash }` on every imported channel and on the key file.
- An operator attestation, required interactively, that the old node is stopped and its data moved aside. An optional check: refuse to start if the old node's RPC or port still answers, or if the peer's `channel_reestablish` shows the peer has seen a newer state than the import (that path goes to data loss, never a broadcast).
- For level c: a quiescent export only (no HTLCs, no pending updates; STFU/`option_quiesce` on the old node first), and verify each imported commitment against the peer's signature before accepting it. Keep the signer's S1/I4 invariants seeded from the imported numbers.
- For level d: `DataLossDetected` in the first save, which makes the signer refuse all commitment signing (this already exists).
- Channel types NLightning cannot resolve on chain are refused at import (taproot now, anchors until O7).

## 5. Recommendation and staged plan

**Stage 1: identity and LND fund recovery (≈3-5 weeks).**
- Fix NL-159: a key file v3 with a standard BIP32 root and an optional separate imported node key. Add an `nltg import-node-key` flow for the daemon's first run.
- Add an imported per-channel key record and a signer branch. Touch points: `LocalLightningSigner` `Get*BasepointSecret`, `GenerateFundingPrivateKey`, `DerivePerCommitmentSecret`, `ChannelSigningInfo`, `IChannelSigningInfoSource`.
- Add an LND SCB import (input: `chantools dumpbackup` JSON, or the decrypted SCB plus the root xprv from `showrootkey`) and a `Recovering` state (a new `ChannelState` value between Failed and OnchainResolving, keeping the numbering monotonic). Touch points: `ChannelModel`, an EF migration on all three providers, and `PeerManager` reconnect with the stored addresses.
- Add a zero `channel_reestablish` sender (`ReestablishPlanner` / `ChannelReestablishMessageHandler`). The existing `OnchainChannelWatcher` → `RemoteCommitResolver` data-loss path sweeps `to_remote`.
- IPC: `importbackup` (next free `ClientCommand` 21) and reuse `pendingsweeps`.
- Docker proof: an LND node with open channels, then SCB, then NLightning takes over the id, then peers force close, then sweeps land in the NLightning wallet.

**Stage 2: CLN (≈2-3 weeks).** Add the `hsm_secret`/BIP39 node-key and per-channel HKDF derivation (a small, vector-testable C# port of `derive_keys` and `get_channel_seed`), an `emergency.recover` import (or the `hsmtool getemergencyrecover` output), and use the peer basepoints and shachain from its TLVs so a penalty is possible against an old broadcast. Test it with the existing CLN interop harness.

**Stage 3: live import (research, ≥2-3 months, after O7 anchors).** An offline Go exporter for LND (`channeldb`) and a SQL exporter for CLN, both producing a neutral JSON. Import only quiescent channels. Build the `ChannelCommitments` snapshot and verify it against the peer's stored signatures. The first reconnect runs reestablish in "verify only" mode. Taproot stays out of scope until NLightning has MuSig2 channels. Given the penalty exposure and the small gain over "recover then reopen", I would keep this off the roadmap unless an operator with many large channels asks for it.

## Sources

- LND key families: https://github.com/lightningnetwork/lnd/blob/master/keychain/derivation.go
- LND revocation producer: https://github.com/lightningnetwork/lnd/blob/master/lnwallet/revocation_producer.go
- LND SCB format: https://github.com/lightningnetwork/lnd/blob/master/chanbackup/single.go
- LND migrating guide: https://docs.lightning.engineering/lightning-network-tools/lnd/migrating-lnd
- chantools: https://github.com/lightninglabs/chantools ; triggerforceclose: https://github.com/lightninglabs/chantools/blob/master/cmd/chantools/triggerforceclose.go
- CLN hsmd: https://github.com/ElementsProject/lightning/blob/master/hsmd/libhsmd.c
- CLN basepoints: https://github.com/ElementsProject/lightning/blob/master/common/derive_basepoints.c
- CLN SCB wire: https://github.com/ElementsProject/lightning/blob/master/common/scb_wire.csv
- CLN hsmtool: https://github.com/ElementsProject/lightning/blob/master/doc/lightning-hsmtool.8.md
- CLN recovery / HSM secret docs: https://docs.corelightning.org/docs/recovery , https://docs.corelightning.org/docs/hsm-secret , https://docs.corelightning.org/docs/backup
- BOLT 2 (channel_reestablish): https://github.com/lightning/bolts/blob/master/02-peer-protocol.md
- BOLT 3 (shachain, key derivation) and BOLT 5 (penalties): https://github.com/lightning/bolts
- VLS: https://gitlab.com/lightning-signer/validating-lightning-signer
- NLightning: `src/NLightning.Infrastructure.Bitcoin/Managers/SecureKeyManager.cs`, `src/NLightning.Infrastructure.Bitcoin/Signers/LocalLightningSigner.cs`, `src/NLightning.Application/Onchain/Resolvers/RemoteCommitResolver.cs:352-389`, `docs/agents/ISSUES.md` NL-010, NL-025, NL-159, `docs/agents/BOLT_COVERAGE.md`
