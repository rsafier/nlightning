# Running nltg on Mutinynet (custom signet)

Mutinynet is a custom signet with ~30 s blocks, run by Mutiny (benthecarman). This page describes how to run the
NLightning daemon (`nltg`) against the local Mutinynet bitcoind in `~/mutinynet` (see `~/mutinynet/README.md` for the
node itself). It was written in ABCD wave 4 (lane W4-D). The live smoke test (open a channel to the faucet node, pay
both ways) belongs to a later wave, after the local bitcoind has synced.

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
- Faucet: https://faucet.mutinynet.com (on-chain coins to a `tb1` address, a channel from the faucet node to us, or a
  payment to our invoice); the API needs a GitHub-login JWT or an L402 token, see `~/mutinynet/README.md`.
- Explorer and API: https://mutinynet.com/api/ (`/api/blocks/tip/height`, `/api/v1/fees/recommended`).

Smoke test outline for the later wave: fund `getaddress` from the faucet, wait for the deposit, `connect` to the
faucet node, `openchannel` to it (our feerate is the estimate above; LND accepts 253 sat/kw), wait `MinimumDepth`
blocks (~1.5 min), create an invoice and have the faucet pay it, pay a faucet invoice, then `closechannel`.

## Known gaps

- Network resolution is not unified (NL-298): the `Wallet/` services (including `BlockchainMonitorService`, since the wave 4 integration 53accb1) use `NBitcoinNetworkResolver`; the builders, `LocalLightningSigner`, `SecureKeyManager`, `ShutdownScriptProvider` and `FallbackAddressTaggedField` resolve with `Network.GetNetwork(name)` (which knows `signet`) and throw otherwise; `ChannelFailureService` falls back to `Network.RegTest`; `ChannelManager` and `ChannelCloseCoordinator` load transactions with `Network.Main` (harmless for parsing).
- The daemon's `Node:Network` binding resolves through `BitcoinNetwork.Resolve` in `PostConfigure` since 960cf05, so an unknown name fails fast with an `ArgumentException`.
- DNS seeds are not used for signets (the template writes an empty `DnsSeedServers`); connect peers by hand.
