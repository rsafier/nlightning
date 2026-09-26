# Running nltg on Mutinynet (custom signet)

Mutinynet is a custom signet with ~30 s blocks, run by Mutiny (benthecarman). This page describes how to run the
NLightning daemon (`nltg`) against the local Mutinynet bitcoind in `~/mutinynet` (see `~/mutinynet/README.md` for the
node itself). It was written in ABCD wave 4 (lane W4-D); the live smoke test (open a channel to the faucet node, pay
both ways, restart, close cooperatively) passed in wave 5 (lane w5e), see [Live smoke test](#live-smoke-test-wave-5).

## How NLightning sees a custom signet

- Every signet has the same genesis block
  (`00000008819873e925422c1ff0f99f7cc9bbb232af63a077a480a3633bee1ef6`), so a custom signet has the Lightning
  parameters of plain signet: BOLT `chain_hash` = that hash in wire (reversed) byte order (`ChainConstants.Signet`),
  `tb1` bech32 addresses, `lntbs` BOLT 11 invoices. LND, CLN and Eclair on Mutinynet use the same values.
- The block challenge (`signetchallenge=512102f7...51ae`) matters only for validating blocks, and bitcoind does that.
  NLightning never needs it.
- So inside the node Mutinynet **is** `signet`: `NodeOptions.BitcoinNetwork` is `BitcoinNetwork.Signet` (even
  `new BitcoinNetwork("mutinynet")` gives `signet`), and the custom name only lives in `Node:CustomSignet:Name` and in
  the configuration directory name.
- `BitcoinNetwork.Resolve(name)` (Domain) maps a configured name to the network: built-in names to themselves, a
  registered custom signet to `Signet`, anything else throws `ArgumentException`. Mutinynet is registered by default
  (`NetworkConstants.Mutinynet`); other custom signets register through `Node:CustomSignet:Name` or
  `BitcoinNetwork.RegisterCustomSignet`.
- `NBitcoinNetworkResolver.ToNBitcoinNetwork()` (Infrastructure.Bitcoin) maps signet and every custom signet to
  NBitcoin's `Bitcoin.Instance.Signet` (same genesis, `tb` HRP, testnet key versions). An unknown network throws;
  there is no `Network.Main` fallback in the wallet and chain services any more.

## Start the daemon

```bash
cd ~/mutinynet && docker compose up -d && ./cli.sh getblockchaininfo   # bitcoind must be synced first
dotnet run --project src/NLightning.Daemon -- --network mutinynet
```

The first run writes `~/.nltg/mutinynet/appsettings.json` and stops at the key step if bitcoind is not reachable
(creating the key reads the block height for the wallet birthday). The file has `Node:Network` `signet` and
`Node:CustomSignet:Name` `mutinynet`; `--network mutinynet` resolves to `signet`, so the key file, chain hash and
invoices are signet's while the directory stays `~/.nltg/mutinynet` (separate from plain signet in
`~/.nltg/signet`). `--network signet` with `Node:CustomSignet:Name` set in `~/.nltg/signet/appsettings.json` works
too; any other custom signet name is accepted by `--network <name>` once `~/.nltg/<name>/appsettings.json` exists with
`Node:Network` `signet` and `Node:CustomSignet:Name` `<name>`. An unknown `--network` fails before anything is written.

Edit `~/.nltg/mutinynet/appsettings.json`:

```jsonc
"Bitcoin": {
  "RpcEndpoint": "http://127.0.0.1:38332",   // template writes http://localhost:38332
  "RpcUser": "<RPCUSER from ~/mutinynet/.env>",
  "RpcPassword": "<RPCPASSWORD from ~/mutinynet/.env>",
  "ZmqHost": "127.0.0.1",
  "ZmqBlockPort": 28332,                      // zmqpubrawblock
  "ZmqTxPort": 28333                          // zmqpubrawtx
},
"Database": { "RunMigrations": true, ... }    // first run only, creates the SQLite schema
```

The password can also come from the environment instead of the file: `NLTG_Bitcoin__RpcPassword`. The template also
writes `"EnableHtlcs": true` for signets (test coins); leave it or set it explicitly.

The CLI finds the daemon by the same directory name:

```bash
dotnet run --project src/NLightning.Client -- --network mutinynet info
dotnet run --project src/NLightning.Client -- --network mutinynet getaddress        # tb1q... / tb1p...
```

## Fee estimates

`FeeEstimation:Source` picks where the estimate comes from. Every source ends in sat/kw and never below
253 sat/kw (BOLT 3 floor); Mutinynet usually answers 1 sat/vB (250 sat/kw), so expect 253.

| Source | Settings | Notes |
|---|---|---|
| `Http` (template default on signets) | `Url`, `PreferredFeeRate` (JSON property), `RateUnit` (`sat/vB` default, `sat/kvB`, `sat/kw`, `BTC/kvB`), `Method`/`Body`/`ContentType` | Mutinynet: `https://mutinynet.com/api/v1/fees/recommended` (mempool.space API: `fastestFee`, `halfHourFee`, `hourFee`, `economyFee`, `minimumFee`, all sat/vB). Plain signet: `https://mempool.space/signet/api/v1/fees/recommended`. |
| `Bitcoind` | `ConfirmationTarget` (default 6), `EstimateMode` (`CONSERVATIVE`/`ECONOMICAL`) | `estimatesmartfee` over the `Bitcoin` RPC settings. A fresh node has no estimate for a while; the service then logs a warning and uses `FallbackFeeRatePerKw`. |
| `Fixed` | `FixedFeeRatePerKw` (default 2500, at least 253) | Template default on regtest. |

Whatever the source, until the first estimate arrives (and while every fetch fails) the rate is
`FeeEstimation:FallbackFeeRatePerKw` (default 2500 sat/kw, at least 253), never 0; once there was an estimate, a failed
refresh keeps it. A request that times out is logged as such.

`FeeEstimation:RateMultiplier` is ignored since NL-288 (it turned sat/vB into sat/kvB, 4x too high) and logs a warning
when set; delete it from old configuration files.

## Faucet and peers

- Faucet node (LND): `02465ed5be53d04fde66c9418ff14a5f2267723810176c9212b722e542dc1afb1b@45.79.52.207:9735`.
- Faucet: https://faucet.mutinynet.com. Two endpoints need **no login** (IP rate-limited, 60 a day) and are all a
  smoke test needs (`scripts/mutinynet/faucet.sh`):
  - `POST /api/bolt11 {"amount_sats":N}` returns `{"bolt11":"lntbs..."}`, an invoice of the faucet LND node itself
    (our peer), so we pay it directly over the channel.
  - LNURL-withdraw: `GET /api/lnurlw` returns a `k1` (max 1,000,000 sat), then
    `GET /api/lnurlw/callback?k1=<k1>&pr=<our bolt11>` makes the faucet pay our invoice (it needs an amount and
    enough inbound liquidity on our side: push at open, or pay first). The call answers `{"status":"OK"}` after the
    payment succeeded.
  - On-chain coins (`/api/onchain`), a channel from the faucet to us (`/api/channel`) and paying a lightning address
    (`/api/lightning`) need a GitHub-login JWT or an L402 token (see `~/mutinynet/README.md`); the smoke test does not
    use them (on-chain coins came from the local `nltg-funding` wallet).
- Explorer and API: https://mutinynet.com/api/ (`/api/blocks/tip/height`, `/api/v1/fees/recommended`).

## Scripts (`scripts/mutinynet/`)

All of them read `env.sh` (`MUTINYNET_DIR`, `NLTG_NETWORK` default `mutinynet`, `NLTG_BUILD` default `Release`,
`NLTG_FRAMEWORK` default `net10.0`, `FAUCET_URL`, `FAUCET_NODE`) and run the built binaries, not `dotnet run`.

| Script | What |
|---|---|
| `build.sh` | builds `NLightning.Daemon` and `NLightning.Client` |
| `start-daemon.sh` | runs the daemon from `~/.nltg/<network>` (so the relative SQLite and log paths land there, NL-306) in the foreground with `--password-file ~/.nltg/<network>/.password` (created with a random password, mode 600, if missing) and appends the output to `~/.nltg/<network>/daemon.out`. The first run writes the template and fails at the key step (bitcoind 401) |
| `configure.sh` | writes the local bitcoind RPC/ZMQ settings (from `~/mutinynet/.env`) and `Database:RunMigrations=true` into the template (needs `jq`) |
| `cli.sh` | the CLI with `--network mutinynet` |
| `faucet.sh invoice <sats>` / `faucet.sh withdraw <bolt11>` | the two no-login faucet calls above |

## Live smoke test (wave 5)

Run on 2026-09-26 (UTC 06:07-06:30) with `wip/fafo` @ `30fc0d5` plus the lane commits (NL-301 push over IPC,
NL-302 UTXO wallet addresses at startup), Release build, SQLite, against the local synced node (tip 3456825 at the
start). Node id `030f7defc57e05273c109870dbc15ec0f1ade96872852a06247c42c75bfac2495a`.

```bash
scripts/mutinynet/build.sh
scripts/mutinynet/start-daemon.sh          # first run: writes the template, fails at the key step (401)
scripts/mutinynet/configure.sh
scripts/mutinynet/start-daemon.sh &        # creates the key, migrates, syncs from the birthday height
scripts/mutinynet/cli.sh getaddress p2wpkh
~/mutinynet/cli.sh -rpcwallet=nltg-funding sendtoaddress <address> 0.009
scripts/mutinynet/cli.sh connect 02465ed5be53d04fde66c9418ff14a5f2267723810176c9212b722e542dc1afb1b@45.79.52.207:9735
scripts/mutinynet/cli.sh openchannel 02465ed5...1b@45.79.52.207:9735 200000 50000     # push 50,000 sat
scripts/mutinynet/cli.sh payinvoice "$(scripts/mutinynet/faucet.sh invoice 5000)"
scripts/mutinynet/cli.sh createinvoice 10000000 "w5e mutinynet smoke receive"
scripts/mutinynet/faucet.sh withdraw <bolt11>
# restart the daemon, wait for "Reestablished: Yes", pay again
scripts/mutinynet/cli.sh closechannel <channel_id> 0 120
```

| Step | Result |
|---|---|
| Faucet funding of `nltg-funding` (earlier) | `e228b23b7bb7bdd4a1f7313c0421ce9ceaf180069295a9fdbfe6deb7a45d3294` (1,000,000 sat, block 3456568) |
| Deposit to the nltg wallet (`tb1qf2fzcfvzempdv97tvqlj5qpugk0vxl4jxd75xt`, 900,000 sat) | `79301b2a986410fbef2b87f01d7edf04fa7130209536a94bee571670e3346d29` (block 3456831) |
| First open attempt after a daemon restart | failed before broadcast: the restored UTXO had no wallet address, the signer skipped the input and the open ended in a NullReferenceException (NL-302, fixed in this lane; nothing was broadcast, the UTXO was released) |
| Funding tx (200,000 sat, push 50,000 sat, 1 input, 154 sat fee at 1 sat/vB) | `17eb2731122d05e4bbe9d74c6edad915fdc455e34d2e82c653c046efc9dd37dc`, output 0, block 3456838, SCID `3456838x12x0` |
| Channel id | `dc37ddc9ef46c053c6822e4de355c4fd15d9da6e4cd7e9bbe4052d123127eb17` (Open 3 min after broadcast; `channel_ready` both ways) |
| Pay a faucet invoice, 5,000 sat | payment hash `ca3d3ac830e6807a27a8d0c3321750a3ac03c3f70c659b20a3beda8d5c287ccd`, preimage `1ed58f1702e3a6c6a8ce666af8a7f9bd3ce8c38784b2475935cbda7aee9ed0de`, Succeeded in < 1 s, fee 0 (direct) |
| Receive 10,000 sat (LNURL-withdraw) | payment hash `f2acea8a1f718824256f6682dae8060a855a3ea1b6632747bb74cfa7591dbcec`, invoice Settled, 10,000,000 msat received |
| Daemon restart | `channel_reestablish` with LND, `Reestablished: Yes`, balances unchanged (local 155,000 / remote 45,000 sat, commitment 5/5) |
| Pay a faucet invoice after the restart, 1,000 sat | payment hash `85e2fb58ddbdb24d7b794cb833e5472daff84f584c08b09adfd326691f60901c`, preimage `e6e4c88d613704dedf766fed895729d9485ff870c6fc15724424c41229c905e9`, Succeeded |
| Cooperative close (we initiate, feerate from the estimator) | closing tx `ac6b8f9aa3b0853e3f28b924425be8167d41100ed5e834eddf926c4159bb5465`, 169 vB, 180 sat fee (ours, as funder); outputs 46,000 sat to the faucet, 153,820 sat to us; block 3456859; channel `Closed`, final commitment 7/7 |
| Wallet after the close | 853,666 sat confirmed (699,846 funding change + 153,820 close output) |

Observations from the run (ledger items, not fixed here):

- NL-303: `info` prints the best block hash, and `openchannel`/`listchannels` print the funding txid, in internal
  byte order (`bf2416f6...0000` for block `00000284...24bf`; `dc37ddc9...eb17:0` for funding tx `17eb2731...37dc`). The
  closing txid is printed in the usual order. Display only.
- NL-304: `LocalLightningSigner.SignFundingTransaction` logs a warning for an input it cannot sign (no UTXO or no wallet
  address), leaves it null and then fails with a NullReferenceException; it should throw a `SignerException` naming the
  input. Fixed in the w5e review: the signer now throws `SignerException` naming the input and its outpoint
  (`test/NLightning.Integration.Tests/Persistence/FundingSigningAfterReloadTests` reloads a UTXO from SQLite, locks
  it and signs the funding transaction, with and without its wallet address).
- NL-305: the deposit address `tb1qf2fz...5xt` was still "unused" after it received the deposit, so the cooperative
  close paid our output to it again (address reuse); `getaddress` moved on only after the close output arrived.
- NL-306: the template's relative paths (`Database:ConnectionString` `Data Source=nltg.db`, Serilog `logs/log-.txt`)
  resolve against the daemon's working directory, not `~/.nltg/<network>`: the run above first wrote its database
  and logs into the repository checkout it was started from (moved to `~/.nltg/mutinynet` afterwards; the node
  reloaded its closed channel and wallet from there). `start-daemon.sh` now `cd`s into the configuration directory;
  the daemon should anchor relative paths itself.

## Known gaps

- Network resolution is not unified (NL-298): the `Wallet/` services (including `BlockchainMonitorService`, since the wave 4 integration 53accb1) use `NBitcoinNetworkResolver`; the builders, `LocalLightningSigner`, `SecureKeyManager`, `ShutdownScriptProvider` and `FallbackAddressTaggedField` resolve with `Network.GetNetwork(name)` (which knows `signet`) and throw otherwise; `ChannelFailureService` falls back to `Network.RegTest`; `ChannelManager` and `ChannelCloseCoordinator` load transactions with `Network.Main` (harmless for parsing).
- The daemon's `Node:Network` binding resolves through `BitcoinNetwork.Resolve` in `PostConfigure` since 960cf05, so an unknown name fails fast with an `ArgumentException`.
- DNS seeds are not used for signets (the template writes an empty `DnsSeedServers`); connect peers by hand.
