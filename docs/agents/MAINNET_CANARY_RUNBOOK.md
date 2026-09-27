# Mainnet canary runbook (draft for owner approval)

Status: **draft, nothing run yet.** This runbook was written on 2026-09-27 against `wip/fafo` @ `88d046c7` (after
wave d12). No step here has been run on mainnet. It is modelled on the Mutinynet smoke test that passed in wave 5
([`MUTINYNET.md`](MUTINYNET.md) "Live smoke test"). Items marked **(unverified)** have not been checked on any live
network. Never paste secrets (RPC password, key password) into this file, a shell history, a log or a chat.

The goal is one small private anchors channel with real funds: open it, pay, receive, restart, close cooperatively,
and get the coins back on chain. Each step has abort criteria. The force-close drill (§5) needs a separate approval.

## 1. Preconditions and go/no-go

**Budget.** The owner picks the amount. Suggested total: **100,000-200,000 sat**, and never more than you can lose.
Example split for 200,000 sat: a 150,000 sat channel, 10,000 sat anchors reserve, and the rest for the funding,
close and CPFP fees.

**Go/no-go checklist** (all must be "go"):

- [ ] **Canary commit.** Record the SHA with `git log -1`. The tree is clean, with no local changes.
- [ ] **Tests green at that SHA** (CLAUDE.md "Build / test / format"):
  - `dotnet build -c Release` and `-c Release.Native`: 0 errors, only the 5 baseline CS86xx warnings.
  - `dotnet format --verify-no-changes`.
  - Non-Docker tests on net10.0: all pass, no skips.
  - Docker suites on net10.0, from the in-container runner: anchors (18), legacy on-chain with `-explicit on` (24),
    LND (59), CLN (22), ABCD `scripts/run-abcd.sh 3`, and gossip. These are the suites of the O7b and d12 records.
- [ ] **Features on by default on mainnet at this commit.** Check them against the written `appsettings.json`:
  - HTLCs: **on**. `Node:EnableHtlcs` is `null`, which means on (O6-T4, `BOLT5_ONCHAIN_PLAN.md` "O6-T4 decision").
  - Anchors: **on**. `option_anchors` is Optional (O7-T4). The wallet must hold **confirmed** funds for the reserve:
    10,000 sat per anchors channel, capped at 100,000 sat (`Node:Anchors:ReservePerChannel`/`MaxReserve`). Without
    them the open is refused.
  - Gossip graph and sync: **on**. Relay of other nodes' gossip: **off** (`Gossip:RelayEnabled=false`, NL-417). The
    memory budget is `Gossip:MaxMemoryMb=1024`. See `BOLT7_GOSSIP_PLAN.md` "D12 decision".
  - Public channels: **off**. `Gossip:AllowPublicChannelsOnMainnet=false` refuses `openchannel --public` and a
    peer's public open. Leave it off for the canary unless the owner opts in (§3, optional).
  - Experimental features (route blinding, attribution_data, dual funding): off
    (`Features:AllowExperimentalFeatures=false`).
- [ ] **Known gaps accepted** (from [`ISSUES.md`](ISSUES.md) and [`REMAINING_WORK.md`](REMAINING_WORK.md)):
  - **No on-chain send or withdraw over IPC** (no NL ID yet; REMAINING_WORK "Wallet features"). After the close, the
    coins stay in the nltg wallet. They can only leave through another channel open until a send command lands.
  - **No standard seed (NL-159).** The node key is a random key in `nltg.key.json`, not a BIP39 mnemonic. No other
    wallet can restore it. The key file plus its password is the only backup of the on-chain funds.
  - **No channel backup or restore on `wip/fafo`.** The RF1 lane `wip/fafo-rf1-r1-backup-restore-*` adds encrypted
    static channel backups (`exportchanbackup`/`verifychanbackup`, a `channel.backup` file). It is not merged at the
    time of writing. **If present at the canary commit, use it** (§6). A restore path is not written anywhere yet.
  - NL-279: HTLCs the peer adds after our `shutdown` are not failed back. Close only with `HTLCs (out/in): 0/0`.
  - NL-280 / NL-305: the wallet can reuse addresses. This is a privacy issue, not a funds issue.
  - NL-259: a funder channel forgotten at startup keeps its UTXO locks. NL-392/NL-393: a failed open leaves a
    temporary channel in memory. A restart clears it.
  - NL-294: a broadcast the node refuses for good is retried forever (noise in the logs).
  - Anchors follow-ups: NL-384, NL-386, NL-389, NL-390, NL-391 (some anchors state is memory-only, and a peer's
    low-fee commitment is not packaged). They matter only in a force close; none is rated a fund-safety blocker.
  - NL-330, NL-336 and NL-335 concern forwarding and on-chain final-hop edge cases. A canary does not forward.
  - NL-306: relative paths resolve against the working directory. Always start the daemon from `~/.nltg/mainnet`.
  - NL-298: network resolution is not unified. The mainnet force-close path (`ChannelFailureService`, the
    builders) has run only on regtest in Docker. It has never run on a live network (unverified).
- [ ] **bitcoind is Bitcoin Core 28 or newer.** Package relay (`submitpackage`, NL-380) needs it.
- [ ] The owner has read §6 and accepts it: **never** start the node on an old copy of the database.

## 2. Setup

### 2.1 bitcoind (M4 Mac, 192.168.1.165)

Configure the unpruned mainnet node with:

- `server=1`
- an `rpcauth=` line for a dedicated user
- `rpcbind`/`rpcallowip` restricted to the nltg host (`/32`)
- `zmqpubrawblock=tcp://0.0.0.0:28332` and `zmqpubrawtx=tcp://0.0.0.0:28333`

nltg needs no bitcoind wallet. RPC travels over HTTP on the LAN, so the rpcauth password crosses the LAN in clear.
If nltg runs on the same Mac, use `127.0.0.1` instead.

From the nltg host, check that RPC and ZMQ are reachable. Keep the credentials in a mode-600 curl config file, not
on the command line:

```bash
curl -s -K ~/.nltg-rpc.curl --data-binary '{"jsonrpc":"1.0","id":"c","method":"getblockchaininfo","params":[]}' \
     -H 'content-type: text/plain;' http://192.168.1.165:8332/ | jq '.result | {chain, blocks, headers, pruned}'
nc -z 192.168.1.165 28332 && nc -z 192.168.1.165 28333
```

Expect `chain: "main"`, `blocks == headers` and `pruned: false`.

### 2.2 Build and stage

Build Release for net10.0 at the canary SHA, with `dotnet build` for `src/NLightning.Daemon` and
`src/NLightning.Client` (`-c Release -f net10.0`). Then **copy both output folders** to
`~/.nltg/mainnet/bin/{daemon,client}`, as `soak-gossip.sh` stages its build. A rebuild of the checkout must never
swap binaries under a node that holds funds. Record the staged SHA in `~/.nltg/mainnet/bin/SHA`.

### 2.3 First start: template and key

```bash
mkdir -p ~/.nltg/mainnet && chmod 700 ~/.nltg/mainnet && cd ~/.nltg/mainnet
~/.nltg/mainnet/bin/daemon/NLightning.Daemon --network mainnet --status   # writes appsettings.json, creates no key
```

The key is created at the first real start (§2.5), after bitcoind is configured: that start reads the tip height as
the wallet birthday.

Choose a **strong password** (a password manager, 20 or more random characters) and store it off the machine. Pass
it with `--password-stdin`, or through a mode-600 password file that you create yourself
(`--password-file ~/.nltg/mainnet/.password`). Do **not** use `scripts/mutinynet/start-daemon.sh` for mainnet: it
creates a random password file next to the key, so the key file's encryption would protect nothing and nobody would
know the password. Never use `--password` (it is visible in the process list).

### 2.4 `~/.nltg/mainnet/appsettings.json` (change only these)

```jsonc
"Node": { "ListenAddresses": [ "127.0.0.1:9735" ], ... },   // optional: we dial out, a private canary needs no inbound
"Bitcoin": {
  "RpcEndpoint": "http://192.168.1.165:8332",
  "RpcUser": "<rpcauth user>",
  "RpcPassword": "",                 // leave empty and set NLTG_Bitcoin__RpcPassword in the daemon's environment
  "ZmqHost": "192.168.1.165",        // the template writes "bitcoinzmq" 8334/8335: wrong for this setup
  "ZmqBlockPort": 28332,
  "ZmqTxPort": 28333
},
"FeeEstimation": { "Source": "Bitcoind", "ConfirmationTarget": 6, "EstimateMode": "CONSERVATIVE" },
"Database": { "RunMigrations": true }   // first run only; SQLite nltg.db in ~/.nltg/mainnet
```

`Bitcoind` fees avoid calling mempool.space from the node's IP. The template default (`Http`, `fastestFee`) is
acceptable but pays more. Leave `EnableHtlcs`, the `Gossip` section and `Features` as written, then `chmod 600` the
file.

### 2.5 Start and verify

```bash
cd ~/.nltg/mainnet && NLTG_Bitcoin__RpcPassword=... ./bin/daemon/NLightning.Daemon --network mainnet --password-stdin 2>&1 | tee -a daemon.out
chmod 600 ~/.nltg/mainnet/nltg.key.json      # check the mode; it should already be 600
C=~/.nltg/mainnet/bin/client/NLightning.Client
$C --network mainnet info
$C --network mainnet chainstatus             # "Processing: running", last processed block == bitcoind tip
$C --network mainnet describegraph           # the graph fills once a peer is connected (the canary peer)
```

**Back up now:** copy `nltg.key.json` to offline storage and keep the password separately. Record the node id from
`info` (it is public).

## 3. Canary steps

Run `$C --network mainnet <cmd>` for each command below. Keep a second terminal on
`tail -f ~/.nltg/mainnet/logs/log-*.txt`.

1. **Fund the wallet.**
   - Get an address with `getaddress p2wpkh` (proven live on Mutinynet).
   - From an external wallet, send the budget in **one** output.
   - Wait for **at least 3 confirmations**. The anchors reserve counts only mined outputs that pass the three-block
     rule.
   - `walletbalance` should show Confirmed = the deposit and Spendable = Available minus the reserve.
   - Look the transaction up at `https://mempool.space/tx/<txid>`.
2. **Choose the peer.** It must support anchors and accept a small, private, inbound channel. Every candidate's
   minimum channel size is **(unverified)**: check it on amboss.space or 1ml.com. A refused open costs no on-chain
   fee, because nothing is broadcast before `funding_signed`.
   - **Best: a node the owner or a friend runs (LND 0.18 or newer, or CLN).** Both sides can be observed, invoices
     can be issued on demand, and LND is the implementation proven most (LND suite 59/59, ABCD, the on-chain
     proofs).
   - **A large, well-connected LND node** with a low minimum channel size. Our Docker proofs run against LND 0.20.
   - **ACINQ** (`03864ef0…@3.33.236.230:9735`, Eclair). It is very reliable and was a clean gossip peer in the
     mainnet probe. However, **our channel and HTLC interop with Eclair is untested**, and its minimum channel size
     may exceed a canary budget (unverified).
   - Avoid peers that need a public channel or charge LSP fees.
3. **Open ONE private anchors channel.**
   - Run `connect <id>@<host>:9735`, then `openchannel <id>@<host>:9735 150000`.
   - Add `[push_sats]` only toward your own or a friend's node: pushed sats are given away.
   - Record the funding outpoint. Since NL-303 it prints in display order.
   - Wait for the peer's `minimum_depth` (often 3 blocks, peer-dependent). Expect `listchannels` to show State
     `Open`, Reestablished `Yes`, a Short Channel Id and Capacity 150000.
   - **Optional public variant** (separate owner opt-in): set `Gossip:AllowPublicChannelsOnMainnet=true` and use
     `openchannel ... --public`. The channel is announced after 6 blocks, and the node becomes visible on
     `mempool.space/lightning/node/<id>`. Not recommended for the first canary.
4. **Pay a small invoice (1,000-10,000 sat).**
   - First pay the peer itself (a direct payment that needs no graph), then a third party through the graph: a
     friend's wallet, or a Lightning Address.
   - For a Lightning Address: `GET https://<domain>/.well-known/lnurlp/<user>`, then `GET <callback>?amount=<msat>`;
     the invoice is in `.pr`.
   - Run `payinvoice <bolt11> --max-fee-msat 5000 --timeout 60`, then `listpayments`. Record the payment hash and
     preimage.
   - Paying through the graph from a private first hop on mainnet is **(unverified)**. `getroute <node> <msat>`
     shows the route first.
5. **Receive a small payment.**
   - Run `createinvoice 5000000 "canary receive"`. The invoice carries an `r` route hint for the private channel.
   - Pay it from another wallet. The inbound liquidity comes from step 4 or a push.
   - `listinvoices` should show it Settled.
6. **Restart.**
   - Only when `listchannels` shows `HTLCs (out/in): 0/0`.
   - Stop with Ctrl-C (foreground) or `--stop`, then start again as in §2.5.
   - Expect `Reestablished: Yes` within about 1 minute, with unchanged balances and commitment numbers. Pay once
     more (1,000 sat).
7. **Cooperative close.**
   - Run `closechannel <channel_id> 0 300` (0 = the fee estimator; we pay the fee as funder).
   - Record the closing txid, then wait for 1-6 confirmations.
   - Expect `listchannels` to show `Closed` and `walletbalance` to rise by the local balance minus the close fee.
   - `pendingsweeps` should report nothing to resolve.

## 4. What to watch, and abort criteria

| Step | Watch | Abort (stop and investigate; do not retry blindly) |
|---|---|---|
| Any | `chainstatus`; ERR/WRN in the log; RSS (the gossip sync peaked at about 610 MB) | `Processing: HALTED`; any unhandled exception; the last processed block stays behind the tip for more than 3 blocks |
| Fund | `walletbalance`, mempool.space | The deposit is not seen after 1 confirmation (ZMQ or birthday problem) |
| Open | log: open → accept → funding_signed → broadcast; mempool.space | Funding not in the mempool after 10 min; not confirmed after 12 blocks at a sane feerate; State `Failed`; any `error` from the peer |
| Pay/receive | `listpayments`, `listinvoices`, `listchannels` HTLC counts | A payment in flight past its timeout with HTLCs that do not clear within about 10 min; balances that do not add up to the capacity minus the funder's commitment fee and anchors |
| Restart | `Reestablished`, commitment numbers | `DATA LOSS DETECTED`; `Reestablished: No` after 5 min connected; the peer sends `error` |
| Close | closing tx on mempool.space, `pendingsweeps` | Not broadcast within the wait; a commitment tx appears instead of a closing tx (the peer force-closed: go to §6) |

If a channel ends in `Failed` or `OnchainResolving`: **do not stop the node.** It must stay online to sweep.

## 5. Failure drills (optional, separate approval)

**Force close a tiny channel.** Use a second channel of 50,000-100,000 sat to the same peer; the reserve stays
covered. Then:

1. Run `forceclosechannel <id>`. Our anchors commitment pays a low fee, and `AnchorCpfpService` CPFPs it through
   `to_local_anchor` with wallet funds (package relay if it is below the mempool minimum).
2. Watch `pendingsweeps <id>` and mempool.space.
3. Our `to_local` is swept after the peer's `to_self_delay` (LND: 144 blocks or more, about 1 day). The anchors can
   be swept after 16 blocks when it pays.

**Cost estimate (unverified).** Commitment about 280 vB, CPFP child about 150-200 vB, to_local sweep about 120 vB:
roughly 550-600 vB in all. That is about 3,000 sat at 5 sat/vB and about 12,000 sat at 20 sat/vB, plus the funds
locked for about a day.

**First live run.** This would be the first force close on any live network. The Docker proofs (anchors 18/18,
on-chain 24/24) are regtest only.

## 6. Rollback and recovery

- **Crash mid-payment.** Restart the same database from `~/.nltg/mainnet`. Startup reverts uncommitted updates,
  replays pending events and reestablishes. In-flight payments are reconciled (`listpayments`). Do not pay the same
  invoice again until the first attempt is final.
- **The channel force-closed (either side).** Keep the node running and connected to bitcoind. The resolvers claim
  our outputs: `to_local` after the CSV delay, HTLCs by timeout or preimage, and a penalty if the peer broadcast a
  revoked state. Watch `pendingsweeps`. Keep the anchors reserve in the wallet.
- **Database lost** (the key file is safe):
  - If RF1 backups are present, run `verifychanbackup` on the last `channel.backup`. There is **no restore command
    yet**, so the backup holds what a manual recovery of the peer's force close needs (outpoint, key index, peer).
  - Without a backup: on reconnect the peer gets an `error` for an unknown channel and will likely force-close. Its
    `to_remote` output pays a key derived from our key file at the channel's key index. The fresh wallet does not
    watch it, so recovery needs manual tooling (unverified, none exists).
  - Therefore: take **cold** copies of `nltg.db`, with the node stopped or through `sqlite3 .backup`, for forensics
    only.
- **NEVER start the node on an old copy of `nltg.db`, and never run two nodes with the same key.** A stale database
  broadcasts a revoked commitment, or signs from an old state, and the peer takes the whole channel as a penalty.
  After any restore, if `listchannels` shows `DATA LOSS DETECTED`, do not force-close. Let the peer close, and
  record the channel.
- **Lost key file or password:** the on-chain funds and the channel funds are lost (NL-159).

## 7. Results template

```text
Canary commit SHA / staged SHA:
bitcoind version / tip at start:
Node id:
Deposit txid / amount / confirmed at block:
Peer (id@host, implementation/version):
Funding txid:vout (display order) / fee / feerate:
Channel id / SCID / open time (broadcast -> Open):
Pay #1 (direct): hash / amount / fee / result / duration:
Pay #2 (graph or LN address): hash / amount / fee / route hops / result / duration:
Receive: invoice hash / amount / settled at:
Restart: time down / Reestablished after (s) / commitment l/r before and after:
Close: closing txid / fee / confirmed at block / wallet balance after:
Peak RSS / DB+WAL size / warnings and errors (count, first lines):
Drill (if approved): commitment txid / CPFP child txid / sweep txid / total fees / blocks to done:
Anomalies (new NL IDs filed):
```
