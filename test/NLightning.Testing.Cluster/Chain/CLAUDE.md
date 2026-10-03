# Chain/ and Nodes/BitcoinCore/ — bitcoind and the chain helpers (test harness spike)

Plan: `docs/agents/TEST_HARNESS_PLAN.md` R7 (`BitcoinCore`) and R10 (chain helpers). Everything here is built on the
scaffold's seams (`TestRun.DeployAsync`, `NodeWorkload`, `KubeNodeHandle`); nothing replaces them.

## Nodes/BitcoinCore/

- `BitcoinCoreOptions` (defaults as the Docker fixtures: user/password `nltg`, `-txindex`, `-fallbackfee=0.0002`, a
  `miner` wallet; `Image` = `ImageVersions.BitcoinCore` 29.0 or `BitcoinCore31`; `AddNodes` for followers; `ExtraArgs`)
  and `BitcoinCorePorts` (fixed in-pod ports: RPC 18443, P2P 18444, ZMQ raw block 28332, raw tx 28333, hash block 28334).
- `BitcoinCoreWorkload.Build(options)`: StatefulSet + headless Service + 2 GiB PVC at `/home/bitcoin/.bitcoin`, every port
  on the Service, readiness = `bitcoin-cli getblockchaininfo` answers (out of warmup, wallet loaded), grace 30 s. The
  images' entrypoint runs bitcoind as user `bitcoin` when the first argument is an option, so `Command` stays null.
- `BitcoinCoreNode.DeployAsync(run, options, readyTimeout, ct)` deploys, waits, and creates the wallet with
  `load_on_startup` (it comes back after a restart). Addresses for other pods: `ClusterRpcUrl` (`http://miner:18443`),
  `ClusterP2pAddress`, `ClusterZmqRawBlock`/`RawTx`/`HashBlock` (the bare alias resolves inside the run's namespace).
  `ConnectToAsync(peer)` peers two bitcoinds (`addnode add` by alias + `onetry` by pod IP every 2 s until connected: a
  name looked up before the peer's Service had an endpoint can stay unresolved for a minute).
- RPC from the test process (`RpcRoute`): `ConnectRpcAsync(RpcRoute.Auto)` picks Service DNS in-cluster, the pod IP when
  it answers a TCP connect within 8 s (OrbStack; once a just-ready pod's IP took longer than a single 3 s probe), else `Exec` (`bitcoin-cli` in the pod, works anywhere with exec rights, one exec
  per call). `CreateRpc(route[, wallet])` for an explicit route; `CreateNBitcoinClient(route)` for code built on NBitcoin
  (the in-process node's `RegtestBitcoinEndpoint`; its host is fixed, rebuild it after a restart). `ProbeHostRouteAsync`
  reports what the host reaches.
- `Rpc/`: `IBitcoinCoreRpc` (typed calls; hashes/txids display hex, amounts sat, fee rates sat/vB, `CallAsync` for the
  rest) → `BitcoinCoreRpcClient` over an `IBitcoinRpcTransport`: `HttpRpcTransport` (NBitcoin `RPCClient`, named args,
  host re-read on every call so a new pod IP after a restart is followed) or `ExecCliRpcTransport` (`BitcoinCli`:
  `-named`, strings raw, everything else JSON; output and `error code:` parsing). Errors are `BitcoinRpcException`
  (`Code` null = no RPC answer).

## Chain/

- `RegtestChain(rpc)`: `MineAsync` (one cached wallet address), `WaitAllAtTipAsync(followers)` / `MineAndWaitAsync` (the
  tip is re-read each poll; the timeout lists every follower's state), `SendAsync`, `WaitForMempoolAsync`,
  `WaitForConfirmationAsync`, `MineUntilConfirmedAsync`, `ReorgAsync(depth, ReorgOptions)` / `ReconsiderAsync`,
  `SetWalletFeeRateAsync` (`settxfee`; Bitcoin Core 31 removed it: `NotSupportedException`, pin the rate per send
  instead), `SeedFeeEstimatesAsync(satPerVb)`.
- `ChainFollower`: one per node that follows the chain, with its own predicate. `FromReport` (height, optional hash,
  synced flag: LND `synced_to_chain`), `AtHeight`, `Bitcoind(name, rpc)` (height and hash), or the constructor for
  anything else (the in-process node's `BlockchainMonitor.LastProcessedBlockHeight`). An exception from a probe counts as
  "not yet" (a restarting node), a cancellation does not.
- Reorgs: `ReorgAsync` invalidates the first block above the fork and mines `depth + 1` blocks (`NewBlocks`), each to a
  fresh address so no block can repeat an invalidated one; `Transactions = Drop` mines empty blocks (`generateblock`),
  `FirstBlockTransactions` puts exactly those in the first new block. When another valid branch above the fork takes
  over (one an earlier reorg left and `ReconsiderAsync` brought back), its first block is invalidated too
  (`ChainReorg.OtherInvalidatedHashes`), so the new branch always grows on the fork block.
- Gotcha: bitcoind's wallet sets a send's lock time to the tip height at the time (anti fee sniping). A fork below that
  height drops the transaction from the mempool instead of keeping it (not final there); so does a coinbase spend that
  is immature at the fork. Keep the fork at or above the send height when the test needs the transaction back.
- Fee seeding: a fan-out to confirmed outputs, then `Blocks` (8) x `TransactionsPerBlock` (5) self-sends at the rate
  (the estimator ignores transactions with unconfirmed parents); 12 sat/vB gives exactly 12 at target 2. The estimator
  decays slowly: seed one rate per chain. Needs a mature coinbase (mine 101 first).

## Live evidence (OrbStack, 2026-10-02)

`BitcoinCoreClusterTests` (`[Trait("Category","Cluster")]`, explicit; bitcoind 29.0 and 31.1): ready 6.5-12.5 s after
deploy (PVC, image cached), 101 blocks in 1-7 s (machine shared with Docker suites). OrbStack routes pod IPs to the Mac
(TCP connect 0-2 ms, tip read 1 ms) and resolves `*.svc.cluster.local` there too; in 1 of 6 deployments the fresh pod
was not reachable for the first 3 s (hence `Auto`'s 8 s window; exec covered it). The ZMQ raw block feed answers a ZMTP
greeting by pod IP. A restart (2-4.6 s) moves the pod IP; `HttpRpcTransport` follows it, chain and wallet kept.
Miner + follower: deployed and peered in 8-18 s, mine 101 + both followers at the tip 1.2-13.7 s, a reorg seen by the
follower in 0.2-4.5 s, `settxfee` 4 sat/vB paid exactly 4.00 (29.0; 31.1 answers -32601), seeding 12 sat/vB 1.5-17 s.
