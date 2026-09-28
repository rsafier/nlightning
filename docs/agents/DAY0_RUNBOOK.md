# Day-0 runbook: a dual-funded public channel between two NLightning nodes, then splices (draft)

Status: **draft, nothing run yet.** Written on 2026-09-27 in wave sp2 (lane SP2-F) against the SP2 contracts
(`wip/fafo-sp2-contracts` @ `3560f3a9`). The splice completion it relies on (reestablish across a splice, the lock
and new short channel id, re-announcement, on-chain handling across fundings, backups of the current funding) lands in
the other SP2 lanes. Items marked **(unverified)** have not been checked on any live network. Never paste secrets (RPC
password, key password) into this file, a shell history, a log or a chat.

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
      in every wave so far; mixed builds are not supported.
- [ ] **Tests green at that commit** (root `CLAUDE.md` "Build / test / format"): Release and Release.Native builds
      with only the baseline warnings, `dotnet format --verify-no-changes`, the non-Docker tests on net10.0, and the
      Docker proofs above plus the LND, CLN, ABCD, gossip and on-chain suites of the SP2 integration record.
- [ ] **Features.** After the SP2 integration `option_splice` and `option_quiesce` are Optional by default (plan D13);
      `option_dual_fund` stays experimental (plan DF3), so dual funding needs `AllowExperimentalFeatures`. The runbook
      sets all four explicitly, so it does not depend on the defaults of the commit:

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
      (for example `OptionAttributionData` stays `No`).
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
  - **A peer that connects to us from `127.0.0.1` is not saved** (`PeerManager` stores an inbound peer only when its
    host is not loopback), and a restarted node loads its channels peer by peer from that table: the channel is
    forgotten at the restart and the peer's `channel_reestablish` gets `error` "unknown channel" (found by
    `Day0UpgradeInPlaceTests` in wave sp2, reported to the ledger). Never run the day-0 connection through a local
    tunnel (SSH port forward, Tor on the same host). An inbound peer from any other address is saved with its host
    and port 9735 (not the port it dialed from), so the side that dials must itself listen on 9735 for the other
    side's reconnects. Check with `listpeers` on both sides, and after the first restart that `listchannels` still
    lists the channel.
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

   Continue only when `listchannels` shows the channel `Open`, `HTLCs (out/in): 0/0`.
2. **Stop the node.** If the gossip soak runs it: `scripts/mutinynet/soak-gossip.sh stop` (it stops the daemon it
   started); otherwise Ctrl-C in the `start-daemon.sh` terminal, or the daemon's `--stop`. Check nothing is left:
   `pgrep -fl NLightning.Daemon` prints nothing.
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
6. **Start and watch the migration.** `scripts/mutinynet/start-daemon.sh &`. The log shows EF applying every
   migration after the node's last one, at least `..._AddSpliceFundings` (and any SP2 migration). Then:

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

   mutinynet.com still lists NLightningFAFO with its channel `3458334x7x0`.
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

```bash
# 0. both: funded wallets (contribution + anchors reserve 10,000 sat + fees), same commit
$U info; $N info; $U walletbalance; $N walletbalance

# 1. dual-funded public open (we are the opener, Nick contributes as accepter); not over a loopback tunnel (§1)
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
$U listchannels; $N listchannels                     # Reestablished Yes on both, the splice locks; backups

# 6. routing policy
$U setchannelpolicy $CH --htlc-max-msat 150000000
$U getchannelpolicy $CH                              # and mutinynet.com / the faucet LND show the new maximum

# 6b. splice RBF drill (only with wave SPR in the build; Day0FlowTests step 9): a splice at the floor, then bumped
$U splicein $CH 30000 --feerate 253
$U listchannels                                      # fundings: one pending splice; note its txid (T1)
# wait at least Nick's Splice:MinRbfInterval (1 min by default), and do it before the next block if you can
$U bumpsplice $CH 1000                               # bumpsplice <channel_id> <feerate_per_kw> [--max-fee-sat <sats>]; prints the new txid (T2)
$U listchannels; $N listchannels                     # both list T1 and T2 pending (T2 an RBF attempt)
# mutinynet.com: T2 replaced T1 in the mempool. Pay once each way while both are pending, then wait for the lock:
# both ends run on T2, the new SCID is announced after 6 blocks, T1 never confirms; backups on both

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