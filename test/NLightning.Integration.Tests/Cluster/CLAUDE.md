# Cluster/: our in-process node in a cluster topology (test harness phase 2, lane A)

Plan: `docs/agents/TEST_HARNESS_PLAN.md` (phase 2; "Spike check 1 record" for the reachability rules). The harness
library `test/NLightning.Testing.Cluster` references no NLightning project, so the glue that puts our node
(`Docker/Utils/NLightningTestNode`, built from the daemon's `AddNltgNodeServices`) into its topologies lives here, on
the library's existing seams: `NodeKind.NLightning`, `ILightningNodeDeployer`, `ITopologyLightningNode`.

Running the ported suites: `scripts/run-cluster.sh --matrix [suites]` runs several at once within the namespace cap
(phase 5; test/CLAUDE.md "Phase 5"), `scripts/run-cluster.sh -n N --suite <suite>` one of them N times (below).

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
  once by the first test that runs (`IAsyncLifetime.InitializeAsync` awaits `fixture.EnsureStartedAsync()`, NL-800),
  nothing reset, the isolation rules in the base class's XML docs): owns the `InProcessNodeDeployer`
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

- **The cluster is the only backend of the CLN, Eclair, LDK and Postgres fixtures since NL-866** (owner decision
  2026-10-03; the LND network's since NL-820): their Docker backends (`DockerClnBackend`, `DockerEclairBackend`,
  `DockerLdkBackend`, `DockerPostgresBackend`) and the `I*Backend` switches are gone, and `Fixtures/DockerAbsenceTests`
  keeps the Docker API and the `docker` CLI out of every source but the Tor fixture (`Fixtures/Tor/`), the shared
  `DockerContainerUtils`, `SqlServerFixture` (not run) and `Testing.Lnd.Tests`' Explicit live test.
  `NLTG_TEST_BACKEND=cluster` (`Fixtures/TestBackend`; also `k8s`/`kubernetes`) is the explicit opt-in; unset, every
  cluster fixture starts nothing and its tests are skipped with the reason (`ClusterAvailability`: "The CLN fixture
  runs on the cluster backend only (NL-866): set NLTG_TEST_BACKEND=cluster or run scripts/run-cluster.sh --matrix
  cln"; the test classes call `fixture.SkipIfUnavailable()` in their constructor, so their cleanup never touches a
  fixture that did not start); set, a missing Kubernetes configuration fails the fixture (NL-860); `docker` or a typo
  throws. `scripts/run-cluster.sh` sets it for every suite.
- `Fixtures/ClnFixture` runs `Fixtures/Cln/ClusterClnBackend` (a warm `ClusterTopologyFixture`, suite `cln-interop`:
  bitcoind `miner` + CLN `nltg-cln` on `emptyDir`, the CLN release by digest, its flags and alias, pinned by
  `ClnBackendTests`; bitcoind and CLN by pod IP from the host; CLN dials us at `host.orb.internal`,
  `ClnFixture.HostAddressForCln`). The 17 classes under `Docker/Interop/Cln/` and the two Explicit gossip captures run
  on it.
- The classes that ran CLN containers of their own use `ClnFixture.StartClnAsync(ClnNodeSpec)` (`ExtraClnNode`:
  client, node id, `Address`, `PeerHost` for other nodes, `RestartAsync`, disposal removes it): `ClnDualFundTests`
  (`nltg-cln-df` per test), `ClnSpliceReestablishTests` (`nltg-cln-sp2`, `Restartable`: a PVC and its stable
  `<node>-p2p` ClusterIP name) and the captures (`nltg-cln2`, reached only by CLN). They go into the run's namespace
  and `TestRun.RemoveNodeAsync` takes them out.
- `ClnClient` runs `lightning-cli` through a `ClnExec` delegate (`ClusterClnBackend.KubeExec`; the Tor fixture's
  `docker exec`); the tests catch the Integration.Tests `ClnRpcException`.
- Blocks ZMQ never announced: a subscription to bitcoind's pod was slower to set up than to Docker's 127.0.0.1 port,
  so a block mined right after a node's start was lost and the monitor waited for the next block (the first
  3-at-once CLN batch). The product's chain monitor now polls the tip (`Bitcoin:TipPollInterval`, default 30 s;
  `NLightningTestNode` sets 1 s) and catches up with a Warning "ZMQ announced no block from height
  ..."; the earlier test-only ZMQ startup guard is gone, so the cluster exercises the product path.
- The Tor interop suite (`Docker/Interop/Tor/`, `Category=Interop.Tor`, fixture `Fixtures/Tor/TorInteropFixture` with
  its Docker chain `TorChainHost`) is the one suite left on Docker: it runs without `NLTG_TEST_BACKEND`
  (`scripts/run-interop.sh tor`, under the machine's Docker lock), and under `NLTG_TEST_BACKEND=cluster` its fixture
  starts nothing and its tests skip with the reason (`TestBackend.SkipOnCluster`).
- Run: `scripts/run-cluster.sh -n 3 --suite cln` (N processes, each its own run id and namespace, no Docker lock;
  `--class` for one class, `--explicit on` adds the captures). One process by hand:
  `NLTG_TEST_BACKEND=cluster NLTG_KUBE_CONTEXT=orbstack dotnet test/NLightning.Integration.Tests/bin/Release/net10.0/NLightning.Integration.Tests.dll -trait Category=Interop.Cln`.
  `scripts/run-interop.sh cln` only prints these commands and exits 2 (NL-866).

## The LND regtest network on the cluster (phase 3)

- `Fixtures/LightningRegtestNetworkFixture` (collections `regtest`, `onchain-regtest`, `gossip-regtest`) runs on
  `ClusterLndBackend` only (`IAsyncLifetime`; the network starts in `InitializeAsync`). NL-820 (owner decision
  2026-10-03) retired its Docker backend (`DockerLndBackend`, LNUnit's builder, the only LNUnit user) and removed the
  `LNUnit` package; `Fixtures/LnUnitAbsenceTests` keeps the solution free of it. Without `NLTG_TEST_BACKEND=cluster`,
  or when no Kubernetes configuration can be built, the fixture starts nothing and every test that touches it is
  reported **skipped** with `UnavailableReason` (`SkipIfUnavailable()`; `NLightningTestNode.CreateAsync(fixture, ...)`
  and its fixture constructors skip before taking a port; tested in `Fixtures/LightningRegtestNetworkFixtureTests`). A
  configured cluster that fails is a fixture failure, never a skip. Members: `Bitcoin` (the miner's RPC with its
  `miner` wallet, by pod IP), `BitcoinZmqPorts` (28332/28333 on that host), `LndNodes`, `GetLndNode(alias)` (in-tree
  `LndNodeConnection`s, the same objects across restarts), `RestartLndAsync(alias)`, `GetLndPeerEndpointAsync(lnd)`,
  `HostAddressForLnd` (where LND dials us: `host.orb.internal` or `NLTG_HOST_ADDRESS`), `DumpLndLogsAsync(aliases)`
  (pod logs; the test bodies call it instead of container log dumps; `TestDiagnostics.CurrentTestFailed` says when), `Cluster` (the `ClusterLndBackend`) and
  `UnavailableReason`. The image `custom_lnd:0.21.4-beta` is never pulled or built by the harness: build it once with
  `docker build -t custom_lnd:0.21.4-beta test/Docker/custom_lnd` (OrbStack shares the Docker image store with its
  cluster; a missing image fails the pod at once with `ErrImageNeverPull`).
- `ClusterLndBackend` is a warm `LndRegtestNetworkFixture` (the library's `Topology/Lnd/LndRegtestNetwork`: the Docker
  fixture's nodes, flags, channels and policies on `custom_lnd:0.21.4-beta`, PVCs) plus `Network` (restart, join,
  open, routed payments), `BitcoinEndpoint`, `LndPeerHost(alias)` and `JoinInProcessNodeAsync(name, fundSat)`: our
  node on the network's chain through its own `InProcessNodeDeployer`, stopped before the namespace goes.
- Restart: a StatefulSet restart keeps the DNS names and PVC, the pod IP changes. `RestartLndAsync` has the LND peers
  dial the new pod IP and joined nodes the Service name, and waits until those channels are active again (no NL-262
  address-hold containers; those went with the Docker backend, NL-820). `NLightningTestNode`s made from the fixture
  dial LND at `GetLndPeerEndpointAsync` (`NLightningTestNode.ConnectToAsync(LndNodeConnection)`): the Service name
  `alias.<ns>.svc.cluster.local:9735`, which our node stores
  and redials after the restart (NL-780; LND cannot dial back, it only saw our ephemeral port).
- The LND suites: `scripts/run-cluster.sh -n 1 --suite lnd` runs the `regtest` collection's classes of the `Docker`
  namespace and `Docker.Utils` (the catalog's `lnd`, SQL Server left out; 2 namespaces: the collection's network and
  `MultiNodeHarnessTests`' own Postgres pod, so `-j` is capped at 3); the classes of the fixture's other collections
  run in their own suites: `PostgresTests` in `postgres`, `BackupRestoreFlowTests` in `onchain`,
  `ChannelPolicyPublicFlowTests` and `SpliceLndObserverTests` in `gossip` (`--suite onchain|gossip --class X`).
  No test code drives Docker containers by name any more (the on-chain helpers run on the cluster only since NL-820,
  below; `SkipUnlessDocker`/`RequireDocker` are gone); failure dumps go through `DumpLndLogsAsync` everywhere.
- `Live/LndRegtestNetworkClusterTests` (`Category=Cluster`, `Explicit`): the backend's members answer, our node joins
  (2M sat), opens 1M sat to alice by her Service name, pays carol through alice and is paid by alice, alice restarts,
  and both payments work again. `scripts/run-cluster.sh -n 2 -p integration --class
  NLightning.Integration.Tests.Cluster.Live.LndRegtestNetworkClusterTests` (network 33-54 s, whole test 47-70 s).

## The Eclair interop suite on the cluster (phase 4)

- `Fixtures/EclairFixture` runs `Fixtures/Eclair/ClusterEclairBackend` (a warm `ClusterTopologyFixture`, suite
  `eclair-interop`: bitcoind `miner` on Bitcoin Core 31.1 (Eclair 0.14.3 refuses older) on `emptyDir`, and Eclair
  `nltg-eclair` from the local `nltg-eclair:0.14.3` image (never pulled, never rebuilt by the harness: build it once
  with `docker build -t nltg-eclair:0.14.3 test/Docker/eclair`) with its `eclair.conf` (pinned by `EclairBackendTests`),
  on a PVC because two tests restart it). Its Docker backend (`DockerEclairBackend` on `InteropChainHost`) was retired
  by NL-866.
- Addresses: bitcoind by pod IP (`ClusterChainEndpoint`); Eclair's p2p port at its stable ClusterIP name
  (`nltg-eclair-p2p.<ns>.svc.cluster.local:9735`, `EclairFixture.EclairAddress`), which our nodes store and redial after
  `RestartEclairAsync`; Eclair's API at the pod IP, moved to the new pod after a restart (`EclairClient.Retarget`);
  Eclair dials us at `EclairFixture.HostAddressForEclair` (`host.orb.internal`).
- The liquidity seller of PR #19 (NL-850, `EclairLiquidityAdsTests`, NL-864): `EclairFixture.GetSellerAsync` asks the
  backend (`ClusterEclairBackend.StartSellerAsync`, once per fixture) for a second Eclair `nltg-eclair-seller` on the
  same bitcoind, wallet `eclair-seller`, with `EclairFixture.SellerConfigLines` (`eclair.liquidity-ads` at
  `EclairFixture.SellerRates`) after the common configuration: a node deployed into the collection's run namespace
  (`EclairNode.Workload` with `ClusterEclairBackend.BuildSellerOptions`: `emptyDir`, never restarted, dialed and called
  at its pod IP; the wallet init container creates the wallet), removed with the namespace. Its config is pinned by
  `EclairBackendTests`; a failed test dumps its log through `EclairFixture.DumpSellerLogAsync`.
- Run: `scripts/run-cluster.sh -n 1 --suite eclair` and `--suite eclair2` (no Docker lock; the catalog runs
  `EclairSpliceTests`, the longest class, as `eclair2`, its own process and Eclair topology, NL-841; `--class` for one
  class, `--explicit on` adds the Explicit E-X1 open). `scripts/run-interop.sh eclair` only prints these commands and
  exits 2 (NL-866).

## The LDK interop suite on the cluster (test harness phase 4)

- `Fixtures/LdkFixture` runs `Fixtures/Ldk/ClusterLdkBackend` (a warm `ClusterTopologyFixture`, suite
  `ldk-interop`: bitcoind `miner` 31.1 on `emptyDir` + ldk-server `nltg-ldk` from the local `nltg-ldk-server:dc02b76c`
  image (never rebuilt, pulled `Never`: build it once with
  `docker build -t nltg-ldk-server:dc02b76c test/Docker/ldk_server`, 10-20 min cold) on a PVC, through the harness's
  `LdkNodeDeployer`; its `config.toml` is pinned by `LdkBackendTests`). The 27 tests under `Docker/Interop/Ldk/` reach
  it only through the fixture. Its Docker backend (`DockerLdkBackend`) was retired by NL-866.
- Addresses: this process dials LDK at its stable ClusterIP (`LdkFixture.LdkHost`; LDK announces the same address),
  which survives `RestartLdkAsync` (a graceful pod restart: 5 s drain, then SIGTERM; the new pod on the same PVC keeps
  the node id and channels). LDK dials our listeners at `LdkFixture.HostAddressForLdk` (`host.orb.internal` on
  OrbStack's cluster).
- `LdkClient` runs `ldk-server-cli` through an `LdkExec` delegate (`ClusterLdkBackend.KubeExec`);
  `LdkFixture.DumpLdkLogAsync` dumps LDK's log and `LdkFixture.GetTipAsync` reads the chain's tip.
- Run: `scripts/run-cluster.sh -n 1 --suite ldk` (no Docker lock; `--class` for one class). `scripts/run-interop.sh
  ldk` only prints these commands and exits 2 (NL-866).

## Postgres and partitions on the cluster (test harness phase 4)

- `Fixtures/PostgresFixture` (`DbConnectionString`, `ConnectionStringFor`, `StartNamed`, `StartNamedOnCluster`, `Host`,
  `HostPort`, `UnavailableReason`) runs `Fixtures/Postgres/ClusterPostgresBackend` only (its own run namespace, suite
  `postgres` or `postgres-<name>`, one `PostgresNode` by digest on an `emptyDir`, reached at its pod IP; about 4-5 s
  to ready); the Docker container (`DockerPostgresBackend`, `postgres:16.2-alpine` on a `127.0.0.1` port) was retired
  by NL-866. It starts in its constructor: without the opt-in its members skip the test, under the opt-in without a
  Kubernetes configuration the constructor throws, and `StartNamed` skips the calling test when unavailable.
  `MultiNodeHarnessTests`' Postgres fact gets its server through `StartNamed` (the `lnd` suite).
- `Live/ServerDatabaseClusterTests` (Explicit, `Category=Cluster`, no collection): the server-database restart on the
  cluster: our node on a fresh database of a Postgres pod the test starts itself
  (`PostgresFixture.StartNamedOnCluster("pg-restart")`, whatever `NLTG_TEST_BACKEND` says) connects to
  an LND pod, stops, starts and redials it from the stored peer. It must not join the `postgres` collection: xunit
  creates a collection's fixture whenever the selection holds any of its tests, Explicit ones included, so a
  `FullyQualifiedName!~Docker` run would start the collection's Postgres fixture (NL-801).
- `Live/PartitionClusterTests` (Explicit, one topology per test: bitcoind, our node, CLN with `ProcessFaults`, a channel
  `nltg` → `cln` with a push; `Node:ReconnectMaxDelay` 4 s): an HTLC to a frozen CLN across a partition (our ping gets
  no pong and drops the link; the HTLC is kept, then settles after the heal through `channel_reestablish`), a partition
  that outlasts our reconnect attempts (CLN drops us; a payment fails at once without an HTLC; our node reconnects by
  itself after the heal), a CLN whose `lightningd` is frozen behind a live `connectd` (our `channel_reestablish` goes
  unanswered: the channel stays gated and refuses a payment; after `Node:ReestablishTimeout`, 15 s there, our node
  drops the connection and its backoff dials CLN again, still gated, NL-796), and CLN split from bitcoind (CLN's height stalls, our
  node follows the tip and pays over the established connection; CLN catches up after the heal).
- `Live/ChainMonitorZmqClusterTests` (Explicit): bitcoind keeps only RPC and P2P open (`LimitIngressPortsAsync`) and
  restarts in place, so our ZMQ subscriber is gone (`TcpConnectionTable` shows no connection on 28332); our node
  (tip poll every 5 s) follows the chain over RPC alone (NL-775's `TipPollCatchUps` grows), opens a channel to CLN,
  sees it confirm and pays; after the heal ZMQ comes back and the next block arrives within the poll interval with no
  catch-up.
- A partition cuts new connections only, so every test also drops the established one, and how matters: our
  `DisconnectPeer` is "on purpose" and never redialled; CLN's `disconnect` or our own ping timeout are drops our node
  reconnects after (`PeerManager`'s backoff).
- Run: `scripts/run-cluster.sh -n 1 --suite postgres` (3 namespaces at once: the collection's server, and the
  server-database test's own server and topology; `--class` runs one class) and `scripts/run-cluster.sh -n 1 --suite
  faults` (no Docker lock). The Postgres round trips have no Docker side since NL-866.

## The ABCD suite on the cluster (test harness phase 6)

- `Docker/Abcd/` (LND alice → our bob → our carol → LND david, 11 tests incl. `AbcdAccountingTests` and the
  container-free `OnceOnlyBuildTests`) runs unchanged on either backend: it reaches LND only through
  `LightningRegtestNetworkFixture` (`GetLndNode`, `Bitcoin`, `LndNodes`, `DumpLndLogsAsync`), and bob and carol dial
  alice and david at `GetLndPeerEndpointAsync` (the Service names on the cluster), so LND's own disconnects in
  `AbcdReestablishTests` and bob's stop/crash in `AbcdRestartTests` end with our nodes redialling the stored names.
- Run: `scripts/run-cluster.sh -n 1 --suite abcd` (1 namespace, the `regtest` collection's network; no Docker lock),
  or as part of the default matrix (`--matrix`). `scripts/run-abcd.sh` is a retired pointer since NL-820. Proven 2026-10-03: 11/11 alone (51-58 s, network ready 28 s), 2 x 11/11 at once.

## The on-chain suites on the cluster (test harness phase 6)

- `Docker/Onchain/` (the catalog's `onchain`: `Docker.Onchain.Onchain*` + `Docker.BackupRestoreFlowTests`, 33 + 2
  `Explicit`) and `Docker/Onchain/Anchors/` (`anchors`, 18) run on `LightningRegtestNetworkFixture`'s cluster backend
  (collection `onchain-regtest`, one namespace each). Reorgs (`invalidateblock`/`reconsiderblock`, `generateblock`),
  `setmocktime`, `prioritisetransaction`, mempool watching (ZMQ raw tx at the miner's pod IP) and LND's 0.21 sweeper
  pacing (`ChainSync.MineUntilLndSweptAsync`, NL-770) needed no change: they reach bitcoind and LND only through the
  fixture (`Bitcoin`, `GetLndNode`, `GetLndPeerEndpointAsync`).
- The two helpers that drove Docker containers run on the cluster only (their Docker paths went with NL-820):
  - `Onchain/Cheater/LndChannelDbRollback` (Proof O5 (a), `AnchorsO5Tests`): `LndRegtestNetwork.RestartAsync(alias,
    whileStopped: ...)` scales david's StatefulSet to 0, a maintenance pod on his PVC (`StoppedNodeMaintenance`, the
    library's `Kube/`) copies `channel.db` to `channel.db.nltg-snapshot` next to it (and later back), david starts again
    and the network has the LND peers redial his new pod IP; our node redials his Service name.
  - `Onchain/Anchors/RelayBitcoind` (`AnchorsPackageRelayTests`, NL-380): a bitcoind pod `relay` of the harness in the
    network's namespace (`BitcoinCoreNode`, the version table's image, `emptyDir`, `-connect=miner:18444`,
    `-maxmempool=5`, wallet `relay`), RPC and ZMQ at its pod IP, removed with `TestRun.RemoveNodeAsync` on disposal.
- `NLightningTestNode.StopAsync` clears the node's SQLite connection pool (NL-825): the pooled connections kept the
  database open, and on the macOS host (where the cluster backend runs the test process) `File.Copy` replaces a file
  by a new inode, so `OnchainO4Tests`' O4 (d) restarted on the files it had replaced instead of the snapshot.
- Run: `scripts/run-cluster.sh -n 1 --suite onchain` and `--suite anchors` (no Docker lock; `--class` for one class,
  `--explicit on` adds the two by-hand O5 variants), or in the default matrix. `scripts/run-onchain.sh` is a retired
  pointer since NL-820.

## The gossip suite on the cluster (test harness phase 6)

- The gossip-regtest collection's 35 tests run as two catalog suites, each its own process and network (one namespace
  each; split in the phase 6 proof to shorten the matrix, NL-841): `gossip` = `Docker.Gossip.*` without the Explicit
  `Gossip.Capture` sub-namespace (30 tests, 8 of them the container-free `GossipProofHelperTests`) and `day0` = `Docker.Day0.*`, `ChannelPolicyPublicFlowTests` and
  `SpliceLndObserverTests` (5). They reach LND only through
  `LightningRegtestNetworkFixture` (`GetLndNode`, `LndNodes`, `Bitcoin`, `GetLndPeerEndpointAsync` for
  `GraphStoreFlowTests`' LND-to-LND opens, `DumpLndLogsAsync`), our nodes dial LND at the Service names.
- NL-830 (test): `SpliceLndObserverTests` waited only for alice's edge of the open before splicing; with two runs at
  once the splice confirmed 7 s after the open's sixth block while bob (who hears it only through alice's 5 s trickle)
  was still taking it in, and bob's LND kept the spent edge for good (his graph closed 0 channels at the splice's block,
  alice's 1). The test now waits for bob's edge too before the splice.
- NL-842 (test): LND's sweeper takes wallet outputs as fee inputs of its anchor and HTLC sweeps after a force close, so
  an LND node that funds an open right after another test's force close can have nothing to spend ("not enough witness
  outputs ... only have 0 BTC available", `PublicChannelFlowTests` G1 (b)). Before an LND node funds, call
  `ChainSync.EnsureLndSpendableAsync(fixture, lnd, minSat, nodes, ct)` (confirmed, unleased, above the anchors reserve;
  two 0.1 BTC outputs and a block when short).
- Run: `scripts/run-cluster.sh -n 1 --suite gossip` and `--suite day0` (no Docker lock; about 13 and 8 min, network
  ready 29-58 s), or in the default matrix. `scripts/run-gossip.sh` is a retired pointer since NL-820.

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