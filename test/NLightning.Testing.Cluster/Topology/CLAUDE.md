# Topology/ and Nodes/Cln/: declarative topologies and the CLN node (spike)

Plan: `docs/agents/TEST_HARNESS_PLAN.md` R8 (declarative topology), R9 (facade), R11 (lifecycle).

## Topology/

- `TopologyBuilder`: `AddBitcoinCore(name)`, `AddLnd(name)`, `AddCln(name)`, `AddEclair(name)` (phase 4; the chain
  on `ImageVersions.BitcoinCore31`), `AddNode(name, kind)` (each with an optional `storage:`), `FundWallet(node, sat)`,
  `AddChannel(from, to, capacitySat, pushMsat, announce)`, `UseDeployer(ILightningNodeDeployer)` (one per `NodeKind`;
  CLN, LND and Eclair are registered by default),
  `UseChain(ChainFactory, ChainEndpointFactory?)` (default `BitcoinCoreTopologyChain.DeployAsync` and `.EndpointFor`;
  without an endpoint the nodes wait for the chain), `Storage` (nodes without their own; null = `NLTG_NODE_STORAGE`,
  else PVC), `DeployNodesWithChain` (default true), `ChainAddressWait`, `Log`, `ReadyTimeout`, `StepTimeout`.
  `Build()` returns the validated `TopologySpec` (every node's storage resolved); `BuildAsync(run, ct)` deploys it.
- `TopologySpec.Validate()` lists every problem: DNS-1123 names, exactly one `BitcoinCore`, fundings and channels
  between Lightning nodes only, no self channel, push within the capacity, and each funder funded with more than it
  opens.
- `TopologyDeployer` runs these steps, each logged with its time (and kept in `TestTopology.Timings`: `chain`,
  `nodes`, `fundings`, `channels`):
  1. The chain (a mature `miner` wallet, `load_on_startup`) and, **in the same wave**, every Lightning node whose
     deployer says `DeploysWithChain` (phase 2): it gets `TopologyDeployContext.ChainEndpoint` at once and the started
     `Chain` only through `WaitForChainAsync`; its pod waits for bitcoind in the chain's startup wait (init
     container). The other deployers run once the chain is ready, as before. The first failure cancels the rest of
     the wave and the first real error is thrown. When the chain node is on `emptyDir` storage the wave first waits
     (up to `ChainAddressWait`, default 5 s) until the chain's name resolves: its pod has an IP within about a second,
     and a node that looked the name up earlier would see CoreDNS's cached miss for 5 s. On a PVC it does not wait
     (the chain's pod has an IP only after its claim, 6-15 s; the nodes' claims are provisioned meanwhile), so a node
     may lose up to 5 s to that cache.
  2. Every Lightning node ready (those deployed after the chain, in parallel).
  3. Wait until every node is at the tip.
  4. The fundings: sent, 6 blocks, then each wallet's confirmed balance polled.
  5. The channels: connect with retries, then open, one at a time.
  6. 6 blocks, then wait until each channel is active on **both** ends.
- `TestTopology` (the built topology):
  - `Node(name)` / `Node<T>(name)`, `Chain`, `Channels` (with their SCIDs);
  - `MineAndSyncAsync`, `WaitAllAtTipAsync`, `WaitChannelsActiveAsync`, `ReconnectChannelsAsync`.
- `ClusterTopologyFixture` / `ClusterTopologyFixture<TDefinition>` (`IClusterTopologyDefinition`: static `Suite`
  and `Configure(builder)`): an xunit collection (or class) fixture that starts its run and builds the topology once,
  when the first test that runs awaits `EnsureStartedAsync()` (each test class implements `IAsyncLifetime` and calls it
  in `InitializeAsync`; a Docker-era fixture's cluster backend calls `EnsureStartedAsync(ct)`), and disposes the run
  after its last (`OnBuiltAsync` for more setup, `StartLog`, `StartTime`). xunit's own `InitializeAsync` builds
  nothing: xunit creates the fixtures of Explicit tests that will not run too, which would start a namespace in every
  `FullyQualifiedName!~Docker` run and fail those tests where no cluster is reachable (NL-800). Nothing is reset between tests. Isolation expectations (the XML docs have them in full): collections
  are isolated (each its own namespace and slot); tests of one collection run one after another and see what the
  earlier ones left (assert deltas, unique invoice labels, own channels when a fresh one is needed); a test that
  restarts, kills, crashes, pauses or partitions a node restores the topology before it returns or runs in its own
  collection; ephemeral nodes cannot be restarted at all.
- Seams for the other lanes:
  - `ITopologyLightningNode` (`ILightningTestPeer` plus block height and confirmed balance);
  - `ILightningNodeDeployer` (`DeploysWithChain`, default false) and `TopologyDeployContext` (`Run`,
    `ChainEndpoint`, `Chain` once ready, `IsChainReady`, `WaitForChainAsync`, `ReadyTimeout`, `Log`);
  - `ITopologyChainEndpoint` (RPC host/port/credentials, ZMQ ports (`ZmqHashBlockPort` null by default, set by the
    Bitcoin Core chain for Eclair), `CreateStartupWait()`) and `ITopologyChain` (the
    endpoint plus the node, mine, send, tip). `BitcoinCoreTopologyChain` is the
    implementation: the chain lane's `BitcoinCoreNode` + `RegtestChain` (its `Chain` property has the reorgs, fee
    seeding and tx waits). The CLN lane's stopgap `TopologyBitcoind` and the LND lane's `LndTopologyChain` were
    replaced by it at the integration.
- `StableNodeAddress`: a ClusterIP Service `<node>-p2p` in front of a node's p2p port. Peers dial this, not the
  headless name. Why:
  - A recreated pod gets another IP, and CLN stores the IP it resolved and redials it. A SYN to a vanished pod IP gets
    no answer, so CLN's redial hangs for minutes, and `connect` only adds the new address to that hung attempt.
  - The ClusterIP stays the same. kube-proxy refuses dials while the node has no ready endpoint and routes to the new
    pod once it is ready.
  - Deployers of other kinds should create one too (`StableNodeAddress.EnsureAsync`). `LndNodeDeployer` does not
    yet: LND's readiness probe includes `synced_to_chain`, which flaps on new blocks and would take the node out of a
    ClusterIP Service; LND peers redial by pod IP after a restart instead (`LndPairTopology.RestartAsync`).

## Nodes/Cln/

- `ClnNode.Workload`:
  - `elementsproject/lightningd` v26.06.8 by digest, with the `ClnFixture` flags (`--developer --dev-bitcoind-poll=1`
    `--ignore-fee-limits=false`) and bitcoind reached by its alias;
  - its data on a PVC at `/root/.lightning` (an `emptyDir` with `ClnNodeOptions.Storage = Ephemeral`);
  - the chain's startup wait as its init container (`ClnNodeOptions.StartupWait`, set by `ClnNodeDeployer`);
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

## Fixed at the integration: `KillAsync` overlapped two processes on one PVC

`INodeHandle.KillAsync` deleted the pod with grace 0, a **force delete**: the StatefulSet created the new pod while
the old container still ran on the same PVC, and CLN refused to start ("lightningd already running? Error locking
PID file"). It now deletes with a 1 s grace (`KubeNodeHandle.KillGracePeriodSeconds`): SIGTERM, SIGKILL after 1 s,
and the replacement only once the old container is gone. It is still not a crash: `ClnTestPeer.CrashAsync` (SIGKILL
to lightningd, no shared process namespace needed) or `FaultInjector.CrashAsync` (any node deployed
`WithProcessFaults()`) are.

## Live tests

They are in `test/NLightning.Testing.Cluster.Tests/Live/ClnTopologyTests.cs`, `Explicit`, with
`[Trait("Category","Cluster")]`:
- the pair builds and pays;
- crash and restart, which needs CLN to reconnect by itself;
- `NLTG_CLN_CONCURRENCY` pairs at once (default 3, at most 6).
