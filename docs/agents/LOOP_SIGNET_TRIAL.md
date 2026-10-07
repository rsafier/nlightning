# Public signet Loop trial (real loopd against NLightning's LND gRPC)

Date: 2026-10-06. Result: **real Lightning Labs `loopd` v0.35.0-beta runs against an NLightning node on the public
signet and completes a Loop Out, a classic Loop In, a static-address Loop In and a cooperative static-address withdrawal
against Lightning Labs' signet Loop server**. The static Loop In first failed because of an NLightning bug (NL-1233, fixed on this branch in `91759661`).

## Setup

| Item | Value |
|---|---|
| NLightning | `57dc482e` (wip/fafo) at the start; `91759661` (this branch, NL-1233) from 20:47 on. Release net11.0 osx-arm64 self-contained (`~/.nltg/signet-loop/bin-*`) |
| Node | `0276430842babe3f78da54a02ebef2186ee5ee85e08f208ce7c22df37dbbc92964`, alias `nltg-signet-loop`, config `~/.nltg/signet-loop` |
| Listeners | P2P `127.0.0.1:9745` (outbound peers only), LND gRPC `127.0.0.1:10029` (TLS + macaroons, signer on: families 21/99/42060/42068/42069) |
| Chain | public signet, Bitcoin Core 31.1 (`signet-bitcoind` container, unpruned, no txindex), RPC by cookie, ZMQ 39500/39501 |
| Loop | `loop`/`loopd` `v0.35.0-beta` (`91ab84a`, latest release tag), plain `go build` (Go 1.26), `--network=signet`, loopdir `~/signet/loopd`, RPC `127.0.0.1:11030`, REST `127.0.0.1:8091`, our admin macaroon |
| Loop server | `signet.swap.lightning.today:11010` (loopd's signet default); terms out/in 250,000-120,000,000 sat |
| Server's Lightning node | `022b9e44b3a8093d9512b61f5f83a72d5634201efc49718efd34f2ee851b3afa8e@50.112.25.211:9735` (the L402 invoice's payee); accepts only **public** channels of at least **1,000,000 sat** |

The node reads the bitcoind cookie at start (`start-daemon.sh` exports it as `NLTG_Bitcoin__RpcUser`/`RpcPassword`;
`BitcoinOptions` has no cookie-file setting), so restart the node after a bitcoind restart. Funding came from signet
faucets and the owner (1,851,385 sat over 10 deposits, blocks 325252-325257); the public faucets sit behind Cloudflare
challenges, so a human funded the node.

Peers: the Loop server's node, `024e679c...` (lnd1.staging.blink.sv), `03adf6ef...` and `03f229f6...` (Zap signet LNDs)
and `0288fa27...`. Many nodes in mempool.space's signet graph are unreachable.

## Results

| Step | Result | Evidence |
|---|---|---|
| loopd startup | **works** | lndclient sees `v0.21.4-beta`, build tags `signrpc,walletrpc,chainrpc,invoicesrpc`, "lnd is now fully synced", `RegisterBlockEpochNtfn` returned a height, `macaroons.db` created through the signer; "Connected to lnd node 'nltg-signet-loop'" |
| `loop terms`, `loop getinfo`, `loop quote out` | **work** | out/in 250,000-120,000,000 sat; quote out 250,000: 3,151 sat fee |
| L402 token | **works** once a channel exists | 1 sat paid over our routerrpc, payment hash `6e126d37c9b0299a4b7fb61e0142eaf3a89df7a35a95210fe7d32e9472fda5a4`; before the channel: `NO_ROUTE` |
| Channel | **works** | 1,200,000 sat public anchors channel to the server's node, funding `febe2e95...8dec:1` (block 325261), scid `325261x51x1`, announced at 6 confirmations. A 600,000 sat private open was refused by the peer (minimum 0.01 BTC, no private channels) |
| `loop static new` | **works** | `tb1p8809m7nu7qrzf5duar5n0l0d84wfu7a6764meg0k8cyahaqzplfqax9dcc`, expiry 1500 blocks; "Imported static address taproot script to lnd wallet" (our ImportTapscript) |
| Static deposit | **works** | 260,000 sat from our `withdraw`, tx `83db4aaa8db93acd5843f5d0bf5922437763454218ca4da51400ef9ea1a6022a:0` (block 325264): `DEPOSITED` in `loop static listdeposits`; our ordinary wallet balance excludes it |
| Loop Out 300,000 | **SUCCESS** | swap `5a02cb187b09acb3b40a22dfafe880792579281deead82519f5e1f924f05f757`; prepay 30,000 sat and swap payment 273,048 sat over our router; server HTLC `07c0059c...1ab0` (325268) seen by our ChainNotifier; cooperative MuSig2 sweep `a70dd6df571921b59569b713389a34f4939596c444252f32dc1a85a6c3f3a301` (325269) published through our PublishTransaction; cost server 3,048, on-chain 113 sat |
| Static Loop In, first try (`57dc482e`) | **failed: NLightning bug NL-1233** | the server's probe HTLC (260,000,000 msat, random hash, no MPP record) was failed with `invalid_onion_payload`; LND counts only `incorrect_or_unknown_payment_details` as the destination reached, so loopd logged "Server probe error: target unreachable" and the server answered "loop in failed" |
| Classic Loop In 250,000 | **SUCCESS** | swap `d0892d48cf9b2bd112a874b6b9b1eb82d1e9242668e6fb8351bd40c81d1a72c8`; its probe goes to a real hold invoice with a payment_secret ("Server probe successful", answered 0x400F, invoice canceled); our wallet published the HTLC `bd9e32ec0ea25b79bd03e989154f9fb3f0430cdd5b4a5d22f5e3e831ced27e47` (WalletKit SendOutputs, 144 sat fee, block 325270); the server paid our 249,482 sat invoice; server sweep `d77716af...445b` (325274); cost server 518, on-chain 144 sat. It survived a node and loopd restart in `InvoiceSettled` (20:47, the NL-1233 deploy): loopd resumed and finished |
| Static Loop In, after the fix (`91759661`) | **SUCCEEDED** | swap `c55f5633ff3a19b30186f88185c4ff02f806515703bac9417fe00a2bb5029368`, deposit `83db4aaa...:0` `LOOPED_IN`; the server's two probes (HTLCs 3 and 4) answered `IncorrectOrUnknownPaymentDetails`; three `MuSig2Sign` sessions in our signer; the server paid our 259,932 sat invoice (HTLC 5) 1.7 s after the request; cost server 68 sat. The server swept the deposit cooperatively in `b6a5b2a089386cde9e5009a1fcaceb52463f21f47079f52e388658a0cdaec211` (block 325276) |
| Cooperative static withdrawal | **works** | `loop static withdraw --utxo fe528e22...:0` (deposit 2, 260,000 sat, block 325269, sent by the orchestrator from our wallet): our `SignerService` ran MuSig2Sign session `A4A742E7...` and published `f193b713ab4a748c23b78eade76ea417fe1f781413ade524ade8ae6df1463549` (key path, 112 sat fee, no wallet input) to a fresh wallet address `tb1p7r4fln0r7jkp0aq5r7gv0czn75wwzeuh9j52hctskxpknevllw9qk4qzh2` from our NewAddress; confirmed at 325276 and seen by our wallet ("Deposit detected: 0.00259888"; `walletbalance` Unconfirmed 259,888 until our 4-confirmation rule, Confirmed from 325279: 1,091,765 sat, nothing unconfirmed). A second `static withdraw` of the same deposit, issued by the orchestrator three seconds later, built `64f131df...`, which bitcoind refused as a replacement (`insufficient fee, rejecting replacement`); `f193b713...` is the one that confirmed. At three confirmations (325278, loopd's `MinConfs` through our ChainNotifier) the deposit is `WITHDRAWN` and `loop static listwithdrawals` shows `f193b713...`, 259,888 sat withdrawn |

The first two static attempts on the fixed build (20:54, 20:55, one per deposit) were refused at once without a probe
HTLC; ten minutes later the same request went through. The server evidently remembered the failed probe for a while.

**Inbound lesson.** A static (and classic) Loop In needs the server's side of our channel to hold at least the swap
amount above its channel reserve (1 % of the capacity, 12,000 sat here): the classic Loop In paid us 249,482 sat and
left the server only 53,567 sat, so a 260,000 sat static Loop In could not be paid until the orchestrator's second Loop
Out (`9e8efbdf...`, 300,000 sat, sweep `85e7b6cc...` at 325275) moved liquidity back to the server's side. Plan one
Loop Out (or other inbound) per Loop In of the same size on a single channel.

### NL-1233 (fixed in `91759661`)

`HopPayloadValidator` refused a final, non-blinded, non-keysend payload without `payment_data` as
`invalid_onion_payload`. BOLT 4's failure rules make an unknown `payment_hash` and a missing required `payment_secret`
`incorrect_or_unknown_payment_details`, and LND's probes (random hash, no MPP record), such as the Loop server's static
loop-in probe, count only that error as reaching the destination. The validator now leaves the payload to
`FinalHopProcessor`, which already refuses it with 0x400F.

### Wallet observations

- **P2TR sweeps "Unconfirmed" for a while: our 4-confirmation rule, not an address-type bug.** `walletbalance` counts
  an output as Confirmed at four confirmations, for every address type: `UtxoMemoryRepository.GetConfirmedBalance` takes
  `BlockHeight + 3 <= tip` (`src/NLightning.Infrastructure.Repositories/Memory/UtxoMemoryRepository.cs:42-54`). At
  325277 the 559,775 sat Unconfirmed were the orchestrator's Loop Out sweep `85e7b6cc...` (299,887 sat to
  `tb1pzmax...`, block 325275) and the withdrawal `f193b713...` (259,888 sat, block 325276), not `a70dd6df...` (block
  325269, already Confirmed); they moved to Confirmed at 325278 and 325279. Outputs paid to addresses handed out through
  the LND `NewAddress`/WalletKit path are tracked like any other wallet address.
- **Finding (LND parity, low): `WalletBalance` over LND gRPC uses the same four confirmations.** `LightningService.Info.cs:93-94`
  calls `GetConfirmedBalance`/`GetUnconfirmedBalance`, so an LND client sees an output as unconfirmed for three blocks
  after LND (btcwallet) would call it confirmed (one confirmation). Proposed fix: compute the gRPC
  `confirmed_balance`/`unconfirmed_balance` (and `account_balance`) with LND's one-confirmation split, leaving the
  node's own `walletbalance` and the coin-selection/anchors-reserve depth unchanged; add a unit test with an output at
  1 and 3 confirmations.
- **`f193b713...` "rebroadcast" after it was mined: loopd's calls, not our node.** `WalletPsbtService.PublishTransactionAsync`
  sends a transaction without a wallet input once and saves no `BroadcastTransactions` row
  (`src/NLightning.Infrastructure.Bitcoin/Wallet/WalletPsbtService.cs:428-440`), so the chain monitor never rebroadcasts
  or books it. Each broadcast at 21:17:29, 21:29:29 and 21:34:38 is loopd calling `PublishTransaction` again: its
  withdrawal manager republishes on every block until `MinConfs = 3` (`staticaddr/withdraw/manager.go:59`; the loopd log
  shows "Publishing deposit withdrawal with txid f193b713..." at the same instants). bitcoind's "outputs already in utxo
  set" is mapped to success (`MapRefusal`, lines 531-535), as in LND. Cosmetic follow-up: log an already-known
  transaction as such instead of "Published transaction ... (no wallet input)".
- **loopd `WITHDRAWING` after the withdrawal was mined.** loopd waits for three confirmations of the withdrawal through
  our ChainNotifier `RegisterConfirmationsNtfn`; it reported `WITHDRAWN` at 325278, as designed.

## Not covered

- The CSV timeout sweep of a static deposit was not run on signet (the expiry is 1,500 blocks; regtest proof in
  `LOOP_GRPC_PLAN.md`).
- A baked least-privilege Loop macaroon was not used; loopd ran with the admin macaroon.

## Operating the trial setup

```bash
~/.nltg/signet-loop/cli.sh info                               # node (IPC client wrapper)
~/signet/loopd/loop.sh getinfo                                # loop CLI wrapper
~/signet/loopd/loop.sh stop                                   # stop loopd
~/.nltg/signet-loop/cli.sh shutdown                           # stop the node (refused while HTLCs are in flight)
~/.nltg/signet-loop/start-daemon.sh && ~/signet/loopd/start-loopd.sh   # start again: node first, then loopd
```

Logs: `~/.nltg/signet-loop/daemon.out` (node), `~/signet/loopd/loopd.out` (loopd). Keep `~/signet/loopd` (the L402
token and Loop database) while any deposit is unspent: they are needed to spend or recover it.
