# Day-0 runbook: a dual-funded public channel between two NLightning nodes, then splices

Status: **the Mutinynet phase passed** on 2026-09-28 (dry run with two NLightning nodes on the owner's machine, the
live NLightningFAFO upgraded in place as U and a new NLightningFAFO2 as N): a first pass on `d5d8b184` found NL-517
(fixed in `d5be5b73`), the clean pass on `91591787` ran every step of §2.2 including the splice RBF drill (results in
§5). The rehearsal with Nick's own node and mainnet (§3) are still to do. Written on 2026-09-27 in wave sp2 (lane
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
- [ ] **Features.** `option_splice`, `option_quiesce` and `option_dual_fund` all stay No and experimental through
      wave spr and at `91591787` (plan D13 not applied, DF3 not scheduled): `FeatureOptions` defaults them to `No`
      and lists them in `ExperimentalFeatures`, so splicing and dual funding need `AllowExperimentalFeatures`. The
      runbook sets all four explicitly, so it does not depend on the defaults of the commit (the keys are under
      `Node:Features`; `DualFund` is the property name of `option_dual_fund`):

      ```jsonc
      "Node": {
        "Features": {
          "AllowExperimentalFeatures": true,
          "OptionQuiesce": "Optional",
          "OptionSplice": "Optional",
          "DualFund": "Optional"
        },
        "DualFund": { "AcceptContributionSat": 0, "MatchOpenerContribution": true, "AllowRbf": false }
      }
      ```

      `AllowExperimentalFeatures` also allows every other experimental feature; leave the others at their defaults
      (for example `OptionAttributionData` stays `No`). For a rehearsal of the splice RBF drill (§2.2 step 6b) on
      Mutinynet's ~30 s blocks also set `"Splice": { "MinRbfInterval": "00:00:05" }` on both nodes (default 1 min,
      longer than a Mutinynet block); leave the default on mainnet.
- [ ] **Known gaps accepted** (check [`ISSUES.md`](ISSUES.md) at the commit for the NL-021/NL-037 follow-ups, e.g.
      NL-470 and NL-478..NL-483):
  - **Splice RBF only from a build with wave SPR integrated** (`bumpsplice`, Proof SPR and `Day0FlowTests` step 9
    green at the commit). Without it a splice whose feerate is too low cannot be bumped and the channel keeps working
    on the old funding until it confirms. Even with it, pick a feerate that confirms (§3.3): every bump is a new
    negotiation with the peer (both online, quiescence), and the peer refuses a bump of an attempt it considers created
    recently (`Splice:MinRbfInterval` on its side). Bump only **your own** splice: a CLN peer contributes nothing to an
    RBF it did not start (Proof SPR header), so an RBF of the other side's splice drops that side's contribution.
  - **No RBF of a dual-funded public open** (`DualFundingOptions.AllowRbf`: refused for a public channel). Same rule:
    pick a funding feerate that confirms.
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
   `Node:Features`/`Node:DualFund` block of §1. For the rehearsal with Nick also set our reachable address so his
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
$U openchannel $NICK 300000 --public --dual-fund      # prints the channel id: CH
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
# wait at least Nick's Splice:MinRbfInterval (1 min by default; 5 s for the Mutinynet rehearsal, §1), and do it
# before the next block: on 30 s blocks create both invoices first and run splicein, bumpsplice and both payinvoice
# back to back (the dry run's first pass bumped 6 s after T1 and T2 was mined 11 s later, before any payment)
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
  - the `Node:Features`/`Node:DualFund` block of §1, with `AcceptContributionSat` on the accepter only (Nick);
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
