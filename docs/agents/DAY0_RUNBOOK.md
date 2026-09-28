# Day-0 runbook: a dual-funded public channel between two NLightning nodes, then splices

Status: **the Mutinynet phase passed** on 2026-09-28 (dry run with two NLightning nodes on the owner's machine, the
live NLightningFAFO upgraded in place as U and a new NLightningFAFO2 as N): a first pass on `d5d8b184` found NL-517
(fixed in `d5be5b73`), the clean pass on `91591787` ran every step of §2.2 including the splice RBF drill (results in
§5). A second dry run on the same nodes with the build compiled for .NET 11 (`2a209a5e`, net11.0) passed the same
day, with an RBF of the dual-funded open by both sides (§5, "Mutinynet dry run 2"). The rehearsal with Nick's own node
and mainnet (§3) are still to do. Written on 2026-09-27 in wave sp2 (lane
SP2-F). Items marked **(unverified)** have not been checked on any live network. Never paste secrets (RPC password,
key password) into this file, a shell history, a log or a chat.

**Goal (day 0).** The owner's node (**U**) and Nick's node (**N**) open a **dual-funded public** channel on mainnet
(both contribute), pay both ways, then **splice in** and **splice out**, with a backup after every step. Mutinynet
first (§2), mainnet after (§3).

**Proofs this runbook stands on** (regtest, Docker, both in `test/NLightning.Integration.Tests/Docker/Day0/`, run with
`scripts/run-gossip.sh 1 Release -namespace NLightning.Integration.Tests.Docker.Day0`):

| Proof | What it shows |
|---|---|
| `Day0FlowTests` | Two NLightning nodes A and B, LND alice watching: (1) dual-funded public open, both contribute, alice has the `channel_announcement` and both policies at 6 confirmations; (2) payments A→B, B→A, alice→A→B; (3) A splices in: lock, new SCID announced, old SCID gone from alice's graph (blocks mined up to BOLT 7's 72-block delay; the test prints the delay LND used); (4) B splices out to an address: same, and the address holds the amount in a confirmed tx; (5) B crashes before its `tx_signatures` of a splice reach the wire, restarts, retransmits them on `channel_reestablish` and the splice completes; B is down while it confirms, restarts, the lock completes; (6) `setchannelpolicy` on A, alice sees the new `htlc_maximum_msat`; (7) `exportchanbackup` + `verifychanbackup` on both after every step, on the current funding; (9) (wave SPR, run before the close) A splices in at 253 sat/kw and bumps it with `bumpsplice`: the bump replaces the first attempt in the mempool, both nodes list both attempts pending, a payment while they are, the bump confirms and locks, the first never confirms, alice has the new SCID and forgets the old one; (8) cooperative close. |
| `Day0UpgradeInPlaceTests` | A SQLite database holding a used v1 channel, rolled back with EF's migrator to (a) the live Mutinynet node's schema (`AddGraphFundingTxId`, an `option_static_remotekey` channel) and (b) the migration before `AddSpliceFundings` (an anchors channel), migrates at startup, the channel reestablishes with unchanged balances and commitment numbers, pays both ways and then splices. The rows are written by the new build and rolled back by EF, not produced by an old build: values an old build wrote differently are not covered. |

Also required green: Proof SP1 (`Docker/Interop/Cln/ClnSpliceTests`), Proof DF (`ClnDualFundTests`), Proof SP2
(`ClnSpliceReestablishTests`, `Docker/Onchain/OnchainSpliceTests`, lane SP2-D) and Proof SPR
(`ClnSpliceRbfTests`, wave SPR lane SPR-C: we bump our splice, CLN bumps its splice, CLN's same-feerate RBF refused,
payments with three attempts pending, a bumped splice across a reconnection and a restart).

## 1. Preconditions and go/no-go (both phases)

- [ ] **One build, both sides.** U and N run the **same commit** (compare `git log -1` and the staged `bin/SHA`
      files). Client and daemon come from the same build (NL-210). Wire behavior of splicing and dual funding changed
      in every wave so far; mixed builds are not supported. The build must contain `d5be5b73` (NL-517): an older one
      rewrites a spliced funding's row at the next lock after a restart and the signer refuses the channel at the start
      after that (found in the Mutinynet dry run, §5). Use `8e852a18` (NL-519) or later for the rehearsal with Nick, so
      the logs print txids in the order explorers use.
- [ ] **Tests green at that commit** (root `CLAUDE.md` "Build / test / format"): Release and Release.Native builds
      with only the baseline warnings, `dotnet format --verify-no-changes`, the non-Docker tests on net10.0, and the
      Docker proofs above plus the LND, CLN, ABCD, gossip and on-chain suites of the SP2 integration record.
- [ ] **Features.** Since splicing plan D13 (wave d13, NL-021/NL-042/NL-037) `option_splice`, `option_quiesce` and
      `option_dual_fund` are advertised Optional by default on every network, mainnet included, and are no longer in
      `ExperimentalFeatures`: a build with D13 needs **no** `Node:Features` keys and no `AllowExperimentalFeatures`.
      A build from before D13 (wave spr and `91591787`..`29ce3126`) still defaults them to `No` and needs the block
      below; it is harmless on a D13 build, except that `AllowExperimentalFeatures` also allows every other
      experimental feature (leave the others at their defaults, e.g. `OptionAttributionData` stays `No`), so remove
      `AllowExperimentalFeatures` once both nodes run D13 (the keys are under `Node:Features`; `DualFund` is the
      property name of `option_dual_fund`):

      ```jsonc
      "Node": {
        "Features": {
          "AllowExperimentalFeatures": true,    // pre-D13 builds only
          "OptionQuiesce": "Optional",          // the default since D13
          "OptionSplice": "Optional",           // the default since D13
          "DualFund": "Optional"                // the default since D13
        },
        "DualFund": { "AcceptContributionSat": 0, "MatchOpenerContribution": true, "AllowRbf": true }
      }
      ```

      `openchannel` without `--dual-fund` still opens a v1 channel; a peer's `open_channel2` is accepted with
      `Node:DualFund:AcceptContributionSat` (0 by default). **Splice RBF recency (NL-520):** a node refuses (`tx_abort`)
      a peer's splice RBF while the latest attempt is "created recently", which since NL-520 means until one new block
      (`Splice:MinRbfBlocks`, default 1; about 10 min on mainnet, about 30 s on Mutinynet) has been processed since
      that attempt; nothing needs configuring for the Mutinynet rehearsal or mainnet. `Splice:MinRbfInterval` is still
      supported as an optional wall-clock override for test networks: when set it **replaces** the block rule (the
      live FAFO/FAFO2 Mutinynet nodes keep their `"Splice": { "MinRbfInterval": "00:00:05" }` from the dry run, so a
      bump seconds after the first attempt is accepted there, as in the dry run). Do not set it on mainnet.
- [ ] **Known gaps accepted** (check [`ISSUES.md`](ISSUES.md) at the commit for the NL-021/NL-037 follow-ups, e.g.
      NL-470 and NL-478..NL-483):
  - **Splice RBF only from a build with wave SPR integrated** (`bumpsplice`, Proof SPR and `Day0FlowTests` step 9
    green at the commit). Without it a splice whose feerate is too low cannot be bumped and the channel keeps working
    on the old funding until it confirms. Even with it, pick a feerate that confirms (§3.3): every bump is a new
    negotiation with the peer (both online, quiescence), and the peer refuses a bump of an attempt it considers created
    recently (since NL-520: in the block the attempt was created in, `Splice:MinRbfBlocks`; or younger than its
    `Splice:MinRbfInterval` when that override is set on its side). Bump only **your own** splice: a CLN peer
    contributes nothing to an RBF it did not start (Proof SPR header), so an RBF of the other side's splice drops that
    side's contribution.
  - **RBF of a dual-funded open only from a build with lane dfrbf** (NL-528: `Node:DualFund:AllowRbf` true by default,
    public opens included, `bumpopen <channel_id> <feerate_per_kw>` from either node, the accepter too since NL-530;
    the peer's RBF is followed; a CLN peer refuses an accepter's bump). Every
    signed attempt may confirm, and the channel follows the one that does, so a bump is safe; still pick a funding
    feerate that confirms: each bump needs both nodes online, and a peer that sent or received `channel_ready` refuses
    it. On an older build there is no RBF of a dual-funded open. `openchannel` returns only at `channel_ready`
    (NL-535): to bump, run it in the background or in a second terminal, read the funding txid from `listchannels`,
    and run `bumpopen` before the first confirmation (Mutinynet: within one ~30 s block; dry run 2 bumped twice in
    one second). A node config that sets `Node:DualFund:AllowRbf=false` (the first dry run's did) refuses it.
  - `openchannel` has no `--feerate`: the dual-funded funding transaction uses the node's fee estimate
    (`FeeEstimation`). Check `walletbalance`/logs for the estimate before opening.
  - No standard seed (NL-159): the key file plus its password is the only backup of the on-chain funds.
  - Relay of other nodes' gossip stays off on mainnet (NL-417); our own announcements still go to our peers.
  - **A peer that connects to us from `127.0.0.1`** is saved as inbound-only since wave spr (NL-497): its channels
    are registered at startup and it reconnects to us (it is never dialed at the loopback address; a dialable row
    saved before is kept and used). An inbound peer from any other address is still saved with its host and port
    9735 (not the port it listens on, NL-514), so the side that dials must itself listen on 9735 for the other side's
    reconnects. Check with `listpeers` on both sides, and after the first restart that `listchannels` still lists the
    channel. The side without a dialable row never dials: after **it** restarts, the channel comes back when the
    other side's reconnect backoff (`Node:ReconnectInitialDelay` 5 s doubling to `ReconnectMaxDelay`) fires again,
    57-72 s in the Mutinynet dry run; `connect <its id>@<host>:<port>` from the dialing side brings it back at once.
  - **Each node needs its own well-connected peers** besides the other day-0 node. Our node sends its own
    `channel_announcement`, `channel_update` and `node_announcement` to its connected peers, but relays the other
    end's `channel_update` of our channel only as others' gossip, i.e. only to peers that sent a
    `gossip_timestamp_filter` (LND sends one only to its few active sync peers) and never on mainnet
    (`Gossip:RelayEnabled=false`, NL-417). In `Day0FlowTests` LND alice, a peer of A only, never learned B's
    direction until B connected to her too (found in wave sp2, reported to the ledger).
- [ ] **Amounts approved by the owner** (§3.1 table), written down before anything is sent.
- [ ] Both operators have read §4 and accept it: **never start a node on an old copy of `nltg.db`.**

## 2. Mutinynet phase

### 2.1 Upgrade the live NLightningFAFO node in place

The node: `~/.nltg/mutinynet` on the owner's machine, node id
`030f7defc57e05273c109870dbc15ec0f1ade96872852a06247c42c75bfac2495a`, alias `NLightningFAFO`, one public channel of
200,000 sat to the faucet LND with SCID **`3458334x7x0`** (funding outpoint
`12482a42baf84a4945374ad40c3a77dcd16de1590cfe7374306ae21169aa8d0c:0`, [`MUTINYNET.md`](MUTINYNET.md) "Public
channel"). The faucet LND cannot splice; this channel only proves the upgrade. `U=scripts/mutinynet/cli.sh`.

Done on 2026-09-28 (05:37-05:40 UTC) from the soak's staged build `988bbde` to `d5d8b184`, then `91591787`: nine
migrations (`AddFeeInputReservations` ... `AddSpliceHardening`), the channel reestablished at commitment 1/1 as
recorded, payments both ways; details in §5. Notes from that run are in the steps below.

1. **Record the state** (keep the output with the backup):

   ```bash
   $U info; $U listchannels; $U walletbalance; $U chainstatus
   $U exportchanbackup --output ~/day0/mutinynet/pre-upgrade.backup
   cat ~/.nltg/mutinynet/bin-SHA                  # the build SHA the node runs now (if staged as in step 4)
   ```

   Write down, for the rollback decisions of steps 6 and 8:
   - the channel's **commitment numbers**, the `Commitment (l/r):` line of `listchannels` (local/remote);
   - the node's **last applied migration**, read after step 2 with the node stopped:
     `sqlite3 ~/.nltg/mutinynet/nltg.db 'select MigrationId from __EFMigrationsHistory order by 1 desc limit 1'`
     (on 2026-09-27 it was `20260926163333_AddGraphFundingTxId`, so the upgrade applies at least the seven
     migrations `AddFeeInputReservations` ... `AddSpliceFundings`);
   - the **build SHA** of the old binaries (`git log -1 --format=%H` of the checkout they were built from).

   Continue only when `listchannels` shows the channel `Open`, `HTLCs (out/in): 0/0`. A build older than wave rf1
   (the soak's `988bbde` was) has no `exportchanbackup`; the cold copy of step 3 is the backup then.
2. **Stop the node.** If the gossip soak runs it: `scripts/mutinynet/soak-gossip.sh stop` (it stops the daemon it
   started); otherwise Ctrl-C in the `start-daemon.sh` terminal, or the daemon's `--stop`. Check nothing is left:
   `pgrep -fl NLightning.Daemon` prints nothing. In the dry run the sampler had already exited (its pid file was
   stale) and the daemon was stopped with `kill -TERM <soak/daemon.pid>`: it disconnected the faucet and exited in 2 s.
3. **Back up, with the node stopped** (a cold copy; never copy `nltg.db` alone while the daemon runs):

   ```bash
   B=~/day0/mutinynet/pre-upgrade-$(date -u +%Y%m%dT%H%M%SZ); mkdir -p "$B" && chmod 700 "$B"
   cd ~/.nltg/mutinynet
   sqlite3 nltg.db ".backup '$B/nltg.db'"          # consistent copy, WAL included
   cp -p nltg.key.json appsettings.json "$B/"
   [ -f channel.backup ] && cp -p channel.backup "$B/"
   (cd "$B" && shasum -a 256 * > SHA256SUMS)
   ```

   The key password stays where it is (`.password`, mode 600). This copy is for **rollback before any new channel
   update** and forensics only (§4). "Before any new channel update" is not "before step 7": the new daemon of step 6
   reestablishes the public channel with the faucet LND as soon as it starts, and any `update_fee` (we funded the
   channel, so our `IFeeUpdateScheduler` sends them) or any HTLC the faucet adds (a probe, a payment) signs a new
   commitment and turns this copy into a **revoked state**. Step 8 says how to tell.
4. **Stage the new build.** Check out the day-0 commit, then `scripts/mutinynet/build.sh`, and record
   `git log -1 --format=%H > ~/.nltg/mutinynet/bin-SHA`. `start-daemon.sh` runs the binaries from the checkout's
   `bin/Release/net10.0`, so do not rebuild the checkout while the node runs (or stage copies as the soak does).
5. **Configure** `~/.nltg/mutinynet/appsettings.json` (with `jq`, or by hand): `Database:RunMigrations=true` and the
   `Node:Features`/`Node:DualFund` block of §1 (a D13 build needs only `Node:DualFund`). For the rehearsal with Nick also set our reachable address so his
   node can dial us: `Gossip:AnnounceAddresses: ["<public ip>:9735"]` and a matching `Node:ListenAddresses`.

   For the **first start after the upgrade** also turn our `update_fee` rounds off (`Node:FeeUpdates:Enabled=false`),
   so the node itself signs no new commitment before step 7 checked it; turn them back on (remove the key) before
   step 7. This does not stop the faucet from adding an HTLC, so step 6 still compares the commitment numbers.

   ```bash
   cd ~/.nltg/mutinynet && cp appsettings.json appsettings.json.pre-day0
   jq '.Database.RunMigrations = true
       | .Node.Features.AllowExperimentalFeatures = true
       | .Node.Features.OptionQuiesce = "Optional" | .Node.Features.OptionSplice = "Optional"
       | .Node.Features.DualFund = "Optional"
       | .Node.FeeUpdates.Enabled = false' appsettings.json.pre-day0 > appsettings.json
   chmod 600 appsettings.json
   ```

   On a D13 build (§1) the three feature lines and `AllowExperimentalFeatures` can be left out; keeping them from an
   earlier upgrade is harmless, but remove `AllowExperimentalFeatures` so no other experimental feature can be turned
   on by mistake.
6. **Start and watch the migration.** `scripts/mutinynet/start-daemon.sh &`. The template's log levels
   (`Default: Error`) hide EF's migration lines, so read the applied migrations from the database instead:
   `sqlite3 ~/.nltg/mutinynet/nltg.db 'select MigrationId from __EFMigrationsHistory where MigrationId > "<step 1
   migration>"'` lists every migration after the node's last one, at least `..._AddSpliceFundings` and
   `..._AddSpliceHardening`. A `The configuration directory ... is accessible by other users (mode 755)` warning
   means `chmod 700 ~/.nltg/mutinynet`. Then:

   ```bash
   $U chainstatus                  # Processing: running, last block == tip
   $U listchannels                 # 3458334x7x0: Open, Reestablished Yes, same balances and commitment numbers
                                   #   (Commitment (l/r) as recorded in step 1),
                                   #   one funding (Initial, Current) on 12482a42...8d0c:0
   $U listpeers                    # the faucet LND connected
   ```

   **Abort** in these cases, and never with the step 3 copy unless step 8 allows it:
   - The **migration fails** and the daemon exits before it connected to any peer (the log shows the migration
     error and no peer connection or `channel_reestablish` after it): the database never signed anything new;
     step 8 (a).
   - The channel is **not reestablished within 5 minutes**: stop the daemon and keep the **upgraded** database.
     Investigate from the logs (and `NL-###` it); do not go back to the step 3 copy, which may already be revoked
     (step 3). Step 8 applies only if `listchannels` still shows the step 1 commitment numbers.
   - **`DATA LOSS DETECTED`**: the peer claims a newer state than ours. Never restore an older database and never
     force close (§4): keep the upgraded database, leave the node stopped or running without HTLCs, and let the
     faucet close; investigate.
7. **Verify the channel works** and back it up. First turn the `update_fee` rounds back on: stop the daemon,
   remove `Node.FeeUpdates.Enabled` from `appsettings.json` (`jq 'del(.Node.FeeUpdates.Enabled)'`), start it again.
   From here on the step 3 copy is a revoked state.

   ```bash
   $U payinvoice "$(scripts/mutinynet/faucet.sh invoice 1000)"
   $U createinvoice 1000000 "day0 upgrade check"             # copy the lntbs... invoice
   scripts/mutinynet/faucet.sh withdraw <that invoice>
   $U exportchanbackup --output ~/day0/mutinynet/post-upgrade.backup
   $U verifychanbackup ~/day0/mutinynet/post-upgrade.backup   # valid, the channel, keys match
   ```

   mutinynet.com still lists NLightningFAFO with its channel `3458334x7x0`. The faucet can pay us only what it holds
   above its channel reserve (1 %, 2,000 sat on this channel): with 1,000 sat on its side the first `withdraw` answered
   `FailureReasonNoRoute`; pay it 5,000 sat first, then withdraw 1,000.
8. **Rollback, if needed.** Stop the daemon first. The step 3 copy of `nltg.db` may be restored **only** when
   (a) the migration failed before the daemon connected to any peer (step 6), or (b) `listchannels` on the upgraded
   node, read before the stop, still shows **exactly** the commitment numbers recorded in step 1 (no commitment was
   signed since the copy). Then restore it (`cp "$B/nltg.db" ~/.nltg/mutinynet/nltg.db`, and remove any
   `nltg.db-wal`/`nltg.db-shm` left next to it), restore `appsettings.json.pre-day0`, and start the old build (the
   SHA recorded in step 1). In **every other case** the copy is a revoked state: **never restore it**. Keep the
   upgraded database instead and, if the old build must run, roll its schema back to the **last migration recorded
   in step 1** with the new checkout, node stopped:
   `cd src/NLightning.Infrastructure.Persistence && NLIGHTNING_SQLITE="Data Source=$HOME/.nltg/mutinynet/nltg.db"
   dotnet ef database update <step 1 migration, e.g. 20260926163333_AddGraphFundingTxId> --framework net10.0`
   (this undoes every later migration, `AddSpliceFundings` and any SP2 one included; the design-time factory picks
   SQLite from `NLIGHTNING_SQLITE`, as `scripts/add_migration.sh` does; unset `NLIGHTNING_POSTGRES` first). A
   rollback to a newer migration than the node's own (for example the one just before `AddSpliceFundings`) leaves a
   schema the old build has never run against: never do that. **(Unverified on a live file;**
   `Day0UpgradeInPlaceTests` rolls a database with a used channel back to both `AddGraphFundingTxId` and the
   migration before `AddSpliceFundings` with EF's migrator and upgrades it again at startup (both cases green on
   2026-09-27 on `wip/fafo-sp2-sp2-f-s1-rv`, before the SP2 merge); its channel rows are written by the new build,
   not by an old one.**)** `AddSpliceFundings`' `Down` refuses while any channel runs on
   a locked splice (its rotated funding keys live only in `ChannelFundings`): after a splice there is no way back to
   a pre-sp1 build. `AddBolt12Offers`' `Down` makes `Invoices.Bolt11` required again, so it fails if the upgraded
   node created a BOLT 12 invoice (do not use offers before the upgrade is confirmed).

### 2.2 Rehearsal with Nick's node

Nick runs the same commit on Mutinynet (his own synced Mutinynet bitcoind), upgraded or fresh as in §2.1, with the
same `Node:Features` block and his contribution as accepter: `Node:DualFund:AcceptContributionSat=<his share>`
(capped at our contribution while `MatchOpenerContribution` is true). One of the two nodes must accept inbound
connections (announced address and open port). Variables: `U` as above, `N` = Nick's `cli.sh`,
`NICK=<nick node id>@<host>:9735`, `CH=<channel id>` once known. Amounts are examples.

**Dry run with a second node on the same machine (2026-09-28).** The script below passed with NLightningFAFO as U
and a new node NLightningFAFO2 (`02c8416ac6ac57fccb5a39dfe7324dcc4677dbb03c99798e1eab1090c1202d2431`) as N, both on
the owner's machine and the same Mutinynet bitcoind. Nothing new was needed in the code to run two nodes:

- The daemon takes `--config <dir>` (`-c`): that directory holds `appsettings.json` (it must exist; the template is
  written only for `~/.nltg/<network>`), the key file, the IPC pipe `nltg.ipc`, the cookie and `channel.backup`, and
  the network comes from the file's `Node:Network`. The CLI finds that node with `--cookie <dir>`. Start the daemon
  from that directory (relative `nltg.db` and `logs/`, NL-306).
- FAFO2's directory `~/.nltg/mutinynet-fafo2` was a copy of FAFO's `appsettings.json` with `Node:Alias`
  `NLightningFAFO2`, `Node:Color` `0000ff`, `Node:ListenAddresses` `["0.0.0.0:9736"]`,
  `Node:DualFund:AcceptContributionSat` 150000, its own `.password` (`openssl rand -hex 24`, mode 600) and no
  database: the first start created the key (a new node), migrated and started the wallet at the tip. It shares the
  bitcoind RPC and ZMQ settings with FAFO.
- Each node runs a staged copy of the build (`~/.nltg/mutinynet/bin-<sha>/{daemon,client}`, both nodes'
  `bin-current` symlinks point at it) so a rebuild of the checkout never changes a running binary. On the owner's
  machine `~/day0/nodectl fafo|fafo2 start|stop|status` starts a node with `nohup` from its directory (output
  appended to its `daemon.out`, pid in `<dir>/day0.pid`) and stops it with SIGTERM (60 s bound); `~/day0/u` and
  `~/day0/n` are the two CLIs (`--network mutinynet` and `--cookie ~/.nltg/mutinynet-fafo2`).
- FAFO dialed FAFO2 at `127.0.0.1:9736` (U is the opener and the dialing side; FAFO2 saves FAFO as inbound-only,
  NL-497), and FAFO2 connected to the faucet LND itself, so the network learns FAFO2's direction and node (§1).
- With more than one channel between the two nodes a payment may take either: close the older one first (the dry
  run's first U→N payment of the clean pass went over the pass-1 channel, which was then closed and the payment
  repeated) or check `Outgoing HTLC` in the `payinvoice` output.

```bash
# 0. both: funded wallets (contribution + anchors reserve 10,000 sat + fees), same commit
$U info; $N info; $U walletbalance; $N walletbalance

# 1. dual-funded public open (we are the opener, Nick contributes as accepter); a loopback address works since NL-497
#    (the accepter needs 3 confirmations on the outputs that back its anchors reserve: fund it, wait, then open)
$U connect $NICK
$U openchannel $NICK 300000 --public --dual-fund      # prints the channel id: CH; returns at channel_ready (NL-535)
# optional open RBF (lane dfrbf/accrbf): run the openchannel above with `&`, then before the first confirmation
#   $U bumpopen $CH 1000; $N bumpopen $CH 1500        # opener, then accepter (NL-530); both follow each attempt
$U listchannels; $N listchannels                     # capacity = 300,000 + Nick's share; wait Open (~3 blocks)
# after 6 blocks (~3 min): our announcements go out; check mutinynet.com lists the channel with both nodes
$U exportchanbackup --output ~/day0/mutinynet/01-open.backup && $U verifychanbackup ~/day0/mutinynet/01-open.backup
$N exportchanbackup --output ~/day0/mutinynet/01-open.backup && $N verifychanbackup ~/day0/mutinynet/01-open.backup

# 2. payments both ways, and a third party through the channel
$N createinvoice 20000000 "day0 u->n"               # then: $U payinvoice <that invoice>
$U createinvoice 10000000 "day0 n->u"               # then: $N payinvoice <that invoice>
$N payinvoice "$(scripts/mutinynet/faucet.sh invoice 2000)"        # Nick -> us -> faucet
$N createinvoice 2000000 "faucet -> us -> nick"       # then: scripts/mutinynet/faucet.sh withdraw <that invoice>
# (the faucet pays through us; it needs more than its reserve on its side of our channel, §2.1 step 7)
# backups (as in 1) on both

# 3. we splice in (feerate in sat/kw; Mutinynet: 253)
$U splicein $CH 100000 --feerate 253
$U listchannels                                      # fundings: the splice pending with its depth
# wait for the lock on both ends (the channel's minimum depth), then 6 blocks: the new SCID is announced
$U listchannels; $N listchannels                     # new capacity, new SCID, the old SCID listed as retired
# backups on both

# 4. Nick splices out to an address (here one of ours)
$U getaddress p2wpkh                                 # ADDR = the tb1q... address it prints
$N spliceout $CH 50000 --address $ADDR --feerate 253
# lock as in 3; the address receives 50,000 sat (walletbalance on our side after the confirmation); backups

# 5. restart drill: start a splice, stop Nick's daemon while it is pending, restart it
$U splicein $CH 20000 --feerate 253
# (Nick) stop the daemon once `listchannels` shows the splice pending; wait 3 blocks; start it again
# (if Nick's node is the one that does not dial, the channel is back when our backoff redials, about a minute;
#  `$U connect $NICK` does it at once, §1)
$U listchannels; $N listchannels                     # Reestablished Yes on both, the splice locks; backups
# a splice replaced by the next lock before its 6th confirmation is never announced (BOLT 7): expected

# 6. routing policy
$U setchannelpolicy $CH --htlc-max-msat 150000000
$U getchannelpolicy $CH                              # and mutinynet.com / the faucet LND show the new maximum

# 6b. splice RBF drill (only with wave SPR in the build; Day0FlowTests step 9): a splice at the floor, then bumped
$U splicein $CH 30000 --feerate 253
$U listchannels                                      # fundings: one pending splice; note its txid (T1)
# wait until Nick's node no longer considers T1 "created recently" (§1, NL-520): by default one new block after T1
# (a bump in T1's own block gets Nick's tx_abort and changes nothing; bump again after the next block, which may
# already confirm T1 on 30 s blocks), or at least his Splice:MinRbfInterval when that override is set (5 s on the
# dry-run nodes). With the override, do it before the next block: create both invoices first and run splicein,
# bumpsplice and both payinvoice back to back (the dry run's first pass bumped 6 s after T1 and T2 was mined 11 s
# later, before any payment)
$U bumpsplice $CH 1000                               # bumpsplice <channel_id> <feerate_per_kw> [--max-fee-sat <sats>]; prints the new txid (T2)
$U listchannels; $N listchannels                     # both list T1 (Pending (Splice)) and T2 (Pending (SpliceRbf))
# bitcoind: T1 left the mempool at once (getrawtransaction finds only T2). Pay once each way while both are pending,
# then wait for the lock: both ends run on T2, the new SCID is announced after 6 blocks, T1 never confirms (our chain
# monitor logs its refused rebroadcasts as ERR until one block after the lock marks it Abandoned); backups on both

# 7. keep the channel for mainnet day -1, or close it
$U closechannel $CH 0 300
```

Record every txid, SCID and payment hash in the results template (§5). Anything that differs from the Docker proof
(for example the lock depth or a peer warning in the log) is a stop: open an `NL-###` entry before mainnet.

## 3. Mainnet day 0

### 3.1 Checklist

- [ ] §1 complete, and the §2.2 rehearsal done with **this** commit on both sides.
- [ ] Both nodes set up as in [`MAINNET_CANARY_RUNBOOK.md`](MAINNET_CANARY_RUNBOOK.md) §2 (bitcoind, staged
      binaries, strong key password, `chmod 600`), plus:
  - `Gossip:AllowPublicChannelsOnMainnet=true` on **both** nodes (else `openchannel --public` and the peer's public
    open are refused);
  - `Gossip:AnnounceAddresses: ["<public ip>:9735"]` (and `Node:ListenAddresses`) on at least the node that accepts
    the connection, so the other can dial and the network sees an address; `Node:Alias`, `Node:Color`;
  - the `Node:DualFund` block of §1 (a D13 build needs no `Node:Features` keys), with `AcceptContributionSat` on the
    accepter only (Nick); no `Splice:MinRbfInterval` (the block rule, NL-520);
  - `Gossip:RelayEnabled` stays `false` (NL-417).
- [ ] **Amounts, approved by the owner before anything is sent** (fill in):

  | Item | Amount (sat) |
  |---|---|
  | U's contribution to the open | |
  | N's contribution (`AcceptContributionSat`) | |
  | U's splice-in | |
  | N's splice-out, and its destination address | |
  | Wallet margin per node: anchors reserve 10,000 + funding, splice and close fees | |

- [ ] **Fee and feerate.** Read `https://mempool.space/api/v1/fees/recommended` right before each step.
  - The open's feerate is the node's estimate (`FeeEstimation`; `Bitcoind` with `ConfirmationTarget` 6 as in the
    canary runbook). Check the log's estimate; do not open when it is below the current "hour" fee.
  - For `splicein`/`spliceout`, pass `--feerate <sat/kw>` = the "halfHourFee" (sat/vB) x 250, at least 253. Err
    high: a stuck splice leaves the channel on its old funding until it confirms. With wave SPR in the build the
    initiator can `bumpsplice <CH> <sat/kw>` (BOLT 2's minimum is max(floor(25/24 x previous), previous + 25 sat/kw),
    e.g. 278 after 253; for bitcoind's replacement rule add about 1 sat/vB = 250 sat/kw to the previous feerate);
    without it there is no way to bump a splice.
  - `closechannel <CH> 0 300` uses the estimator; pass a sat/kw feerate instead to pin it.
- [ ] **Backups after every step, on both nodes:** `exportchanbackup --output <dir>/<NN-step>.backup`, then
      `verifychanbackup <file>` (valid, the channel on its current funding, `KeysMatch` true), and a copy off the
      machine. The node also rewrites `<configPath>/channel.backup` on every change; copy that too.
- [ ] Both operators online (chat) for the whole run; one step at a time, each confirmed by both `listchannels`.

### 3.2 Script

The §2.2 script with mainnet amounts and feerates: `C` = `~/.nltg/mainnet/bin/client/NLightning.Client --network
mainnet` on each side.

1. `connect`, then `openchannel <nick>@<host>:9735 <U amount> --public --dual-fund`. Record the funding txid and
   channel id. Wait for the confirmations and `Open` on both; after 6 confirmations check
   `mempool.space/lightning/channel/<scid>` and both nodes' pages. Backups.
2. Small payments both ways (1,000-10,000 sat); a third party through the channel once the channel is in the
   network's graph. Backups.
3. U `splicein <CH> <amount> --feerate <sat/kw>`. Watch the splice tx on mempool.space until confirmed; both
   `listchannels` show the lock and the new SCID; after 6 more confirmations the network shows the new SCID and not
   the old one. Payments both ways. Backups.
4. N `spliceout <CH> <amount> --address <destination> --feerate <sat/kw>`. Same checks; the destination has the
   amount. Backups.
5. Optional: `setchannelpolicy` for the agreed fees and `htlc_maximum_msat`.
6. Keep the channel open (the day-0 goal), or `closechannel` if the owner decides so.

### 3.3 Abort and rollback

| When | What happened | Do |
|---|---|---|
| Open, before the funding tx is broadcast | refusal, `tx_abort`, timeout (`Node:DualFund:OpenTimeout`) | nothing is lost: the wallet reservation is released (restart if `walletbalance` still shows it locked, NL-462); investigate, retry later |
| Open, funding tx unconfirmed for long | feerate too low, and no RBF for a public dual-funded open | wait; do not open another channel from the same inputs; a CPFP from a change output of ours is possible with an external wallet tool only (unverified) |
| Splice negotiating | `tx_abort`, a disconnect before both `tx_signatures` | the splice is dropped, the channel stays on its funding; retry |
| Splice signed, unconfirmed | feerate too low | the channel keeps working (payments are signed for every pending funding); with wave SPR the side that started the splice runs `bumpsplice` (both online; the old attempt stays listed until the bump locks and then never confirms), otherwise wait for the confirmation; never restart on an old database |
| `bumpsplice` refused or `tx_abort` | feerate below max(floor(25/24 x the last attempt's feerate), that feerate + 25 sat/kw), the last attempt too recent for the peer, too many attempts, or a `splice_locked` already sent | the pending attempts stay as they were; retry later with a higher feerate, or wait |
| A node restarts mid-splice | crash, reboot | start it again on the **same** database: `channel_reestablish` retransmits what is missing (Day0 proof step 5) |
| Either side misbehaves or the channel fails | `Failed`, peer `error` | **keep the node running**: it broadcasts the commitment of the current funding (a pending splice's if that one confirmed) and sweeps (BOLT 5; `pendingsweeps`); `forceclosechannel <CH>` only as the last resort |
| Upgrade went wrong | migration or reestablish failure, `DATA LOSS DETECTED` | §2.1 steps 6 and 8: the pre-upgrade copy only if the migration failed before any peer connection or the commitment numbers are still those recorded before the upgrade; otherwise keep the upgraded database and investigate (never an old `nltg.db` after a channel update) |

## 4. Rules that never change

- **Never start a node on an old copy of `nltg.db`, and never run two nodes with the same key.** An old database
  signs or broadcasts a revoked state and the peer takes the whole channel. After any doubt: `DATA LOSS DETECTED` in
  `listchannels` means do not force close; let the peer close.
- After a splice with a rotated funding key there is no downgrade to a build without `ChannelFundings`.
- Backups are for recovery through the peer (`restorechanbackup` asks it to force close); they are not a way to go
  back in time.

## 5. Results template

```text
Commit SHA (U / N):
Network / date:
U node id / N node id:
Contributions (U / N) / funding txid / channel id / SCID:
Payments (hash, amount, direction):
Splice-in: txid / feerate / lock height / new SCID:
Splice-out: txid / feerate / destination / lock height / new SCID:
Splice RBF (if run): first txid and feerate / bump txid and feerate / which one locked, lock height / new SCID:
Restart drill: what was stopped, when, outcome:
Backups (step, file, verifychanbackup result):
Close (if any): txid:
Deviations from the Docker proof / new NL entries:
```
### Mutinynet dry run, 2026-09-28 (two NLightning nodes on the owner's machine)

U = NLightningFAFO (`~/.nltg/mutinynet`, upgraded in place as in §2.1), N = NLightningFAFO2 (`~/.nltg/mutinynet-fafo2`,
new, §2.2 "Dry run"). Times UTC, heights of Mutinynet (~30 s blocks); a splice's SCID block is its confirmation block,
the lock comes at depth 3. Every step ended with `exportchanbackup` + `verifychanbackup` on both nodes (`valid`, the
channel on its current funding, "derive from this key file"), in `~/day0/mutinynet/` (`01-open-*` ... `06-rbf-*`
for pass 1, `p2-01-open-*` ... `p2-06-rbf-*` for pass 2).

**Pass 1 (build `d5d8b184` on both, found NL-517).**

```text
Commit SHA (U / N): d5d8b184755fbd1dbbf7c5087820fb21cfb8e1d8 / same (staged ~/.nltg/mutinynet/bin-d5d8b184)
Upgrade (§2.1): old build 988bbde, last migration 20260926163333_AddGraphFundingTxId, stopped 05:38:00, nine
  migrations applied (AddFeeInputReservations .. AddSpliceHardening), 3458334x7x0 reestablished 05:38:25 at 1/1;
  faucet paid 1,000 sat (0dad5436...0a74); faucet withdraw 1,000 sat: NoRoute (1,000 sat on its side, below its
  reserve); paid it 5,000 (5b2278e0...adc4), then received 1,000 (d14069ca...1842, Settled)
N funded: U withdraw 400,000 sat to N, deac18b2666be7604967acde3491f8fcbb180f0e99e058de91ac57860ef6b6f4 (132 sat fee)
U node id / N node id: 030f7defc57e05273c109870dbc15ec0f1ade96872852a06247c42c75bfac2495a /
  02c8416ac6ac57fccb5a39dfe7324dcc4677dbb03c99798e1eab1090c1202d2431
Contributions (U / N) / funding txid / channel id / SCID: 200,000 / 150,000 /
  5f92ad89d32c30c04210f599f819a6e76bafa05b32d0299333edeef1b84be997:1 /
  a83a746a5bff8c671af3c502720389d842f9575afeb213482ae96d02013173e6 / 3462074x5x1 (open 05:44:05 -> Open 05:45:20,
  announced 05:46:53)
Payments: U->N 20,000 51c8667d...0c70; N->U 10,000 d59391d3...5bf5; N->U->faucet 2,000 6822b33e...db4e (fee 1,002
  msat); faucet->U->N 5,000 NoRoute (faucet's side 7,000 minus its 2,000 reserve), 2,000 8d6d7e4b...1281 Settled
Splice-in: c653d8c958ce2e7e7f0820d4139123da76a6ed005651c2beef25028608f232c8 / 253 sat/kw (252 sat, 248 vB) / block
  3462083, locked 05:50:30 / 3462083x13x0 (450,000), announced 05:51:50, listed by mutinynet.com at 05:52
Splice-out: 2cb71d6bd786015425667c00a8ac61920cdfd12507ea57a571b970567de0c693 / 253 sat/kw (184 sat, from N's side)
  / 50,000 to U's tb1qs639qw5xt7w0s9chcpyhdpv8s9y24at5xvsdas (received) / block 3462091, locked 05:54:46 /
  3462091x12x1 (399,816)
Restart drill: U splicein 20,000 (763b4b890785e00f77052c35eea591dd03b7987aec09e537043080f965b0e995) 05:57:05, N
  stopped 05:57:06 (SIGTERM, 1 s), 3 blocks, N started 05:58:28; U redialed at 05:59:40 (backoff), reestablished,
  both splice_locked, 3462098x9x1 (419,816), commitments 8/8
Policy: U setchannelpolicy --htlc-max-msat 150000000 at 06:00:14; mutinynet.com later showed NLightningFAFO's
  max_htlc 150,000,000 on 3462104x1x1
Splice RBF: T1 c02bc824f16cd8e6b3543c78cc126333753935a66063ae6024e8d3a0a30f2923 at 253 sat/kw 06:00:27 / bump T2
  20aebfd534abdb206765ab8ffe88ef26f07ebce72abfde4e539f9805640a0776 at 1,000 sat/kw 06:00:33 (996 sat) / T2 mined in
  3462104 at 06:00:45 (before the payments), locked 06:01:46 / 3462104x1x1 (449,816); T1 out of the mempool at once,
  rebroadcast refused until Abandoned at 06:02:16; payments after the lock db063fbd...1396, c27514b0...6772
Close: the pass-1 channel was closed at 06:22:02 during pass 2 (below)
Deviations / new NL entries: NL-517 (high): N restarted in the drill while its own splice-out 2cb71d6b (key index
  2) was the current funding; the reload gave the engine the initial funding (kind Initial, key index 0) and the
  next lock (763b4b) rewrote 2cb71d6b's row with them; the signer would have refused that row at N's next start.
  Fixed in d5be5b73 (ledger 91591787); N's row repaired by hand, node stopped (Kind 1, key index 2, checked against
  U's row and by the signer's registration at the next start). NL-518 (low): every splice logs a MempoolReactor
  warning.
```

**Pass 2, the clean pass (build `91591787` on both, 06:17-06:37 UTC).** Machine-readable record with every field:
[`day0-mutinynet-dryrun.json`](day0-mutinynet-dryrun.json). Txids are in display order (bitcoind, mutinynet.com); links go to
`https://mutinynet.com/tx/<txid>`. Node ids: FAFO `030f7defc57e05273c109870dbc15ec0f1ade96872852a06247c42c75bfac2495a`, FAFO2 `02c8416ac6ac57fccb5a39dfe7324dcc4677dbb03c99798e1eab1090c1202d2431`, hub (faucet LND) `02465ed5be53d04fde66c9418ff14a5f2267723810176c9212b722e542dc1afb1b`.

| Stage | What it proves | UTC | Commands | Result |
|---|---|---|---|---|
| P2-0 wallets and connections | both nodes on the same build, funded, connected to each other and to the hub | 06:17:00-06:17:40 | `~/day0/u info`<br>`~/day0/n info`<br>`~/day0/u walletbalance`<br>`~/day0/n walletbalance`<br>`~/day0/u listpeers`<br>`~/day0/n listpeers` | build: 91591787 on both; peers: FAFO<->FAFO2 over 127.0.0.1:9736 (FAFO dials), both connected to the hub 45.79.52.207:9735; FAFO_confirmed_sat_before_open: 951725; FAFO2_confirmed_sat_before_open: 249899 |
| P2-1 dual-funded public open | v2 open (open_channel2 / interactive-tx) where both sides contribute inputs; public channel announced at 6 confirmations | 06:17:42-06:20:55 | `~/day0/u openchannel 02c8416ac6ac57fccb5a39dfe7324dcc4677dbb03c99798e1eab1090c1202d2431@127.0.0.1:9736 200000 --public --dual-fund`<br>`~/day0/u listchannels`<br>`~/day0/n listchannels`<br>`~/day0/u exportchanbackup --output ~/day0/mutinynet/p2-01-open-u.backup && ~/day0/u verifychanbackup ~/day0/mutinynet/p2-01-open-u.backup`<br>`(same on N)` | announced: 06:20:55Z (both nodes, depth 6); balances after: FAFO 200,000 / FAFO2 150,000 sat; tx [`6348a8a4…7674`](https://mutinynet.com/tx/6348a8a4fb4b34b5489f78f885ef2e78ae8a59734b4d902064d65e6d06e57674) |
| P2-2 payments both ways and through the hub | HTLCs both directions on the new channel, forwarding by FAFO to and from the faucet hub; the hub routes to FAFO2 from its gossip graph (hint-free invoice) | 06:21:48-06:22:17 | `~/day0/n createinvoice 20000000 "p2 u->n"`<br>`~/day0/u payinvoice <bolt11>`<br>`~/day0/u createinvoice 10000000 "p2 n->u"`<br>`~/day0/n payinvoice <bolt11>`<br>`~/day0/n payinvoice "$(scripts/mutinynet/faucet.sh invoice 2000)"`<br>`~/day0/n createinvoice 2000000 "p2 faucet->n"`<br>`scripts/mutinynet/faucet.sh withdraw <bolt11>`<br>`~/day0/u closechannel a83a746a5bff8c671af3c502720389d842f9575afeb213482ae96d02013173e6 0 120   (the pass-1 channel, so later payments can only use the new one)` | balances after: FAFO 190,001.002 / FAFO2 159,998.998 sat; tx [`b078b3cb…0be6`](https://mutinynet.com/tx/b078b3cb813007a8932f50784ff3a97ac705e3bb13b48240fed54aa97d320be6); `2c719094…` 10,000 sat FAFO2 -> FAFO; `190cb1ab…` 2,000 sat faucet LND hub -> FAFO -> FAFO2 (LNURL-withdraw; invoice without route hints: the hub found the path in its graph); `3550f3d6…` 20,000 sat FAFO -> FAFO2; `b013abae…` 2,000 sat FAFO2 -> FAFO -> faucet LND hub (faucet invoice) |
| P2-3 FAFO splices in 100,000 sat | splice-in with a wallet input (splice_init/ack, interactive-tx on the shared input), channel usable while pending, lock at depth 3, new SCID announced at depth 6, old SCID retired for 72 blocks | 06:22:30-06:25:36 | `~/day0/u splicein fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47 100000 --feerate 253`<br>`~/day0/u listchannels`<br>`~/day0/n listchannels`<br>`(backups as in P2-1)` | scid_before: 3462136x3x2; scid_after: 3462145x9x1; capacity_after_sat: 450000; locked: by 06:24:06Z (depth 3, block 3462147); announced: 06:25:36Z; balances after: FAFO 290,001.002 / FAFO2 159,998.998 sat; tx [`49ebd5e4…0e28`](https://mutinynet.com/tx/49ebd5e4db5ee493f0427acc6782496dfcbeb5532a76c1dd57325d1ab0dd0e28) |
| P2-4 FAFO2 splices out 50,000 sat to a FAFO address | splice-out paying an external address, fee from the splicer's channel balance, amount received on chain | 06:27:32-06:29:12 | `~/day0/u getaddress p2wpkh   (tb1qn6km2hznaaq86e0t2r34rex0qt2qzvqq5ulvqr)`<br>`~/day0/n spliceout fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47 50000 --address tb1qn6km2hznaaq86e0t2r34rex0qt2qzvqq5ulvqr --feerate 253`<br>`~/day0/u walletbalance`<br>`(backups)` | splice_out_output: c73f862f752d07dc6e9ea2e059f8dbf51a98e3fe5d9f26ab91f45b22c394ad47:1, 50,000 sat to tb1qn6km2hznaaq86e0t2r34rex0qt2qzvqq5ulvqr (FAFO wallet); scid_before: 3462145x9x1; scid_after: 3462154x6x0; capacity_after_sat: 399816; locked: by 06:29:12Z (block 3462156); announced: never: replaced by the P2-5 lock before its 6th confirmation (BOLT 7 announces at 6); balances after: FAFO 290,001.002 / FAFO2 109,814.998 sat; tx [`c73f862f…ad47`](https://mutinynet.com/tx/c73f862f752d07dc6e9ea2e059f8dbf51a98e3fe5d9f26ab91f45b22c394ad47) |
| P2-5 restart drill mid-splice | a node stopped with a signed, unconfirmed splice resumes it: channel_reestablish, splice_locked retransmitted, lock completes; NL-517 fix live (FAFO2 restarted while its own splice-out was the current funding and kept its kind/key index at the next lock) | 06:29:36-06:32:24 | `~/day0/u splicein fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47 20000 --feerate 253`<br>`~/day0/nodectl fafo2 stop   (06:29:39Z, SIGTERM, 2 s)`<br>`(3 blocks: 3462157 -> 3462160)`<br>`~/day0/nodectl fafo2 start   (06:31:16Z)`<br>`~/day0/u listchannels`<br>`~/day0/n listchannels` | reconnect: FAFO's backoff redialed at 06:32:13Z (FAFO2 never dials FAFO, a loopback inbound-only peer); scid_before: 3462154x6x0; scid_after: 3462158x9x1; capacity_after_sat: 419816; locked: 06:32:24Z; announced: 06:32:49Z; balances after: FAFO 310,001.002 / FAFO2 109,814.998 sat; tx [`9bccd56b…a03d`](https://mutinynet.com/tx/9bccd56b0bb7d28db705a6474db7cf0df82ebb1bddea245f1e47256d4906a03d) |
| P2-6 setchannelpolicy | a per-channel routing policy change is signed, sent as channel_update and seen by the network | 06:32:45-06:43:12 | `~/day0/u setchannelpolicy fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47 --htlc-max-msat 150000000`<br>`~/day0/u getchannelpolicy fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47`<br>`~/day0/n listgraphchannels 3462158x9x1`<br>`curl https://mutinynet.com/api/v1/lightning/channels/<id>` | htlc_maximum_msat_before: 280000000; htlc_maximum_msat_after: 150000000; seen_by_network: mutinynet.com (its own LND, fed by the hub) lists NLightningFAFO max_htlc 150,000,000 on 3462164x1x0 at 06:43Z; pass 1 showed the same on 3462104x1x1 by 06:1xZ |
| P2-6b splice RBF drill | bumpsplice: an RBF attempt replaces the first in the mempool, both nodes follow both attempts, payments keep working while both are pending, only the bump confirms and locks | 06:33:01-06:35:56 | `~/day0/n createinvoice 3000000 "p2 rbf u->n"`<br>`~/day0/u createinvoice 2000000 "p2 rbf n->u"`<br>`~/day0/u splicein fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47 30000 --feerate 253`<br>`sleep 6`<br>`~/day0/u bumpsplice fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47 1000`<br>`~/day0/u payinvoice <bolt11>`<br>`~/day0/n payinvoice <bolt11>`<br>`~/day0/u listchannels`<br>`~/day0/n listchannels` | T1: f7d54bb2... 253 sat/kw, left the mempool when T2 arrived, never confirmed, marked Abandoned by the chain monitor after the lock; T2: 5e85170a... 1,004 sat/kw (bumpsplice asked 1,000), mined in 3462164, locked 06:34:30Z; scid_before: 3462158x9x1; scid_after: 3462164x1x0; capacity_after_sat: 449816; announced: 06:35:56Z; balances after: FAFO 339,001.002 / FAFO2 110,814.998 sat; tx [`f7d54bb2…42bf`](https://mutinynet.com/tx/f7d54bb2dd3612e1a8b5a572369c48d26f4e1104493591b1bda09c6d47c642bf); tx [`5e85170a…c183`](https://mutinynet.com/tx/5e85170aef6bf0d6074d2e250110a95a08ff581eca4fae69493fe51edf09c183); `95ff317c…` 3,000 sat FAFO -> FAFO2 (both attempts pending); `7d18211b…` 2,000 sat FAFO2 -> FAFO (both attempts pending) |
| P2-7 hub routes over the final SCID | the faucet hub pays FAFO2 through FAFO over the spliced channel | 06:37:00-06:37:03 | `~/day0/n createinvoice 3000000 "p2 faucet->n after splices"`<br>`scripts/mutinynet/faucet.sh withdraw <bolt11>` | balances after: FAFO 336,001.002 / FAFO2 113,814.998 sat; `8b2d0853…` 3,000 sat faucet LND hub -> FAFO -> FAFO2 (invoice with an r hint over 3462164x1x0: new SCID, inside the 10 min grace period) |

On-chain transactions (both passes; fee = inputs - outputs; the per-node columns are wallet inputs minus wallet change):

| Pass | Role | Txid | Block | vB | Fee (sat) | sat/vB | sat/kw | FAFO spent | FAFO2 spent | Outputs |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | FAFO2 funding: FAFO withdraw of 400,000 sat to FAFO2's wallet | [`deac18b2…b6f4`](https://mutinynet.com/tx/deac18b2666be7604967acde3491f8fcbb180f0e99e058de91ac57860ef6b6f4) | 3462070 | 131 | 132 | 1.008 | 253.4 | 400,132 | -400,000 | 0: 400,000 FAFO2 wallet<br>1: 599,868 FAFO wallet |
| 1 | pass-1 dual-funded funding tx | [`5f92ad89…e997`](https://mutinynet.com/tx/5f92ad89d32c30c04210f599f819a6e76bafa05b32d0299333edeef1b84be997) | 3462074 | 252 | 256 | 1.016 | 254.5 | 200,155 | 150,101 | 0: 399,713 FAFO wallet<br>1: 350,000 channel funding output (pass-1 channel a83a746a..., 350,000 sat)<br>2: 249,899 FAFO2 wallet |
| 1 | pass-1 splice-in (FAFO +100,000) | [`c653d8c9…32c8`](https://mutinynet.com/tx/c653d8c958ce2e7e7f0820d4139123da76a6ed005651c2beef25028608f232c8) | 3462083 | 248 | 252 | 1.016 | 254.0 | 100,252 | - | 0: 450,000 channel funding output (pass-1 splice, 450,000 sat)<br>1: 399,440 FAFO wallet |
| 1 | pass-1 splice-out (FAFO2 -50,000 to a FAFO address) | [`2cb71d6b…c693`](https://mutinynet.com/tx/2cb71d6bd786015425667c00a8ac61920cdfd12507ea57a571b970567de0c693) | 3462091 | 181 | 184 | 1.017 | 254.8 | -50,000 | - | 0: 50,000 FAFO wallet<br>1: 399,816 channel funding output (pass-1 splice, 399,816 sat) |
| 1 | pass-1 restart-drill splice-in (FAFO +20,000) | [`763b4b89…e995`](https://mutinynet.com/tx/763b4b890785e00f77052c35eea591dd03b7987aec09e537043080f965b0e995) | 3462098 | 249 | 252 | 1.012 | 253.5 | 20,252 | - | 0: 379,461 FAFO wallet<br>1: 419,816 channel funding output (pass-1 splice, 419,816 sat) |
| 1 | pass-1 splice-in RBF attempt T1 at 253 sat/kw (replaced, never confirmed) | [`c02bc824…2923`](https://mutinynet.com/tx/c02bc824f16cd8e6b3543c78cc126333753935a66063ae6024e8d3a0a30f2923) | not mined (replaced) | 249 | 252 | 1.012 | 253.8 | 30,252 | - | 0: 369,188 FAFO wallet<br>1: 449,816 channel funding output of T1 (449,816 sat) |
| 1 | pass-1 splice-in RBF attempt T2 at 1,000 sat/kw (confirmed) | [`20aebfd5…0776`](https://mutinynet.com/tx/20aebfd534abdb206765ab8ffe88ef26f07ebce72abfde4e539f9805640a0776) | 3462104 | 248 | 996 | 4.016 | 1004.0 | 30,996 | - | 0: 368,444 FAFO wallet<br>1: 449,816 channel funding output (pass-1 splice, 449,816 sat) |
| 1 | cooperative close of the pass-1 channel (during pass 2) | [`b078b3cb…0be6`](https://mutinynet.com/tx/b078b3cb813007a8932f50784ff3a97ac705e3bb13b48240fed54aa97d320be6) | 3462144 | 168 | 172 | 1.024 | 256.0 | -320,831 | -128,813 | 0: 128,813 FAFO2 wallet<br>1: 320,831 FAFO wallet |
| 2 | pass-2 dual-funded funding tx | [`6348a8a4…7674`](https://mutinynet.com/tx/6348a8a4fb4b34b5489f78f885ef2e78ae8a59734b4d902064d65e6d06e57674) | 3462136 | 252 | 256 | 1.016 | 254.7 | 200,155 | 150,101 | 0: 99,798 FAFO2 wallet<br>1: 179,306 FAFO wallet<br>2: 350,000 channel funding output (pass-2 channel fede6471..., 350,000 sat) |
| 2 | pass-2 splice-in (FAFO +100,000) | [`49ebd5e4…0e28`](https://mutinynet.com/tx/49ebd5e4db5ee493f0427acc6782496dfcbeb5532a76c1dd57325d1ab0dd0e28) | 3462145 | 248 | 252 | 1.016 | 254.0 | 100,252 | - | 0: 268,192 FAFO wallet<br>1: 450,000 channel funding output (450,000 sat) |
| 2 | pass-2 splice-out (FAFO2 -50,000 to FAFO's tb1qn6km2hznaaq86e0t2r34rex0qt2qzvqq5ulvqr) | [`c73f862f…ad47`](https://mutinynet.com/tx/c73f862f752d07dc6e9ea2e059f8dbf51a98e3fe5d9f26ab91f45b22c394ad47) | 3462154 | 181 | 184 | 1.017 | 255.2 | -50,000 | - | 0: 399,816 channel funding output (399,816 sat)<br>1: 50,000 FAFO wallet |
| 2 | pass-2 restart-drill splice-in (FAFO +20,000) | [`9bccd56b…a03d`](https://mutinynet.com/tx/9bccd56b0bb7d28db705a6474db7cf0df82ebb1bddea245f1e47256d4906a03d) | 3462158 | 249 | 252 | 1.012 | 253.5 | 20,252 | - | 0: 300,579 FAFO wallet<br>1: 419,816 channel funding output (419,816 sat) |
| 2 | pass-2 splice-in RBF attempt T1 at 253 sat/kw (replaced, never confirmed) | [`f7d54bb2…42bf`](https://mutinynet.com/tx/f7d54bb2dd3612e1a8b5a572369c48d26f4e1104493591b1bda09c6d47c642bf) | not mined (replaced) | 248 | 252 | 1.016 | 254.0 | 30,252 | - | 0: 270,327 FAFO wallet<br>1: 449,816 channel funding output of T1 (449,816 sat) |
| 2 | pass-2 splice-in RBF attempt T2 at 1,000 sat/kw (confirmed) | [`5e85170a…c183`](https://mutinynet.com/tx/5e85170aef6bf0d6074d2e250110a95a08ff581eca4fae69493fe51edf09c183) | 3462164 | 248 | 996 | 4.016 | 1004.0 | 30,996 | - | 0: 449,816 channel funding output (pass-2 channel, 449,816 sat, current)<br>1: 269,583 FAFO wallet |

SCIDs of the clean-pass channel `fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47`:

| SCID | Capacity (sat) | Funding | Status |
|---|---|---|---|
| [3462136x3x2](https://mutinynet.com/lightning/channel/3462136x3x2) | 350,000 | `6348a8a4...:2` | retired at 3462145 |
| [3462145x9x1](https://mutinynet.com/lightning/channel/3462145x9x1) | 450,000 | `49ebd5e4...:1` | retired at 3462154 |
| [3462154x6x0](https://mutinynet.com/lightning/channel/3462154x6x0) | 399,816 | `c73f862f...:0` | retired at 3462158, never announced |
| [3462158x9x1](https://mutinynet.com/lightning/channel/3462158x9x1) | 419,816 | `9bccd56b...:1` | retired at 3462164 |
| [3462164x1x0](https://mutinynet.com/lightning/channel/3462164x1x0) | 449,816 | `5e85170a...:0` | current (open, public) |

Backups after each clean-pass step (`exportchanbackup` then `verifychanbackup`: valid on both), SHA-256:

- `p2-01-open-u.backup` (FAFO): `f1179e0408987289929ed5499c0df2822666ee9baf9c91ed1d38c83da28abeb2`
- `p2-01-open-n.backup` (FAFO2): `d37ecbe7b7dfbe40ba6411097530801ad0ed4127855f035e24d552a109fea5fe`
- `p2-02-pay-u.backup` (FAFO): `9ba790003d1468da8b159fb1de15e5953a99b946fd8d16d575adf6ae640e2158`
- `p2-02-pay-n.backup` (FAFO2): `657ed43fb4684645a2940996249ada7972b234dd4e9eac09fe88541ca6405066`
- `p2-03-splicein-u.backup` (FAFO): `ad6e641291b1f9f3e354340c4e01c32f5694cc0ac701ea2975f22fb852bca9b7`
- `p2-03-splicein-n.backup` (FAFO2): `acc28414bfb63f3d6588aa92cb8895cb1580731a45c41a68bd675387566f98af`
- `p2-04-spliceout-u.backup` (FAFO): `4242403d3faed5ecac57f4ba79e18bd57c20609b905b03ac39e562a2cccd31d5`
- `p2-04-spliceout-n.backup` (FAFO2): `3738c871998b092f2f619fd8e9accf822fcf711a5004e7374047f032e7259854`
- `p2-05-restart-u.backup` (FAFO): `18fc186c95f90a66a36e0d435b44f6c91a75e4b6707c38a3a11d248bbfc0b5a9`
- `p2-05-restart-n.backup` (FAFO2): `1d0378044b7bdd1511d4d4075c64408c7d98a521f8ac84ea9383ef7d991f3e21`
- `p2-06-rbf-u.backup` (FAFO): `33bd4a72adef5b468ce2b5a5599f7245becefe24448535a4cc097d8697b69a8b`
- `p2-06-rbf-n.backup` (FAFO2): `f4ffe783605ef6cbfde4a43afd370cb5666e853aadfd981466c5c6d325db4cc2`


Pass 2 in the results template's form:

```text
Commit SHA (U / N): 91591787 (d5be5b73 + ledger) / same (staged ~/.nltg/mutinynet/bin-91591787)
Contributions (U / N) / funding txid / channel id / SCID: 200,000 / 150,000 /
  6348a8a4fb4b34b5489f78f885ef2e78ae8a59734b4d902064d65e6d06e57674:2 /
  fede6471bad5b816c1845c331ecd2e2313db50bf953de743f7b01f9476720b47 / 3462136x3x2 (open 06:17:42 -> Open 06:19:05,
  both announcements 06:20:55)
Payments: N->U 10,000 2c719094a21980a28c56582ae5257b53160dc18c8e3aeae61d3f5847209683a9; U->N 20,000
  3550f3d645fc98bd4bdb783c9f5ddac91658048e5a04dabcb61432306c01764f and N->U->faucet 2,000
  b013abaeba1c424901d73978376b15433e914af50ae56dd10f202192b972d6e4 (fee 1,002 msat), both repeated after closing
  the pass-1 channel (their first attempts took it); faucet->U->N 2,000 over a hint-free invoice (routed from the
  faucet LND's graph) 190cb1ab15df1d3684646aad4d4e0171c00b9e1ccd4be58b77e3eb7f98b0eb19 Settled
Splice-in: 49ebd5e4db5ee493f0427acc6782496dfcbeb5532a76c1dd57325d1ab0dd0e28 / 253 sat/kw / block 3462145, locked by
  06:24:06 / 3462145x9x1 (450,000), announced 06:25:36; 3462136x3x2 retired (expires 3462217)
Splice-out: c73f862f752d07dc6e9ea2e059f8dbf51a98e3fe5d9f26ab91f45b22c394ad47 / 253 sat/kw (181 vB) / 50,000 to
  U's tb1qn6km2hznaaq86e0t2r34rex0qt2qzvqq5ulvqr (output 1 of the splice, received) / block 3462154, locked by
  06:29:12 / 3462154x6x0 (399,816; replaced before its 6th confirmation, so never announced)
Restart drill: U splicein 20,000 9bccd56b0bb7d28db705a6474db7cf0df82ebb1bddea245f1e47256d4906a03d at 06:29:36, N
  stopped 06:29:39 (SIGTERM, 2 s) with it pending, 3 blocks (3462157 -> 3462160), N started 06:31:16, U redialed
  06:32:13, reestablished, lock 06:32:24, 3462158x9x1 (419,816), commitments 8/8; N's row of c73f862f (its own
  splice, current at the restart) kept kind Splice / key index 2 after the lock, same as U's (NL-517 fixed)
Policy: U setchannelpolicy --htlc-max-msat 150000000 at 06:32:45, in N's graph at 06:32:49
Splice RBF: T1 f7d54bb2dd3612e1a8b5a572369c48d26f4e1104493591b1bda09c6d47c642bf at 253 sat/kw 06:33:01 / bump T2
  5e85170aef6bf0d6074d2e250110a95a08ff581eca4fae69493fe51edf09c183 at 1,000 sat/kw 06:33:09 / both listed pending
  on both nodes, U->N 3,000 (95ff317c...db37) and N->U 2,000 (7d18211b...25c0) paid while both were pending (tip
  unchanged), T1 left the mempool at once, T2 mined in 3462164, locked 06:34:30 / 3462164x1x0 (449,816), announced
  06:35:56; faucet->U->N 3,000 over it at 06:37 (8b2d0853...2e8c, with an r hint: new SCID, grace period)
Close: the pass-1 channel a83a746a... cooperatively, b078b3cb813007a8932f50784ff3a97ac705e3bb13b48240fed54aa97d320be6
  (168 vB) at 06:22:02, Closed by 06:26; the pass-2 channel stays open (the Nick rehearsal reference)
Deviations from the Docker proof / new NL entries: none in pass 2 beyond the notes in §1/§2.2 (the non-dialing
  side waits for the other's backoff after its restart; a splice replaced before 6 confirmations is not announced).
  mutinynet.com listed 3462164x1x0 (449,816 sat) with both policies, NLightningFAFO's max_htlc 150,000,000, at 06:43
  (7 min after the announcement); the short-lived 3462145x9x1 and 3462158x9x1 never showed up in its index.
  NL-518 (log level) seen again.
```

**Nodes left running (2026-09-28, build `dbbcba28` = the clean pass's `91591787` plus NL-519, txids in display order
in the logs).** Both on the staged build `~/.nltg/mutinynet/bin-dbbcba28` (`bin-current` symlinks), restarted at 06:59
and reestablished, started with `~/day0/nodectl` (pid in `<dir>/day0.pid`: FAFO 80716, FAFO2 80609):

```bash
~/day0/nodectl fafo status;  ~/day0/nodectl fafo stop;  ~/day0/nodectl fafo start     # ~/.nltg/mutinynet
~/day0/nodectl fafo2 status; ~/day0/nodectl fafo2 stop; ~/day0/nodectl fafo2 start    # ~/.nltg/mutinynet-fafo2
~/day0/u listchannels; ~/day0/n listchannels                                           # the two CLIs
```

The open channels: U-N `fede6471...0b47` (3462164x1x0, 449,816 sat, U 335,001 / N 114,815 sat after a last 1,000 sat payment on the final build) and U's
original public channel to the faucet LND `3458334x7x0`. Stop N before U when both must stop (U dials N); after a
restart of N, `~/day0/u connect 02c8416ac6ac57fccb5a39dfe7324dcc4677dbb03c99798e1eab1090c1202d2431@127.0.0.1:9736`
skips the backoff wait. The soak's old staged build was moved to `~/.nltg/mutinynet/soak/bin-988bbde-retired` (it
must never run on the migrated database). Do not run `scripts/mutinynet/soak-gossip.sh` while `nodectl` runs FAFO: its
`daemon_pid` looks only for its own staged binary, so it would start a second daemon on the same key and database
(§4); stop FAFO with `nodectl` first.

### Mutinynet dry run 2 (.NET 11), 2026-09-28

The same two nodes, upgraded in place from `dbbcba28` (net10.0) to `2a209a5e` (`wip/fafo` after lanes dfrbf and
accrbf) compiled for **.NET 11**: `dotnet publish src/NLightning.{Daemon,Client} -c Release -f net11.0 -r osx-arm64
--self-contained true` with SDK 11.0.100-rc.1.26425.128 (`~/.dotnet11`), staged as `<dir>/bin-2a209a5e-net11` in both
node directories (`daemon/`, `client/`, `commit`, `runtime`) and linked as each `bin-current`. The publish is
self-contained, so `nodectl`, `~/day0/u` and `~/day0/n` run it unchanged (no `DOTNET_ROOT`); the old
framework-dependent `bin-dbbcba28` (net10 runtime) stays on disk. Proof of the runtime: `lsof` of
both daemon processes shows `libcoreclr.dylib` and `System.Private.CoreLib.dll` loaded from
`bin-2a209a5e-net11/daemon/`, whose CoreLib is `11.0.0-rc.1.26425.128+3551975b...`; the runtimeconfig says `tfm
net11.0`, `Microsoft.NETCore.App 11.0.0-rc.1.26425.128`. Config changes on both nodes (previous files
`appsettings.json.pre-net11`): `Node:Features:AllowExperimentalFeatures` removed (D13, §1) and
`Node:DualFund:AllowRbf` set to `true`: the first run's configs had it `false`, which refuses `bumpopen` even on a
build where RBF is on by default. `Splice:MinRbfInterval` 5 s kept. Cold backups before the switch:
`<dir>/backup-20260928T163311Z/` (`nltg.db`, key file, config, `channel.backup`, `SHA256SUMS`). Machine-readable
record: `run2_net11` in [`day0-mutinynet-dryrun.json`](day0-mutinynet-dryrun.json).

A new dual-funded public channel was required: the first one (`f20939f0...`, P2-1a) confirmed without a bump
because `openchannel` returns only at `channel_ready` (NL-535), so the RBF open was repeated with `openchannel` in
the background (P2-1) and that channel (`b78b95b7...`) carried the rest of the script. `f20939f0...` and the first
run's `fede6471...` stay open (no close in this run), so FAFO and FAFO2 share three channels: a payment between
them takes the direct channel with the most outbound, which is why several payments below went over another channel;
one payment was sized to move FAFO's outbound so that the RBF drill's payment used the splicing channel.

| Stage | What it proves | UTC | Commands | Result |
|---|---|---|---|---|
| U0 upgrade in place to the .NET 11 build | both nodes stopped with nodectl, cold backups, bin-current switched to the self-contained net11.0 build, migration AddDualFundAttempts applied at start, every open channel reestablished with unchanged balances and commitment numbers, gossip synced | 16:33:11-16:34:53 | `~/day0/nodectl fafo2 stop; ~/day0/nodectl fafo stop`<br>`sqlite3 <dir>/nltg.db ".backup <dir>/backup-20260928T163311Z/nltg.db" (+ nltg.key.json, appsettings.json, channel.backup, SHA256SUMS)`<br>`jq 'del(.Node.Features.AllowExperimentalFeatures) \| .Node.DualFund.AllowRbf = true' (both configs; previous file appsettings.json.pre-net11)`<br>`ln -sfn <dir>/bin-2a209a5e-net11 <dir>/bin-current (both)`<br>`~/day0/nodectl fafo2 start; ~/day0/nodectl fafo start`<br>`~/day0/u listchannels; ~/day0/n listchannels; ~/day0/u describegraph; ~/day0/u chainstatus`<br>`~/day0/u payinvoice "$(scripts/mutinynet/faucet.sh invoice 1000)"` | last_migration_before: 20260928024953_AddSpliceHardening; migrations_applied: 20260928133020_AddDualFundAttempts; stopped: 16:33:11Z; started: 16:33:35Z (both); reestablished: faucet 3458334x7x0 at 16:33:38Z (19/19, FAFO 196,003,007 msat, as before); FAFO-FAFO2 3462164x1x0 at 16:33:43Z (16/16, 335,001,002 / 114,814,998 msat, as before); runtime_proof: lsof of both daemon pids: libcoreclr.dylib and System.Private.CoreLib.dll loaded from <dir>/bin-2a209a5e-net11/daemon/ (self-contained publish); that CoreLib is 11.0.0-rc.1.26425.128+3551975be08744f0418857c5bed8ab1545c5dd47; NLightning.Daemon.runtimeconfig.json: tfm net11.0, includedFrameworks Microsoft.NETCore.App 11.0.0-rc.1.26425.128; gossip: describegraph: 900 channels, 282 nodes, initial sync complete, both peers sync peers; upgrade_check_payment: `d09556c1…` 1,000 sat FAFO -> faucet LND hub (via 0c8daa69... (3458334x7x0)); cold_backup_sha256: FAFO nltg.db: f9d632219de8ee89436664698c5a2cb16aca6a83554f924bc6e4b36866f0b4e4; FAFO2 nltg.db: 5679107dfacb85159638a9febb16b730bd1a70501433af85b54e8ef36ca5ee01 |
| P2-1a dual-funded public open (no RBF) | v2 open on the net11 build, both contribute, announced at 6 confirmations; `openchannel` blocks until channel_ready, so bumpopen from the same shell comes too late (NL-535) | 16:35:38-16:39:03 | `~/day0/u openchannel 02c8416ac6ac57fccb5a39dfe7324dcc4677dbb03c99798e1eab1090c1202d2431@127.0.0.1:9736 200000 --public --dual-fund`<br>`~/day0/u bumpopen f20939f0c87038c0aec6c2de0587d20dd93b2acc2d6f40b1393a64cc1958e51c 1000   (after the CLI returned: refused, channel_ready exchanged)` | channel_id: f20939f0c87038c0aec6c2de0587d20dd93b2acc2d6f40b1393a64cc1958e51c; funding_outpoint: 79760d18e1ffc92596f89f567a5d886e1c753fb2540d20c7aee4beb99be33ee1:0; scid: 3463271x11x0; capacity_sat: 350000; broadcast: 16:35:38Z; mined: 3463271 (16:36:26Z); open_confirmed_by: 16:37:30Z (depth 3); announced: 16:39:03Z (both nodes); balance_after: FAFO 200,000 / FAFO2 150,000 sat; note: kept open: closing it was not authorised in this run (the lead allowed only the old day-0 channel to be closed, and only if the runbook closes it); tx [`79760d18…3ee1`](https://mutinynet.com/tx/79760d18e1ffc92596f89f567a5d886e1c753fb2540d20c7aee4beb99be33ee1) |
| P2-1 dual-funded public open with opener and accepter RBF | bumpopen by the opener (FAFO) and then by the accepter (FAFO2, NL-530) before the first confirmation; each attempt replaces the previous one in the mempool, both nodes follow every attempt, the last (the accepter's) confirms and is announced; the losing attempts are marked Replaced | 16:42:25-16:45:32 | `~/day0/u withdraw tb1qvr9a7rp7540gxawu0dxxjn3lhjdaw9ungf0rcs 250000   (16:39:47Z, FAFO2's contribution; waited for 4 confirmations)`<br>`~/mutinynet/cli.sh waitfornewblock`<br>`~/day0/u openchannel 02c8416ac6ac57fccb5a39dfe7324dcc4677dbb03c99798e1eab1090c1202d2431@127.0.0.1:9736 200000 --public --dual-fund &   (background)`<br>`~/day0/u bumpopen b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 1000`<br>`~/day0/n bumpopen b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 1500`<br>`~/mutinynet/cli.sh getrawmempool` | channel_id: b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4; T0: 22fb4544... 253 sat/kw 16:42:25Z (FAFO initiator); T1: 9be7406b... 1,000 sat/kw 16:42:26Z (FAFO bumpopen, opener); T2: f54cf06a... 1,500 sat/kw 16:42:26Z (FAFO2 bumpopen, accepter as interactive-tx initiator; fee 1,917 sat, 319 vB); mempool_after_bumps: only T2 (T0 and T1 unknown to bitcoind); mined: T2 in 3463283 at 16:42:56Z; T0 and T1 marked Replaced by both chain monitors; open_confirmed_by: 16:43:57Z (depth 3; openchannel returned "Channel is now open!"); scid: 3463283x1x0; capacity_sat: 350000; announced: 16:45:32Z (both nodes); balance_after: FAFO 200,000 / FAFO2 150,000 sat; cli_note: the background openchannel printed the funding of T1 and T2 but never T0 (NL-535); tx [`e8a46402…61e3`](https://mutinynet.com/tx/e8a46402f190ed12bfd7c65ce3ca286edc48f03123539329711748e7477161e3), tx [`22fb4544…93ba`](https://mutinynet.com/tx/22fb4544f2ee1eca4a9bcddb4f70a884721033507d420ac6301786e4663593ba), tx [`9be7406b…527d`](https://mutinynet.com/tx/9be7406b747aa207b439b5e5cda78a3a699f770f030c5f085e5ba032fc55527d), tx [`f54cf06a…bf77`](https://mutinynet.com/tx/f54cf06aa89d94e9327a302efa133564a60d5a4d6de8c9dc60c984cc6831bf77) |
| P2-2 payments both ways and through the hub | HTLCs both directions between the nodes and forwarding by FAFO to and from the faucet hub; with three FAFO-FAFO2 channels the sender picks the direct channel with the most outbound, so not every payment takes the new one | 16:45:50-16:46:03 | `~/day0/n createinvoice 20000000 "n11 u->n"; ~/day0/u payinvoice <bolt11>`<br>`~/day0/u createinvoice 10000000 "n11 n->u"; ~/day0/n payinvoice <bolt11>`<br>`~/day0/n payinvoice "$(scripts/mutinynet/faucet.sh invoice 2000)"`<br>`~/day0/n createinvoice 2000000 "n11 faucet->n"; scripts/mutinynet/faucet.sh withdraw <bolt11>` | payments: `fe9266b9…` 20,000 sat FAFO -> FAFO2 (via fede6471... (the old day-0 channel)); `3968545d…` 10,000 sat FAFO2 -> FAFO (via b78b95b7... (new channel)); `c9f20fbe…` 2,000 sat FAFO2 -> FAFO -> faucet LND hub (fee 1,002 msat) (via fede6471...); `e8c163c7…` 2,000 sat faucet LND hub -> FAFO -> FAFO2 (LNURL-withdraw) (via forwarded over b78b95b7... (3463283x1x0)); balance_after: FAFO 208,000 / FAFO2 142,000 sat |
| P2-3 FAFO splices in 100,000 sat | splice-in with a wallet input on a channel whose funding was RBF'd by both sides, lock at depth 3, new SCID announced at depth 6, old SCID retired for 72 blocks | 16:46:15-16:49:24 | `~/day0/u splicein b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 100000 --feerate 253`<br>`~/day0/u listchannels; ~/day0/n listchannels`<br>`~/day0/u createinvoice 1000000 ...; ~/day0/n payinvoice <bolt11>   (while pending)` | scid_before: 3463283x1x0; scid_after: 3463290x10x1; capacity_after_sat: 450000; mined: 3463290; locked: 16:47:52Z; announced: 16:49:24Z; retired: 3463283x1x0 at 3463290, resolves until 3463362; payment_while_pending: `5a50761e…` 1,000 sat FAFO2 -> FAFO (via f20939f0... (not the splicing channel)); balance_after: FAFO 308,000 / FAFO2 142,000 sat; tx [`e525de6b…00ee`](https://mutinynet.com/tx/e525de6b77796f658e844e1688330bd960d76774c3acceef0751ebd8543f00ee) |
| P2-4 FAFO2 splices out 50,000 sat to a FAFO address | splice-out paying an external address, fee from the splicer's channel balance, amount received on chain, new SCID announced | 16:49:36-16:52:47 | `~/day0/u getaddress p2wpkh   (tb1q4cvd2r4jv6vhzpep5ypzna46kta24hztzhdvlg)`<br>`~/day0/n spliceout b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 50000 --address tb1q4cvd2r4jv6vhzpep5ypzna46kta24hztzhdvlg --feerate 253` | splice_out_output: 35154edcf5ac73cd95394a1a5554fbd4a7b146e7eeaf875de66aa057fd38022f:1, 50,000 sat to tb1q4cvd2r4jv6vhzpep5ypzna46kta24hztzhdvlg (FAFO wallet); scid_before: 3463290x10x1; scid_after: 3463296x6x0; capacity_after_sat: 399816; mined: 3463296; locked: 16:50:57Z; announced: 16:52:47Z; balance_after: FAFO 308,000 / FAFO2 91,816 sat; tx [`35154edc…022f`](https://mutinynet.com/tx/35154edcf5ac73cd95394a1a5554fbd4a7b146e7eeaf875de66aa057fd38022f) |
| P2-5 restart drill: FAFO2 stopped mid-splice | a node stopped with a signed, unconfirmed splice resumes it after 3 blocks: channel_reestablish (my_current_funding_locked taken as splice_locked), lock completes, every channel back | 16:52:56-16:55:31 | `~/day0/u splicein b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 20000 --feerate 253`<br>`~/day0/nodectl fafo2 stop   (16:52:56Z, SIGTERM, 1 s)`<br>`(3 blocks: 3463301 -> 3463304)`<br>`~/day0/nodectl fafo2 start   (16:54:20Z)` | reconnect: FAFO's backoff redialed at 16:55:31Z (71 s after the start; FAFO2 never dials FAFO); scid_before: 3463296x6x0; scid_after: 3463302x13x0; capacity_after_sat: 419816; mined: 3463302; locked: 16:55:31Z (FAFO2 at its restart saw the depth, FAFO's splice_locked came in channel_reestablish); announced: never: replaced by the P2-5b lock before its 6th confirmation; balance_after: FAFO 328,000 / FAFO2 91,816 sat; tx [`d8ea5114…1f3c`](https://mutinynet.com/tx/d8ea5114dec4163c4a06af4f0d8446f25471cd65e6bf6c16c888f9210eb51f3c) |
| P2-5b restart drill: FAFO stopped mid-splice (FAFO2 splices in) | the other node restarted across a pending splice started by the accepter side; FAFO dials on start and every channel (faucet included) reestablishes at once | 16:55:51-16:57:30 | `~/day0/n splicein b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 10000 --feerate 253`<br>`~/day0/nodectl fafo stop   (16:55:51Z)`<br>`(3 blocks: 3463307 -> 3463310)`<br>`~/day0/nodectl fafo start   (16:57:28Z)` | reestablished: all four channels at 16:57:30Z (2 s after the start); scid_before: 3463302x13x0; scid_after: 3463308x9x0; capacity_after_sat: 429816; mined: 3463308; locked: 16:57:30Z; announced: 16:59:13Z; balance_after: FAFO 328,000 / FAFO2 101,816 sat; tx [`8e60b478…d259`](https://mutinynet.com/tx/8e60b4787eeb59e82a0c0cb48c1e3cc2f9adde7087c98b1b3cbb497e17aad259) |
| P2-6 setchannelpolicy | a per-channel policy change is signed, sent as channel_update and seen by the peer's graph | 16:57:51-16:59:13 | `~/day0/u setchannelpolicy b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 --htlc-max-msat 150000000`<br>`~/day0/u getchannelpolicy b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4`<br>`~/day0/n listgraphchannels 3463308x9x0` | htlc_maximum_msat_before: 280000000; htlc_maximum_msat_after: 150000000; seen_by_peer: FAFO2's graph: Policy 2 -> 1 htlc 1000-150000000 msat, updated 16:59:13Z; seen_by_network: mutinynet.com lists the final 3463315x4x1 (459,816 sat) with both policies at 17:12Z, NLightningFAFO max_htlc 150,000,000 msat |
| P2-6b splice RBF drill | bumpsplice replaces the first attempt in the mempool, both nodes list both attempts, a payment over the channel while both are pending, only the bump confirms and locks, the first is Abandoned | 16:59:43-17:02:48 | `~/day0/u splicein b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 30000 --feerate 253`<br>`sleep 6`<br>`~/day0/u bumpsplice b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 1000`<br>`~/day0/u payinvoice <bolt11>; ~/day0/n payinvoice <bolt11>   (tip unchanged, 3463314)`<br>`~/day0/u listchannels; ~/day0/n listchannels` | T1: f4689a94... 253 sat/kw 16:59:43Z, left the mempool when T2 arrived, refused rebroadcasts logged as ERR (NL-534) until Abandoned at 17:01:46Z; T2: 79fd98ae... 1,003 sat/kw (bumpsplice asked 1,000) 16:59:50Z, mined in 3463315, locked 17:01:15Z; both_listed: Pending (Splice) f4689a94...:0 and Pending (SpliceRbf) 79fd98ae...:1 on both nodes; payments_while_pending: `2ff32573…` 3,000 sat FAFO -> FAFO2 (via fede6471...); `1407c6f9…` 2,000 sat FAFO2 -> FAFO (via f20939f0...); `c39816d5…` 160,000 sat FAFO -> FAFO2 (via fede6471...; moved FAFO's outbound so the next payment takes the splicing channel); `41b21877…` 3,000 sat FAFO -> FAFO2 (via b78b95b7... #1, signed on both pending attempts (17:00:06Z, tip 3463314)); scid_before: 3463308x9x0; scid_after: 3463315x4x1; capacity_after_sat: 459816; announced: 17:02:48Z; balance_after: FAFO 355,000 / FAFO2 104,816 sat; tx [`f4689a94…a058`](https://mutinynet.com/tx/f4689a94e6dd283f07dccf678314fc69eb8ac13e28a88e32cddcb66d309ea058), tx [`79fd98ae…5563`](https://mutinynet.com/tx/79fd98aea79c2c349615741350b79a092c130d2a3d1e795c2a564ad43f495563) |
| P2-7 hub routes to FAFO2 through FAFO | third-party payments (faucet LND hub) forwarded by FAFO to FAFO2 after the splices; the hub chose the path (f20939f0's 3463271x11x0 both times; mutinynet.com had not indexed 3463315x4x1 yet) | 17:02:54-17:03:25 | `~/day0/n createinvoice 3000000 ...; scripts/mutinynet/faucet.sh withdraw <bolt11>   (NoRoute: the hub's side of the faucet channel, 4,995,991 msat, minus its 2,000 sat reserve was below 3,000 sat)`<br>`~/day0/n createinvoice 2000000 ...; scripts/mutinynet/faucet.sh withdraw <bolt11>`<br>`~/day0/u payinvoice "$(scripts/mutinynet/faucet.sh invoice 5000)"`<br>`~/day0/n createinvoice 3000000 ...; scripts/mutinynet/faucet.sh withdraw <bolt11>` | payments: `d6efe026…` 3,000 sat faucet LND hub -> FAFO -> FAFO2 (via -; FailureReasonNoRoute at the hub: its side of the faucet channel minus its reserve was below 3,000 sat); `75e24bdf…` 2,000 sat faucet LND hub -> FAFO -> FAFO2 (via forwarded over f20939f0... (3463271x11x0)); `a7a7bef6…` 5,000 sat FAFO -> faucet LND hub (via 0c8daa69...); `9a6f3e4f…` 3,000 sat faucet LND hub -> FAFO -> FAFO2 (via forwarded over f20939f0... (3463271x11x0)) |

On-chain transactions of run 2 (fee = inputs - outputs; the per-node columns are wallet inputs minus wallet change):

| Role | Txid | Block | vB | Fee (sat) | sat/vB | sat/kw | FAFO spent | FAFO2 spent | Outputs |
|---|---|---|---|---|---|---|---|---|---|
| P2-1a dual-funded public open without RBF (channel f20939f0..., stays open) | [`79760d18…3ee1`](https://mutinynet.com/tx/79760d18e1ffc92596f89f567a5d886e1c753fb2540d20c7aee4beb99be33ee1) | 3463271 | 319 | 325 | 1.019 | 254.7 | 200,155 | 150,170 | 0: 350,000 channel funding output (f20939f0..., 350,000 sat)<br>1: 69,428 FAFO wallet<br>2: 78,441 FAFO2 wallet |
| FAFO withdraw of 250,000 sat to FAFO2's wallet (its contribution for the RBF open) | [`e8a46402…61e3`](https://mutinynet.com/tx/e8a46402f190ed12bfd7c65ce3ca286edc48f03123539329711748e7477161e3) | 3463278 | 141 | 143 | 1.014 | 254.9 | 250,143 | -250,000 | 0: 250,000 FAFO2 wallet<br>1: 18,049 FAFO wallet |
| P2-1 dual-funded open attempt T0 at 253 sat/kw (replaced by FAFO's bump, never confirmed) | [`22fb4544…93ba`](https://mutinynet.com/tx/22fb4544f2ee1eca4a9bcddb4f70a884721033507d420ac6301786e4663593ba) | not mined (replaced) | 320 | 325 | 1.016 | 254.3 | 200,224 | 150,101 | 0: 132,902 FAFO wallet<br>1: 99,899 FAFO2 wallet<br>2: 350,000 channel funding output of T0 (b78b95b7..., 350,000 sat) |
| P2-1 opener RBF (bumpopen by FAFO) T1 at 1,000 sat/kw (replaced by FAFO2's bump, never confirmed) | [`9be7406b…527d`](https://mutinynet.com/tx/9be7406b747aa207b439b5e5cda78a3a699f770f030c5f085e5ba032fc55527d) | not mined (replaced) | 320 | 1,278 | 3.994 | 1000.8 | 200,882 | 150,396 | 0: 350,000 channel funding output of T1 (b78b95b7..., 350,000 sat)<br>1: 132,244 FAFO wallet<br>2: 99,604 FAFO2 wallet |
| P2-1 accepter RBF (bumpopen by FAFO2, NL-530) T2 at 1,500 sat/kw (confirmed, the channel's funding) | [`f54cf06a…bf77`](https://mutinynet.com/tx/f54cf06aa89d94e9327a302efa133564a60d5a4d6de8c9dc60c984cc6831bf77) | 3463283 | 319 | 1,917 | 6.009 | 1502.4 | 201,002 | 150,915 | 0: 350,000 channel funding output (b78b95b7..., 350,000 sat)<br>1: 99,085 FAFO2 wallet<br>2: 132,124 FAFO wallet |
| P2-3 splice-in (FAFO +100,000) | [`e525de6b…00ee`](https://mutinynet.com/tx/e525de6b77796f658e844e1688330bd960d76774c3acceef0751ebd8543f00ee) | 3463290 | 248 | 252 | 1.016 | 254.0 | 100,252 | - | 0: 31,872 FAFO wallet<br>1: 450,000 channel funding output (450,000 sat) |
| P2-4 splice-out (FAFO2 -50,000 to FAFO's tb1q4cvd2r4jv6vhzpep5ypzna46kta24hztzhdvlg) | [`35154edc…022f`](https://mutinynet.com/tx/35154edcf5ac73cd95394a1a5554fbd4a7b146e7eeaf875de66aa057fd38022f) | 3463296 | 181 | 184 | 1.017 | 254.8 | -50,000 | - | 0: 399,816 channel funding output (399,816 sat)<br>1: 50,000 FAFO wallet |
| P2-5 restart drill splice-in (FAFO +20,000; FAFO2 stopped) | [`d8ea5114…1f3c`](https://mutinynet.com/tx/d8ea5114dec4163c4a06af4f0d8446f25471cd65e6bf6c16c888f9210eb51f3c) | 3463302 | 248 | 252 | 1.016 | 254.0 | 20,252 | - | 0: 419,816 channel funding output (419,816 sat)<br>1: 49,176 FAFO wallet |
| P2-5b restart drill splice-in (FAFO2 +10,000; FAFO stopped) | [`8e60b478…d259`](https://mutinynet.com/tx/8e60b4787eeb59e82a0c0cb48c1e3cc2f9adde7087c98b1b3cbb497e17aad259) | 3463308 | 248 | 252 | 1.016 | 254.0 | - | 10,252 | 0: 429,816 channel funding output (429,816 sat)<br>1: 88,833 FAFO2 wallet |
| P2-6b splice-in RBF attempt T1 at 253 sat/kw (replaced, never confirmed) | [`f4689a94…a058`](https://mutinynet.com/tx/f4689a94e6dd283f07dccf678314fc69eb8ac13e28a88e32cddcb66d309ea058) | not mined (replaced) | 248 | 252 | 1.016 | 254.0 | 30,252 | - | 0: 459,816 channel funding output of T1 (459,816 sat)<br>1: 19,748 FAFO wallet |
| P2-6b splice-in RBF attempt T2 at 1,000 sat/kw (confirmed) | [`79fd98ae…5563`](https://mutinynet.com/tx/79fd98aea79c2c349615741350b79a092c130d2a3d1e795c2a564ad43f495563) | 3463315 | 249 | 996 | 4.0 | 1003.0 | 30,996 | - | 0: 19,004 FAFO wallet<br>1: 459,816 channel funding output (459,816 sat, current) |

SCIDs of the run-2 channel `b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4`:

| SCID | Capacity (sat) | Funding | Status |
|---|---|---|---|
| [3463283x1x0](https://mutinynet.com/lightning/channel/3463283x1x0) | 350,000 | `f54cf06a...:0` | retired at 3463290 |
| [3463290x10x1](https://mutinynet.com/lightning/channel/3463290x10x1) | 450,000 | `e525de6b...:1` | retired at 3463296 |
| [3463296x6x0](https://mutinynet.com/lightning/channel/3463296x6x0) | 399,816 | `35154edc...:0` | retired at 3463302 |
| [3463302x13x0](https://mutinynet.com/lightning/channel/3463302x13x0) | 419,816 | `d8ea5114...:0` | retired at 3463308, never announced |
| [3463308x9x0](https://mutinynet.com/lightning/channel/3463308x9x0) | 429,816 | `8e60b478...:0` | retired at 3463315 |
| [3463315x4x1](https://mutinynet.com/lightning/channel/3463315x4x1) | 459,816 | `79fd98ae...:1` | current (open, public) |

Backups after each step (`exportchanbackup` then `verifychanbackup`: valid on both), in `~/day0/mutinynet/n11/`, SHA-256:

- `n11-00-upgrade-u.backup` (FAFO): `429fae50142cb5b69d1f362557277150a069fda7416b5e6d30528438eb2be4a9`
- `n11-00-upgrade-n.backup` (FAFO2): `48004fbd1a5ee4d8c8be48a1fa7f256b7861eecee289daf48323236de07e9cdc`
- `n11-01-open-u.backup` (FAFO): `82a73f1d1007a48ff429314607d8d0a3a881eb9bbd20bb50a7cf80160e198318`
- `n11-01-open-n.backup` (FAFO2): `ad2353041db9a217bf7417b45623d413c1477ceac947cfdd8ba03709f553ae77`
- `n11-01b-rbfopen-u.backup` (FAFO): `7236e356b4bcb0a54c788a2dc0bfcfcb17d86866ccefca123c7659531fda88b4`
- `n11-01b-rbfopen-n.backup` (FAFO2): `d8abb19842f50f2fe33ee599dde1e83e5051a9c8ee0f2d2063b7a6868486bceb`
- `n11-02-pay-u.backup` (FAFO): `3754dffbf9dce067e102cd7717c25222ecdb85bce4ada8ca4be7efb92b71873d`
- `n11-02-pay-n.backup` (FAFO2): `850fd1784baabfcf4e38e524c271466870c4a943387540c7094fd5af274459e7`
- `n11-03-splicein-u.backup` (FAFO): `3df04a3cb88935c60c31f0c9a092fc4e19346deefa8545875dca36a1d78645a5`
- `n11-03-splicein-n.backup` (FAFO2): `81d9b27f75c05c0b0707161c3e479103467521dc03aebf0834743d761c689cd1`
- `n11-04-spliceout-u.backup` (FAFO): `042c32842b7ac76019bd8eb6ff30194169b8e1117781885e5f61f71b8ac7dc15`
- `n11-04-spliceout-n.backup` (FAFO2): `6d3e0d249096b928495203f924ce81b62b8d2d63d830a038614e3c68632c0809`
- `n11-05-restart-n-u.backup` (FAFO): `24f2ef9c5e1bf98f75ff2245937b1a743db51a3e77818047c011ab18bd3fc145`
- `n11-05-restart-n-n.backup` (FAFO2): `460799aac3b02535fef467d70cf3df5d01d310fcd97c9dfcd8b631523c3e25d9`
- `n11-05b-restart-u-u.backup` (FAFO): `bc227a160160d440516e8a286408404c90ce9f5ed5df8f57ae783ff8168b0c58`
- `n11-05b-restart-u-n.backup` (FAFO2): `0277d4349c3a1b94b8d20dcb6f66012f8294f186e047031d91a6dbc2c214ba4e`
- `n11-06-policy-u.backup` (FAFO): `f99c7d99f3654e87c5d3d432c9b87bbedbdc1cd717e33975961c3224d6913569`
- `n11-06-policy-n.backup` (FAFO2): `95e9f6452224fe26f6fd99d490590e579222cee48a1ffc649d1db98a0006250c`
- `n11-06b-rbf-u.backup` (FAFO): `c77f12f3ac7ec45f91bc87c2adcb138b12303586d2ba439e253645e976a01e8f`
- `n11-06b-rbf-n.backup` (FAFO2): `b675751b7aaf2bcda5d0403d3811f65287588fc7c3a6418e3d0a3f7ffb53728c`

Run 2 in the results template's form:

```text
Commit SHA (U / N): 2a209a5ed9ce517ecf88c66161c5fe4e76542456 / same, net11.0 self-contained osx-arm64
  (~/.nltg/mutinynet/bin-2a209a5e-net11, ~/.nltg/mutinynet-fafo2/bin-2a209a5e-net11), runtime 11.0.0-rc.1
Upgrade: from dbbcba28 (net10.0), last migration 20260928024953_AddSpliceHardening, AddDualFundAttempts applied at
  the start 16:33:35; 3458334x7x0 reestablished 16:33:38 (19/19), fede6471 16:33:43 (16/16), balances unchanged;
  FAFO paid the faucet 1,000 sat (d09556c1...82e3)
Contributions (U / N) / funding txid / channel id / SCID: 200,000 / 150,000 /
  f54cf06aa89d94e9327a302efa133564a60d5a4d6de8c9dc60c984cc6831bf77:0 (T2 of three attempts) /
  b78b95b78756fd2b1db8f1c4027d935454131ebb5b1ede607fc8852bcda217a4 / 3463283x1x0 (open 16:42:25 -> Open 16:43:57,
  announced 16:45:32)
Open RBF: T0 22fb4544f2ee1eca4a9bcddb4f70a884721033507d420ac6301786e4663593ba 253 sat/kw 16:42:25 / U bumpopen
  1,000 -> T1 9be7406b747aa207b439b5e5cda78a3a699f770f030c5f085e5ba032fc55527d 16:42:26 / N (accepter) bumpopen
  1,500 -> T2 f54cf06aa89d94e9327a302efa133564a60d5a4d6de8c9dc60c984cc6831bf77 16:42:26 (N paid the shared fields);
  only T2 in the mempool, T2 mined in 3463283, T0/T1 Replaced on both
Also opened: f20939f0c87038c0aec6c2de0587d20dd93b2acc2d6f40b1393a64cc1958e51c (200,000 / 150,000, no RBF,
  79760d18e1ffc92596f89f567a5d886e1c753fb2540d20c7aee4beb99be33ee1, 3463271x11x0, announced 16:39:03)
Payments: U->N 20,000 fe9266b9...f118; N->U 10,000 3968545d...2aa0; N->U->faucet 2,000 c9f20fbe...f681 (fee 1,002
  msat); faucet->U->N 2,000 e8c163c7...90af (forwarded over b78b95b7); U->N 3,000 41b21877...ddbb over b78b95b7 while
  both splice RBF attempts were pending; faucet->U->N 2,000 75e24bdf...c5db and 3,000 9a6f3e4f...01f5 (the hub chose
  f20939f0's SCID)
Splice-in: e525de6b77796f658e844e1688330bd960d76774c3acceef0751ebd8543f00ee / 253 sat/kw / block 3463290, locked
  16:47:52 / 3463290x10x1 (450,000), announced 16:49:24
Splice-out: 35154edcf5ac73cd95394a1a5554fbd4a7b146e7eeaf875de66aa057fd38022f / 253 sat/kw (181 vB) / 50,000 to U's
  tb1q4cvd2r4jv6vhzpep5ypzna46kta24hztzhdvlg (output 1, received) / block 3463296, locked 16:50:57 / 3463296x6x0
  (399,816), announced 16:52:47
Restart drills: (N) U splicein 20,000 d8ea5114dec4163c4a06af4f0d8446f25471cd65e6bf6c16c888f9210eb51f3c 16:52:56, N
  stopped 16:52:56, 3 blocks, N started 16:54:20, U's backoff redialed 16:55:31, lock 16:55:31, 3463302x13x0
  (419,816); (U) N splicein 10,000 8e60b4787eeb59e82a0c0cb48c1e3cc2f9adde7087c98b1b3cbb497e17aad259 16:55:51, U
  stopped 16:55:51, 3 blocks, U started 16:57:28, all four channels reestablished 16:57:30, lock 16:57:30,
  3463308x9x0 (429,816), announced 16:59:13
Policy: U setchannelpolicy --htlc-max-msat 150000000 at 16:57:51, in N's graph at 16:59:13; mutinynet.com listed the
  final 3463315x4x1 (459,816 sat) with both policies, NLightningFAFO's max_htlc 150,000,000, at 17:12
Splice RBF: T1 f4689a94e6dd283f07dccf678314fc69eb8ac13e28a88e32cddcb66d309ea058 at 253 sat/kw 16:59:43 / bump T2
  79fd98aea79c2c349615741350b79a092c130d2a3d1e795c2a564ad43f495563 at 1,000 sat/kw 16:59:50 / both pending on both
  nodes, T1 out of the mempool at once, T2 mined in 3463315, locked 17:01:15 / 3463315x4x1 (459,816), announced
  17:02:48; T1 Abandoned 17:01:46
Close: none (f20939f0 and the run-1 channel fede6471 stay open)
Deviations / new NL entries: NL-535 (low): `openchannel` blocks until channel_ready and never prints the first
  attempt of a dual-funded open, so `bumpopen` needs `openchannel ... &` or a second terminal. The first run's
  `Node:DualFund:AllowRbf=false` had to be changed for `bumpopen`. NL-518 (MempoolReactor warning per splice) and
  NL-534 (refused rebroadcast of the replaced splice attempt at ERR) seen again. The hub routed its payments to N
  over f20939f0 (its choice; mutinynet.com had not indexed 3463315x4x1 yet).
```

**Nodes left running (2026-09-28, build `2a209a5e` net11.0).** Both on their own staged copy
`<dir>/bin-2a209a5e-net11` (`bin-current`), started with `~/day0/nodectl`. Open channels: FAFO-FAFO2 `b78b95b7...`
(3463315x4x1, 459,816 sat, FAFO 355,000 / FAFO2 104,816 sat), `f20939f0...` (3463271x11x0, 350,000 sat, 198,000 /
152,000), `fede6471...` (3462164x1x0, 449,816 sat, 154,002 / 295,814) and FAFO's faucet channel `3458334x7x0`.
`~/.nltg/mutinynet/bin-dbbcba28` stays on disk, but that build predates `AddDualFundAttempts`: going back to it
needs the schema rollback of §2.1 step 8 (node stopped, `dotnet ef database update 20260928024953_AddSpliceHardening`),
never an old `nltg.db`.
