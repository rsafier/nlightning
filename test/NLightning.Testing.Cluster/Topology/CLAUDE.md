# Topology/ and Nodes/Cln/: declarative topologies and the CLN node (spike)

Plan: `docs/agents/TEST_HARNESS_PLAN.md` R8 (declarative topology), R9 (facade), R11 (lifecycle).

## Topology/

- `TopologyBuilder`: `AddBitcoinCore(name)`, `AddCln(name)`, `AddNode(name, kind)`, `FundWallet(node, sat)`,
  `AddChannel(from, to, capacitySat, pushMsat, announce)`, `UseDeployer(ILightningNodeDeployer)` (one per `NodeKind`;
  CLN is registered by default), `UseChain(ChainFactory)` (default `TopologyBitcoind.DeployAsync`), `Log`,
  `ReadyTimeout`, `StepTimeout`. `Build()` returns the validated `TopologySpec`; `BuildAsync(run, ct)` deploys it.
- `TopologySpec.Validate()` lists every problem: DNS-1123 names, exactly one `BitcoinCore`, fundings and channels
  between Lightning nodes only, no self channel, push within the capacity, and each funder funded with more than it
  opens.
- `TopologyDeployer` runs these steps, each logged with its time:
  1. The chain: a mature `miner` wallet, `load_on_startup`.
  2. Every Lightning node, deployed in parallel.
  3. Wait until every node is at the tip.
  4. The fundings: sent, 6 blocks, then each wallet's confirmed balance polled.
  5. The channels: connect with retries, then open, one at a time.
  6. 6 blocks, then wait until each channel is active on **both** ends.
- `TestTopology` (the built topology):
  - `Node(name)` / `Node<T>(name)`, `Chain`, `Channels` (with their SCIDs);
  - `MineAndSyncAsync`, `WaitAllAtTipAsync`, `WaitChannelsActiveAsync`, `ReconnectChannelsAsync`.
- Seams for the other lanes:
  - `ITopologyLightningNode` (`ILightningTestPeer` plus block height and confirmed balance);
  - `ILightningNodeDeployer`;
  - `ITopologyChain` (RPC endpoint, mine, send, tip). `TopologyBitcoind` is the spike's own stopgap; the chain lane's
    Bitcoin Core node replaces it through `UseChain`.
- `StableNodeAddress`: a ClusterIP Service `<node>-p2p` in front of a node's p2p port. Peers dial this, not the
  headless name. Why:
  - A recreated pod gets another IP, and CLN stores the IP it resolved and redials it. A SYN to a vanished pod IP gets
    no answer, so CLN's redial hangs for minutes, and `connect` only adds the new address to that hung attempt.
  - The ClusterIP stays the same. kube-proxy refuses dials while the node has no ready endpoint and routes to the new
    pod once it is ready.
  - Deployers of other kinds should create one too (`StableNodeAddress.EnsureAsync`).

## Nodes/Cln/

- `ClnNode.Workload`:
  - `elementsproject/lightningd` v26.06.8 by digest, with the `ClnFixture` flags (`--developer --dev-bitcoind-poll=1`
    `--ignore-fee-limits=false`) and bitcoind reached by its alias;
  - its data on a PVC at `/root/.lightning`;
  - readiness = not draining and `getinfo` answers. Chain sync is the topology's wait, so a new block never takes the
    node out of its Services.
- `preStop` = drain, then stop:
  - First it touches `/tmp/nltg-draining`, which fails the probe, and waits 5 s so kube-proxy drops the endpoint.
  - Then it runs `lightning-cli stop`.
  - Why the drain: without it, a peer's redial can land between the pod's network going away and kube-proxy's update.
    conntrack then pins that SYN to the dead pod IP, and the reconnect hangs for about 2 minutes.
  - Why `lightning-cli stop`: the image's PID 1 is a bash script that does not pass SIGTERM on to lightningd, so
    without it a graceful restart was a kill at the end of the grace period.
- `ClnRpc`: `lightning-cli -k` through a Kubernetes exec, the approach of the Docker suite's `ClnClient`. `CallAsync`
  throws `ClnRpcException` with CLN's code.
- `ClnTestPeer`: the facade (`xpay` for payments, `fundchannel`, `listpeerchannels`; a channel is active when it is
  `CHANNELD_NORMAL` and its peer is connected).
  - `Rpc` stays reachable for CLN-specific calls.
  - `CrashAsync` sends SIGKILL to lightningd, and the container restarts in the same pod (same IP, same PVC).
- `ClnNodeDeployer`: deploys the workload and its `StableNodeAddress`, and returns a `ClnTestPeer` that dials the
  stable name.

## Known problem in the scaffold (not fixed here)

`INodeHandle.KillAsync` deletes the pod with grace 0. That is a **force delete**:
- The StatefulSet creates the new pod while the old container still runs on the same PVC.
- CLN then refused to start ("lightningd already running? Error locking PID file").
- Two processes on one data directory risk corrupting it.

Use `ClnTestPeer.CrashAsync` for a CLN crash until the faults lane changes `KillAsync`. Options for that change: a
small non-zero grace, or a kill of the process inside the pod.

## Live tests

They are in `test/NLightning.Testing.Cluster.Tests/Live/ClnTopologyTests.cs`, `Explicit`, with
`[Trait("Category","Cluster")]`:
- the pair builds and pays;
- crash and restart, which needs CLN to reconnect by itself;
- `NLTG_CLN_CONCURRENCY` pairs at once (default 3, at most 6).
