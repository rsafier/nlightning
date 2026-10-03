# Cluster/: our in-process node in a cluster topology (test harness phase 2, lane A)

Plan: `docs/agents/TEST_HARNESS_PLAN.md` (phase 2; "Spike check 1 record" for the reachability rules). The harness
library `test/NLightning.Testing.Cluster` references no NLightning project, so the glue that puts our node
(`Docker/Utils/NLightningTestNode`, built from the daemon's `AddNltgNodeServices`) into its topologies lives here, on
the library's existing seams: `NodeKind.NLightning`, `ILightningNodeDeployer`, `ITopologyLightningNode`.

## Pieces

- `InProcessNodeDeployer` (`ILightningNodeDeployer` for `NodeKind.NLightning`, `IAsyncDisposable`): builds and starts
  one `NLightningTestNode` per topology node (pooled port, fresh key, own SQLite file unless `Database` says
  otherwise). Hooks: `ConfigureNodeOptions(alias, NodeOptions)` (applied last on every start, e.g.
  `o.Features.BeyondSegwitShutdown`), `ConfigureNode(node)` (before the first start: `ExtraConfiguration`,
  `ConfigureServices`), `DefaultOpenMode`, `PodFacingHost`. Topology `ExtraArgs` of the node are `Section:Key=value`
  settings. **Dispose it after the topology and before the run** (`await using` declared after the run), so our node
  stops before the namespace goes.
- `InProcessTopologyExtensions`: `builder.AddNLightning("nltg", "Node:Alias=x")`, `builder.UseInProcessNodes(deployer)`,
  `topology.InProcessNode("nltg")`.
- `InProcessNode` (the `ITopologyLightningNode` adapter; every call goes through the daemon's client command handlers):
  connect/disconnect, new address, open (`InProcessOpenMode` `V1` (default, as `NLightningTestNode.OpenChannelAsync`),
  `DualFund`, `Auto`; returns once the funding is in bitcoind's mempool), list channels (active = Open, peer
  connected, reestablished), invoices, pay (`payinvoice`, then `listpayments` while in flight), block height (the chain
  monitor's), confirmed balance, `FindChannelAsync(fundingTxId)`, `CloseChannelAsync(channelId)` (cooperative, returns
  the closing txid), `RestartAsync` (stop + start on the same key, database and port), `TestNode` for the rest.
- `InProcessTopologyFixture` (a `ClusterTopologyFixture` of the library: one warm topology per xunit collection, built
  once, nothing reset, the isolation rules in the base class's XML docs): owns the `InProcessNodeDeployer`
  (`CreateDeployer()` for its hooks), registers it after `ConfigureTopology(builder)` and disposes it in
  `OnStoppingAsync`, i.e. after the topology's adapters and before the namespace is deleted. `fixture.InProcessNode(name)`
  and `fixture.Node<T>(name)` read the nodes. Use it for every ported suite with a shared topology (the CLN port).
- Diagnostics: `GlobalUsings.cs` applies `[assembly: ClusterDiagnostics]`, so a failed test dumps its live runs and its
  collection fixture's run (pods, logs, events, node state) under `TestResults/cluster/` (or `NLTG_CLUSTER_DIAG_DIR`);
  a `Poll`/`ClusterPoll` timeout or a failed topology build dumps by itself. Our in-process node is not in the dump
  (its log goes to the test output).
- `ClusterChainEndpoint`: the topology's bitcoind as a `RegtestBitcoinEndpoint` (RPC with the `miner` wallet, ZMQ raw
  block 28332 / raw tx 28333): by **pod IP** from the host, by headless Service name in the cluster. Read once: a test
  that restarts bitcoind must rebuild the node.

## The CLN interop suite on the cluster (phase 2 lane B)

- `NLTG_TEST_BACKEND=docker|cluster` (`Fixtures/TestBackend`, Docker when unset, a typo throws) picks the backend of
  `Fixtures/ClnFixture`; the 17 classes under `Docker/Interop/Cln/` (and the two Explicit gossip captures) run
  unchanged on either. `Fixtures/Cln/`: `IClnBackend`, `DockerClnBackend` (the former fixture: same images, containers,
  flags and published ports) and `ClusterClnBackend` (a warm `ClusterTopologyFixture`, suite `cln-interop`: bitcoind
  `miner` + CLN `nltg-cln` on `emptyDir`, same CLN release by digest, same flags and alias; bitcoind and CLN by pod IP
  from the host; CLN dials us at `host.orb.internal`, `ClnFixture.HostAddressForCln`).
- The classes that ran CLN containers of their own use `ClnFixture.StartClnAsync(ClnNodeSpec)` (`ExtraClnNode`:
  client, node id, `Address`, `PeerHost` for other nodes, `RestartAsync`, disposal removes it): `ClnDualFundTests`
  (`nltg-cln-df` per test), `ClnSpliceReestablishTests` (`nltg-cln-sp2`, `Restartable`: a PVC and its stable
  `<node>-p2p` ClusterIP name on the cluster, a fixed `127.0.0.1` port on Docker) and the captures (`nltg-cln2`,
  reached only by CLN). On the cluster they go into the run's namespace and `TestRun.RemoveNodeAsync` takes them out.
- `ClnClient` runs `lightning-cli` through a `ClnExec` delegate (`docker exec`, or `ClusterClnBackend.KubeExec`); the
  tests catch the Integration.Tests `ClnRpcException` on both backends.
- Blocks ZMQ never announced: a subscription to bitcoind's pod is slower to set up than to Docker's 127.0.0.1 port,
  so a block mined right after a node's start was lost and the monitor waited for the next block (the first
  3-at-once CLN batch). The product's chain monitor now polls the tip (`Bitcoin:TipPollInterval`, default 30 s;
  `NLightningTestNode` sets 1 s on both backends) and catches up with a Warning "ZMQ announced no block from height
  ..."; the earlier test-only ZMQ startup guard is gone, so the cluster exercises the product path.
- The Tor interop suite (`Docker/Interop/Tor/`, `Category=Interop.Tor`) stays on Docker: on the cluster backend its
  fixture starts nothing and its tests skip with the reason (`TestBackend.SkipOnCluster`).
- Run: `scripts/run-cluster.sh -n 3 --suite cln` (N processes, each its own run id and namespace, no Docker lock;
  `--class` for one class, `--explicit on` adds the captures). One process by hand:
  `NLTG_TEST_BACKEND=cluster NLTG_KUBE_CONTEXT=orbstack dotnet test/NLightning.Integration.Tests/bin/Release/net10.0/NLightning.Integration.Tests.dll -trait Category=Interop.Cln`.
  `scripts/run-interop.sh cln` is unchanged and runs the Docker backend (under the machine's Docker lock).

## Postgres and partitions on the cluster (test harness phase 4)

- `Fixtures/PostgresFixture` keeps its members (`DbConnectionString`, `ConnectionStringFor`, `StartNamed`, `HostPort`,
  plus `Host` and `Backend`) over `Fixtures/Postgres/IPostgresBackend`: `DockerPostgresBackend` (the former fixture,
  unchanged: `postgres:16.2-alpine` on a `127.0.0.1` port) or `ClusterPostgresBackend` (its own run namespace, suite
  `postgres` or `postgres-<name>`, one `PostgresNode` by digest on an `emptyDir`, reached at its pod IP; about 4-5 s
  to ready). `Docker/PostgresTests` run unchanged on both; `MultiNodeHarnessTests`' Postgres fact gets the backend's
  server through `StartNamed` but still needs the LND Docker fixture (phase 3).
- `Live/ServerDatabaseClusterTests` (Explicit, collection `postgres`): the server-database restart on the cluster: our
  node on a fresh database of the collection's server connects to an LND pod, stops, starts and redials it from the
  stored peer.
- `Live/PartitionClusterTests` (Explicit, one topology per test: bitcoind, our node, CLN with `ProcessFaults`, a channel
  `nltg` → `cln` with a push; `Node:ReconnectMaxDelay` 4 s): an HTLC to a frozen CLN across a partition (our ping gets
  no pong and drops the link; the HTLC is kept, then settles after the heal through `channel_reestablish`), a partition
  that outlasts our reconnect attempts (CLN drops us; a payment fails at once without an HTLC; our node reconnects by
  itself after the heal), a CLN whose `lightningd` is frozen behind a live `connectd` (our `channel_reestablish` goes
  unanswered: the channel stays gated and refuses a payment), and CLN split from bitcoind (CLN's height stalls, our
  node follows the tip and pays over the established connection; CLN catches up after the heal).
- `Live/ChainMonitorZmqClusterTests` (Explicit): bitcoind keeps only RPC and P2P open (`LimitIngressPortsAsync`) and
  restarts in place, so our ZMQ subscriber is gone (`TcpConnectionTable` shows no connection on 28332); our node
  (tip poll every 5 s) follows the chain over RPC alone (NL-775's `TipPollCatchUps` grows), opens a channel to CLN,
  sees it confirm and pays; after the heal ZMQ comes back and the next block arrives within the poll interval with no
  catch-up.
- A partition cuts new connections only, so every test also drops the established one, and how matters: our
  `DisconnectPeer` is "on purpose" and never redialled; CLN's `disconnect` or our own ping timeout are drops our node
  reconnects after (`PeerManager`'s backoff).
- Run: `scripts/run-cluster.sh -n 1 --suite postgres` and `scripts/run-cluster.sh -n 1 --suite faults` (no Docker
  lock). The Docker side of the Postgres round trips is unchanged (`PostgresTests` from the host, under the lock).

## Reachability (OrbStack, host-side tests)

- Our node listens on 127.0.0.1 (all interfaces when `NLTG_HOST_ADDRESS` names another host) and is announced to the
  pods as `host.orb.internal:<port>` (`InProcessNode.GetAddressAsync`).
- Pods that dial us show up as 127.0.0.1 and are saved inbound-only (NL-497), so **our node dials out**: use
  `TopologyDeployer.ConnectAsync(ourNode, await peer.GetAddressAsync(ct), ...)`, or declare the channel with our node as
  the funder (`AddChannel("nltg", "cln", ...)`), which connects from our side.
- A bare peer alias (LND's address) is dialled at `<alias>.<namespace>.svc.cluster.local` (`ResolvePeerHost`), which the
  host resolves and which follows the pod after a restart; CLN's stable `<node>-p2p` ClusterIP name is used as given.
  Our node stores the DNS name and redials it after `RestartAsync`.

## Tests

- `InProcessNodeTests`: the pure parts (host resolution, mappings, open request, chain endpoint, settings); normal CI.
- `InProcessTopologyFixtureTests`: what the warm fixture declares and that it stops unstarted; normal CI.
- `Live/InProcessNodeClusterTests` (`Category=Cluster`, `Explicit`, not under `Docker`, no Docker lock): CLN (dual-funded
  open, pay both ways, cooperative close) and LND (v1 open by the topology with a push, pay both ways, restart, pay,
  cooperative close). Run: `scripts/run-cluster.sh -n 3 -p integration --class
  NLightning.Integration.Tests.Cluster.Live.InProcessNodeClusterTests`.
- The CLN proof advertises `option_shutdown_anysegwit`: CLN v26.06.8 sends a P2TR `shutdown` script on a dual-funded
  channel without the option negotiated, which our default features refuse ("shutdown scriptpubkey is not a valid
  form"), and the close stalls.