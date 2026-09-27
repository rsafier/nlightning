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
| `Day0FlowTests` | Two NLightning nodes A and B, LND alice watching: (1) dual-funded public open, both contribute, alice has the `channel_announcement` and both policies at 6 confirmations; (2) payments A→B, B→A, alice→A→B; (3) A splices in: lock, new SCID announced, old SCID gone from alice's graph; (4) B splices out to an address: same, and the address holds the amount in a confirmed tx; (5) B crashes as it sends `tx_signatures` of a splice, restarts, the splice completes; B is down while it confirms, restarts, the lock completes; (6) `setchannelpolicy` on A, alice sees the new `htlc_maximum_msat`; (7) `exportchanbackup` + `verifychanbackup` on both after every step, on the current funding; (8) cooperative close. |
| `Day0UpgradeInPlaceTests` | A SQLite database with the pre-sp1 schema (the migration before `AddSpliceFundings`) holding a used v1 channel migrates at startup, the channel reestablishes with unchanged balances and commitment numbers, pays both ways and then splices. |

Also required green: Proof SP1 (`Docker/Interop/Cln/ClnSpliceTests`), Proof DF (`ClnDualFundTests`) and Proof SP2
(`ClnSpliceReestablishTests`, `Docker/Onchain/OnchainSpliceTests`, lane SP2-D).

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
  - **No splice RBF** (wave SPR not done). A splice whose feerate is too low cannot be bumped by the splice protocol;
    the channel keeps working on the old funding until it confirms. Pick a feerate that confirms (§3.3).
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
   ```

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
   update** and forensics only (§4).
4. **Stage the new build.** Check out the day-0 commit, then `scripts/mutinynet/build.sh`, and record
   `git log -1 --format=%H > ~/.nltg/mutinynet/bin-SHA`. `start-daemon.sh` runs the binaries from the checkout's
   `bin/Release/net10.0`, so do not rebuild the checkout while the node runs (or stage copies as the soak does).
5. **Configure** `~/.nltg/mutinynet/appsettings.json` (with `jq`, or by hand): `Database:RunMigrations=true` and the
   `Node:Features`/`Node:DualFund` block of §1. For the rehearsal with Nick also set our reachable address so his
   node can dial us: `Gossip:AnnounceAddresses: ["<public ip>:9735"]` and a matching `Node:ListenAddresses`.

   ```bash
   cd ~/.nltg/mutinynet && cp appsettings.json appsettings.json.pre-day0
   jq '.Database.RunMigrations = true
       | .Node.Features.AllowExperimentalFeatures = true
       | .Node.Features.OptionQuiesce = "Optional" | .Node.Features.OptionSplice = "Optional"
       | .Node.Features.DualFund = "Optional"' appsettings.json.pre-day0 > appsettings.json
   chmod 600 appsettings.json
   ```
6. **Start and watch the migration.** `scripts/mutinynet/start-daemon.sh &`. The log shows EF applying every
   migration after the node's last one, at least `..._AddSpliceFundings` (and any SP2 migration). Then:

   ```bash
   $U chainstatus                  # Processing: running, last block == tip
   $U listchannels                 # 3458334x7x0: Open, Reestablished Yes, same balances and commitment numbers,
                                   #   one funding (Initial, Current) on 12482a42...8d0c:0
   $U listpeers                    # the faucet LND connected
   ```

   **Abort** if the migration fails (the daemon exits: stop there and restore the step 3 copy, which is safe because
   no channel update happened yet), if the channel is not reestablished within 5 minutes, or on `DATA LOSS
   DETECTED`.
7. **Verify the channel works** and back it up:

   ```bash
   $U payinvoice "$(scripts/mutinynet/faucet.sh invoice 1000)"
   $U createinvoice 1000000 "day0 upgrade check"             # copy the lntbs... invoice
   scripts/mutinynet/faucet.sh withdraw <that invoice>
   $U exportchanbackup --output ~/day0/mutinynet/post-upgrade.backup
   $U verifychanbackup ~/day0/mutinynet/post-upgrade.backup   # valid, the channel, keys match
   ```

   mutinynet.com still lists NLightningFAFO with its channel `3458334x7x0`.
8. **Rollback, if needed.** Before step 7 (no new commitment signed): stop, restore the step 3 copy of `nltg.db`,
   start the old build. **After** step 7 the old copy is a revoked state: **never restore it**. Instead keep the
   upgraded database and, if the old build must run, roll its schema back with the new checkout, node stopped:
   `cd src/NLightning.Infrastructure.Persistence && NLIGHTNING_SQLITE="Data Source=$HOME/.nltg/mutinynet/nltg.db"
   dotnet ef database update 20260927163921_AddInteractiveTxSessions --framework net10.0` (the migration before
   `AddSpliceFundings`, which rolls back any later SP2 migration with it; the design-time factory picks SQLite from
   `NLIGHTNING_SQLITE`, as `scripts/add_migration.sh` does; unset `NLIGHTNING_POSTGRES` first) **(unverified on a
   live file; `Day0UpgradeInPlaceTests` does the same rollback with EF's migrator)**. `AddSpliceFundings`' `Down` refuses while any
   channel runs on a locked splice (its rotated funding keys live only in `ChannelFundings`): after a splice there is
   no way back to a pre-sp1 build.

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
  - For `splicein`/`spliceout`, pass `--feerate <sat/kw>` = the "halfHourFee" (sat/vB) x 250, at least 253. There is
    no RBF of a splice yet, so err high: a stuck splice leaves the channel on its old funding until it confirms.
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
| Splice signed, unconfirmed | feerate too low | the channel keeps working (payments are signed for both fundings); wait for the confirmation; never restart on an old database |
| A node restarts mid-splice | crash, reboot | start it again on the **same** database: `channel_reestablish` retransmits what is missing (Day0 proof step 5) |
| Either side misbehaves or the channel fails | `Failed`, peer `error` | **keep the node running**: it broadcasts the commitment of the current funding (a pending splice's if that one confirmed) and sweeps (BOLT 5; `pendingsweeps`); `forceclosechannel <CH>` only as the last resort |
| Upgrade went wrong | migration or reestablish failure | §2.1 step 8 (never an old `nltg.db` after a channel update) |

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
Restart drill: what was stopped, when, outcome:
Backups (step, file, verifychanbackup result):
Close (if any): txid:
Deviations from the Docker proof / new NL entries:
```