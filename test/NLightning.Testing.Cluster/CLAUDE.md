# NLightning.Testing.Cluster — the Kubernetes-native test harness (spike)

Plan: `docs/agents/TEST_HARNESS_PLAN.md` (§3 requirements, §4 structure, §5 spike). This library replaces LNUnit and
the Docker fixtures over time, suite by suite (`wip/harness-spike`; phase 2 ported the CLN interop suite, plan
"Phase 2 record"). It references no NLightning project:
every implementation, our own node included, is driven through the same seams.

## Layout and seams (use these, do not re-invent them)

- `Run/`
  - `TestRun.StartAsync(TestRunOptions, ct)` creates the run's namespace `<prefix>-<run id>` (+ `ResourceQuota` when
    `Options.Quota` is set); `DeployAsync(NodeWorkload, readyTimeout, ct)` returns a `KubeNodeHandle`;
    `DisposeAsync` (refuses a namespace that is not the run's own) stops the run's pods first
    (`RunNamespace.StopPodsAsync`: StatefulSets deleted with `Orphan`, pods with `TeardownGracePeriodSeconds`, default
    1 s, then a wait until none runs, about 1-5 s), deletes the namespace and **returns while it terminates in the
    background** (`WaitForDeletion`, default false since phase 2; the reaper owns leftovers). Why the pods first: the
    namespace controller, finding unfinished pods, waits their largest spec `terminationGracePeriodSeconds` (bitcoind
    30 s) before it looks again. A terminating namespace still holds its admission slot (`RunAdmission.HoldsSlot`) until
    it is gone; live tests assert "gone or terminating" (`Live/RunAssertions`).
  - `RemoveNodeAsync(name, ct)` (`RunNodeRemoval`): takes one node out of a live run (StatefulSet, pod with the
    teardown grace, headless and `-p2p` Services, data PVC) and waits until it is gone, for a node a test adds next to
    a warm topology (the CLN suite's own CLNs) and must remove again. When it fails (timeout, cancellation) the handle
    stays registered and is marked (`IsRemovalPending`); the next `DeployAsync` of the name finishes the removal first,
    so one teardown hiccup never fails every later deployment of the name (`Live/RunLifecycleTests`).
  - `TestRunOptions.FromEnvironment(suite)`: `NLTG_TEST_RUN_ID` (run id; generated when unset),
    `NLTG_TEST_NAMESPACE_PREFIX` (default `nltg-spike`; the real harness uses `nltg`), `NLTG_KUBE_CONTEXT`,
    `NLTG_KEEP_NAMESPACE=1` (keep for debugging).
  - `RunIdentity` (pure names and labels), `RunLabels` (`nltg.run`, `nltg.suite`, `nltg.started` (Unix seconds),
    `nltg.spike`, `nltg.node`, `nltg.kind`, `app.kubernetes.io/managed-by=nltg-test-harness`), `RunNamespace`
    (build/create/delete with the ownership check `IsOwnedBy`), `KubeClientFactory` (in-cluster when
    `KUBERNETES_SERVICE_HOST` is set, else kubeconfig; TLS always verified).
  - Lifecycle (lane runner): every run namespace records its owner (`RunOwner`: annotations `nltg.owner-host`,
    `nltg.owner-pid`, `nltg.owner-start`; `TestRunOptions.Owner` overrides) plus `nltg.keep` / `nltg.ttl-seconds`
    (`RunAnnotations`, from `KeepNamespace`/`Ttl`). A taken name (a second run of one process under one
    `NLTG_TEST_RUN_ID`, or one still terminating) moves to `<id>-2`, `<id>-3` (`TestRunId.WithSuffix`).
    `RunAdmission` caps live run namespaces under the prefix across all processes (`NLTG_MAX_CONCURRENT_RUNS`,
    default 6, `0`/`off` none): wait while full, create, rank by creation time then name, give the slot back if two
    raced past the cap. `QuotaSizing.ForWorkloads(workloads, extraPods)` sizes `TestRunOptions.Quota` from the
    topology's pod specs (sidecars and init containers included; a container without requests/limits is refused).
  - `RunReaper` (`ListAsync`/`ReapAsync`, pure `Evaluate`): only namespaces named `<prefix>-<nltg.run>` with the
    managed-by label (and `nltg.spike=true` unless `RequireSpikeLabel=false`), prefix must start with `nltg`;
    reaps runs older than their TTL (default 6 h) or whose owner process on this host is gone (pid reuse checked by
    start time); an owner on another host, a run without an owner, or a kept one only by TTL; re-checks right
    before each delete. CLI: `nltg-cluster list|reap [--prefix] [--ttl] [--run <id> [--force]] [--all]
    [--dry-run] [--wait] [--context]` (project `test/NLightning.Testing.Cluster.Cli`, logic in `Run/ClusterCli`).
  - `scripts/run-cluster.sh`: builds once, runs the Category=Cluster tests (`--class`/`--method`) N times
    concurrently (`-n`, `-j` ≤ 6), each with `NLTG_TEST_RUN_ID=<batch>-<i>`, logs and xunit XML under
    `TestResults/cluster/<batch>/<run>/`, reaps each run's leftovers, prints a summary table (`summary.txt`).
    `--diag failure|always|off`, `--keep-on-failure` and `--trait` (default `Category=Cluster`); each run's dumps go
    to `<run>/diag/`, the table counts them (DIAG) and the summary lists the folders of every failed run.
- `Kube/`
  - `NodeWorkload`: one node = StatefulSet (1 replica, `Parallel`) + headless Service of the same name
    (`publishNotReadyAddresses`) + optional data volume (`Data = new DataVolume(mountPath, size)`: a PVC template, or
    an `emptyDir` with `Storage: NodeStorage.Ephemeral`), `InitContainers` (run in order before the node), resources
    (`WorkloadResources.Default` 250m/256Mi requests, 1 CPU/1 GiB limits), `ReadinessProbe` (`Probes.Exec/Tcp`),
    `Command`/`Args`/`Env`/`Ports`/`ScratchVolumes`, image pull policy from the `ImageRef`, service links and the SA
    token off, and `CustomizePod` for init containers, sidecars or ConfigMap volumes. Container name = node name.
  - `KubernetesHelper` (ported from LNUnit PR #10, fixed): `WaitForPodReadyAsync`/`WaitForPodRunningAsync` (fail fast
    on `ErrImageNeverPull`, `CrashLoopBackOff`, ...; `previousUid` waits for a new pod), `ExecAsync` → `ExecResult`
    (exit code, binary stdout/stderr), `ReadFileAsync`/`ReadTextFileAsync`/`WaitForFileAsync`, `WriteFileAsync`
    (≤ 256 KiB, base64 in argv), `ReadLogAsync`, `DeletePodAsync`, `ApplyAsync`, standalone PVCs.
  - `PodStatusReader` (ready / fatal reason / one-line description for messages).
  - `NodeStorage` (`Persistent` = PVC, the default; `Ephemeral` = `emptyDir`) and `NodeStorageEnvironment`
    (`NLTG_NODE_STORAGE=persistent|ephemeral`, the default of builders that leave it unset). A PVC costs each wave of
    StatefulSets about 6 s alone and 12-15 s under load (local-path's `WaitForFirstConsumer` binding, a scheduling
    retry, provisioning), an `emptyDir` nothing; `KubeNodeHandle.RestartAsync`/`KillAsync` refuse an ephemeral node
    (its new pod would start empty), a crash in place keeps it.
  - `KubernetesHelper.WaitForServiceAddressAsync`: until a Service's EndpointSlices list an address, i.e. its name
    resolves. CoreDNS caches a miss (NXDOMAIN) for 5 s (measured), so a pod that looks a node up before the node's
    pod has an IP cannot reach it for up to 5 s more.
- `Nodes/`
  - `INodeHandle` (`KubeNodeHandle`): name/alias, namespace, `PodName` (`<name>-0`), `ServiceDnsName`,
    `PodDnsName`, `PodIp`, exec and file IO, logs, `RestartAsync` (graceful delete, same name + PVC) and `KillAsync`
    (a hard stop: delete with a 1 s grace, `KubeNodeHandle.KillGracePeriodSeconds`; a grace-0 delete let the
    replacement start while the old container still ran on the PVC). Crash, pause and partition are in `Faults/`.
  - `ILightningTestPeer`: the facade shape (node id, address, connect/disconnect, new address, open channel, list
    channels, invoice, pay). Implementations live per kind (`Nodes/<Kind>/`), amounts in `Sat`/`Msat` longs, node ids
    lower-case hex.
  - `NodeKind` (also the `nltg.kind` label value, lower case).
  - Our own node in-process (`NodeKind.NLightning`) is deployed by `InProcessNodeDeployer` in
    `test/NLightning.Integration.Tests/Cluster/` (the glue needs the product, so it stays out of this library).
- `Reach/` (spike check 1; matrix in the plan's "Spike check 1 record")
  - `HostEndpoints.ForPods()`: the name pods dial to reach a listener in the test process (`host.orb.internal`, or
    `NLTG_HOST_ADDRESS`); `BindAddressFor` says what to bind (loopback for OrbStack's names). On OrbStack the peer
    shows up as 127.0.0.1 (our node: NL-497 inbound-only), so let our node dial out to the peers.
  - `ReachabilityCheck.RunAsync(run, ct)` (echo node + ClusterIP Service, both directions, `ReachabilityResult.Placement`
    Host or InCluster), `TcpProbe`, `PodProbe` (busybox `nc` in a pod), `HostListener`, `EchoNode`.
  - A new ClusterIP is routed only 4-9 s after the Service is created: address nodes by their headless Service name.
- `Runner/` (the in-cluster fallback, plan R5)
  - `InClusterTestRunner.RunAsync(run, new TestRunnerJob { ... }, onLine, ct)`: creates `RunnerRbac` (ServiceAccount,
    Role, RoleBinding `nltg-test-runner`, all in the run's namespace, nothing cluster-scoped) and the Job (one pod, no
    retry, `activeDeadlineSeconds`), streams the log, returns the exit code.
  - Inside, `TestRunOptions.AdoptNamespace` (`NLTG_ADOPT_NAMESPACE=1`, set by the Job with `NLTG_TEST_RUN_ID` and
    the prefix) makes `TestRun` adopt the host-created namespace (ownership checked), skip the quota, and on dispose
    delete only the nodes it deployed (`AdoptedNamespace`); `TestRun.OwnsNamespace` is false then.
  - Image: `Runner/image/build.sh [Release]` builds `nltg-spike-runner:latest` (SDK 10.0 + the built
    `bin/<config>/net10.0` of the tests, no restore in the image); `RunnerImage.FromEnvironment()` takes
    `NLTG_RUNNER_IMAGE` for a pushed image. Rebuild it after changing the tests.
- `Images/ImageVersions`: the one version table (bitcoind 29.0 Polar and 31.1 official by digest, `custom_lnd:0.21.4-beta`
  Never, CLN v26.06.8 by digest, `nltg-eclair:0.14.3` Never, `nltg-ldk-server:dc02b76c` Never, postgres 16.2-alpine by
  digest, busybox).
- `Nodes/Postgres/` (phase 4): `PostgresNode.DeployAsync(run, PostgresNodeOptions, timeout, ct)` (the fixture's image,
  user/password `superuser`, database `nlightning`, `PGDATA` under an `emptyDir` by default, readiness = `pg_isready`
  over TCP, which the image's Unix-socket-only init server never passes), `Host` = pod IP, `ConnectionString(db)`.
- `Reach/TcpConnectionTable`: a pod's TCP sockets from `/proc/net/tcp{,6}` through an exec (no `ss` in the images);
  `CountEstablishedAsync(node, port)` says whether a client really is connected (e.g. a ZMQ subscriber), which a
  NetworkPolicy does not.
- `Poll` (library root): the one deadline-bound wait (`UntilAsync` for a bool, `UntilDoneAsync` for a check that
  says what is missing, `ForAsync<T>` for a value); every wait of the chain helpers, topologies and node adapters
  goes through it. Do not add another poll loop.
- `Nodes/BitcoinCore/` and `Chain/` (chain lane, details in `Chain/CLAUDE.md`): `BitcoinCoreNode` (the one bitcoind
  of the harness: StatefulSet + PVC (or `emptyDir`, `BitcoinCoreOptions.Storage`), `miner` wallet, ZMQ
  28332/28333/28334, user/password `nltg`), the typed RPC (`IBitcoinCoreRpc` over HTTP or `bitcoin-cli` exec,
  `RpcRoute.Auto`) and `RegtestChain` (mine, wait at the tip, tx waits, reorgs, fee seeding).
  `BitcoinCoreWorkload.StartupWaitContainer` is the Lightning nodes' init container (bitcoind's own image,
  `getblockchaininfo` from the node's pod every 0.2 s until it answers, gives up after 180 s and lets the node start
  anyway; its log says how long it waited and the last error). `BitcoinCoreTopologyChain` mines its 101 maturity blocks
  as 1 to the wallet and 100 to `BurnAddress` (a coinbase to the wallet costs ~40 ms a block, a foreign one ~1 ms: 4.2 s
  against 0.12 s for 100; the spendable 50 BTC at 101 is the same).
- `Topology/` and `Nodes/Cln/` (CLN lane, details in `Topology/CLAUDE.md`): the declarative `TopologyBuilder`
  (`AddBitcoinCore`/`AddLnd`/`AddCln`, fundings, channels, `Storage`, `DeployNodesWithChain`), its chain
  `BitcoinCoreTopologyChain` (the shared bitcoind + `RegtestChain` as an `ITopologyChain`, the default `ChainFactory`;
  its `ITopologyChainEndpoint` is known before it is up), the deployers `ClnNodeDeployer` and `LndNodeDeployer` (both
  registered by default, both `DeploysWithChain`: their StatefulSets start in the chain's wave behind the startup
  wait), `StableNodeAddress`, the CLN node and `ClusterTopologyFixture<TDefinition>` (a topology kept warm per xunit
  collection).
- `Nodes/Lnd/` (LND lane): `LndNodeOptions` (alias, the bitcoind Service, extra flags) → `LndWorkload.Build`
  (`custom_lnd:0.21.4-beta` Never, LNUnit's `AddPolarLNDNode` flags, `lnddir` `/home/lnd/.lnd` on the PVC, readiness =
  `lncli getinfo` answers with `synced_to_chain`); `LndCredentials` (`tls.cert` + `admin.macaroon` read by exec);
  `LndGrpcConnection` (LNUnit.LND's generated `Lnrpc`/`Routerrpc`/`Walletrpc`/`Invoicesrpc` clients, the server
  certificate **pinned** to the node's `tls.cert`, macaroon header; pod IP from the host, pod DNS name in-cluster);
  `LndNode.DeployAsync(run, options, timeout, ct)` implements `ITopologyLightningNode` (its block height counts only
  once `synced_to_chain`: an open before the wallet caught up fails "channels cannot be created before the wallet is
  fully synced") and reconnects after `RestartAsync`/`KillAsync`; `LndNodeDeployer` puts LND in a declarative topology (`AddLnd`); `LndMapping` (txids,
  `chan_id` → `BxTxO`, channel points). `LndNodeOptions` defaults follow the shared bitcoind (`miner`, `nltg`, ZMQ
  28332/28333).
- `Topology/Lnd/` (LND lane): `LndPairTopology.BuildAsync` = the shared bitcoind `miner`
  (`BitcoinCoreTopologyChain`) + `alice`/`bob` (started in bitcoind's wave, `Settings.DeployNodesWithChain`;
  `Settings.Storage`) with an active alice → bob channel; `PayAsync` retries a failed
  payment (NL-319; any `ILightningTestPeer` pair); `RestartAsync(node, kill)` restarts or kills a node and redials it
  **by its new pod IP**: LND stores the resolved IP of a peer it dialled by name, and the cluster DNS may answer with
  the old IP for a while after a restart.
- `Faults/`: `FaultInjector` (`run.CreateFaultInjector(log)`; disposing resumes and heals; `Events` is the
  timeline). Measured on OrbStack (k3s, flannel host-gw + k3s's kube-router policy controller):
  - `RestartAsync` (graceful) and `KillAsync` (pod deleted with a 1 s grace) replace the pod; PVC data stays, the DNS
    names follow the new pod IP. `KillAsync` is **not** a crash: the node gets SIGTERM, then SIGKILL after 1 s, and
    the replacement starts only once the old container is gone (the live test asserts TERM before the new START; with
    grace 0 the StatefulSet started the replacement first, both on the PVC at once).
  - `CrashAsync` is the crash: SIGKILL to the main container's processes, restart in place (same pod, UID, IP and
    PVC; restart count +1; a second crash within 10 min waits for the kubelet back-off).
  - `PauseAsync`/`ResumeAsync`: SIGSTOP/SIGCONT through exec (POSIX `sh` builtins, the container's own cgroup).
    Pause and crash need `NodeWorkload.WithProcessFaults()` (`shareProcessNamespace`): Linux ignores SIGSTOP/SIGKILL
    sent to PID 1 from inside its namespace; without it they throw `FaultNotSupportedException`.
  - `PartitionAsync(side, others, options)` / `IsolateAsync`: a NetworkPolicy (`nltg-partition-<n>`, label
    `nltg.fault=partition`). Enforced for **new** connections both ways (pod-to-pod dropped, host-to-pod refused);
    **established TCP connections survive** (conntrack), so disconnect the peers (node command or restart) after
    partitioning. DNS stays reachable by default; `PartitionOptions.AllowedIngressCidrs` (`NLTG_RUNNER_CIDRS`) lets
    the runner keep driving an isolated node (OrbStack host: `192.168.194.0/32`). `HealAsync` deletes the policy.
    Policies only allow, so the isolate and split shapes also cut the pod from the host.
  - Phase 4 shapes (`PartitionShape`): `PartitionFromHostAsync(nodes)` (**outside**: the nodes keep every pod of the
    run and DNS and lose the host, i.e. the test process and our in-process nodes; the partition between a pod peer
    and our node; `AllowedIngressCidrs` ignored) and `LimitIngressPortsAsync(node, openPorts)` (**ports**: new
    connections into the node reach only those ports, its own connections out are untouched; e.g. bitcoind with RPC
    and P2P open and its ZMQ feeds cut).
  - `RestartInPlaceAsync(node, stopCommand)`: the node's own clean stop (`bitcoin-cli stop`) and the kubelet's restart
    of the container in the same pod (same UID, IP and data, an `emptyDir` included; restart count +1): every
    connection to the node drops, which a partition alone never does. No shared process namespace needed.
  - `PauseProcessAsync(node, name)`: SIGSTOP only to the processes of that `comm` name and their ancestors in the
    container (CLN's `lightningd` while `connectd` keeps answering the transport). Signal order (NL-795): STOP goes
    parents first and CONT children first (by depth in the process tree). CLN's entrypoint is a bash wrapper that
    waits for `lightningd`: resumed before its child, it saw the stop as the child's end and exited 147, taking the
    container down at `ResumeAsync` (found by the first phase 4 runs; unit tests pin the order, the partition tests
    prove it live).
- `Diagnostics/` (phase 2 lane D): failure diagnostics, written to
  `<root>/<test or fixture>/<namespace>/` where `<root>` is `NLTG_CLUSTER_DIAG_DIR` (`run-cluster.sh` sets
  `TestResults/cluster/<batch>/<run>/diag`) or `<repo>/TestResults/cluster/<run id>`.
  - Contents (`NamespaceDumper`, read-only): `pods.txt`, `workloads.txt` (StatefulSets, Services, NetworkPolicies),
    `events.txt` (oldest first), `storage.txt` (PVCs and their PVs), per pod `pods/<pod>/describe.txt` (conditions,
    container state and last state, exit codes, restarts, probes, env names), `<container>.log` and, after a restart,
    `<container>.previous.log`, and the node's own state `state/*.json` by its `nltg.kind` label
    (`NodeStateCommands`: bitcoind `getblockchaininfo`/`getpeerinfo`/`getmempoolinfo`, CLN `getinfo`/
    `listpeerchannels`/`listfunds`, LND `getinfo`/`listchannels`/`pendingchannels`/`listpeers`); `summary.txt` lists
    the files and what could not be collected; `failure.txt` the reasons. No secret file is ever read (macaroons,
    `hsm_secret`, keys) and every file goes through `SecretRedactor` (passwords, tokens, `rpcauth` masked).
  - When (`NLTG_CLUSTER_DIAG`, `DiagnosticsSettings`): `failure` (default), `always` (also after every test and before
    every run's deletion) or `off`. In `failure` mode a dump happens at a `Poll` timeout (the live runs of the current
    test or fixture, while the bad state is still there; `ClusterDiagnostics.SuppressPollCapture()` for a wait that is
    expected to time out), when `TestRun.DeployAsync`'s readiness wait or `TopologyBuilder.BuildAsync` fails (that
    run), and after a failed test through the xunit v3 hook `[assembly: ClusterDiagnostics]`
    (`ClusterDiagnosticsAttribute`; a `BeforeAfterTestAttribute`, whose `After` sees `TestContext.Current.TestState`).
    One failure seen by several hooks is dumped once per label and its other reasons are appended to `failure.txt`.
  - The hook only sees runs still alive when the test ends: fixture runs (`ClusterTestScope`: a run started in a class
    or collection fixture belongs to that collection, one started in a test body to that test) and runs a test class
    disposes in its own `DisposeAsync`. A run the test body disposed (`await using`) is gone by then, so an assertion
    failure there is not dumped: wrap the body in `run.CaptureOnFailureAsync(what, action)` or keep the run in a fixture.
  - Manual: `await run.DumpAsync(label, reason, ct)` (any mode) returns a `DiagnosticsDump` (folder, files, errors).
  - `NLTG_KEEP_NAMESPACE=failure` (`TestRunOptions.KeepNamespaceOnFailure`) keeps a run's namespace when a failure
    was recorded on it (`TestRun.Diagnostics.Failed`), annotated `nltg.keep` so the reaper leaves it until its TTL
    (6 h); `nltg-cluster reap --run <id> --force` removes it earlier.
- `deploy/runner-rbac.yaml`: the in-cluster runner's RBAC (ported from PR #10). Not applied by the spike.
- Integration notes: an in-cluster runner's Job pod counts against the run's quota: size it with
  `QuotaSizing.ForWorkloads(workloads, extraPods: 1, job.Resources)`. An adopted run (`TestRunOptions.AdoptNamespace`)
  admits itself without the cap (the host already holds the slot) and its disposal also removes its nodes'
  `StableNodeAddress` Services.

## Running a ported suite (phase 2)

- The suites keep their fixtures; `NLTG_TEST_BACKEND=docker|cluster` (Integration.Tests `Fixtures/TestBackend`, Docker
  when unset, an unknown value throws) picks the backend per process. Ported so far: the CLN interop suite
  (`ClnFixture` -> `ClusterClnBackend`, a warm `ClusterTopologyFixture`) and, in phase 4, `PostgresFixture`
  (`ClusterPostgresBackend`: a run namespace with one `PostgresNode`, reached at its pod IP; `StartNamed` gets a
  namespace of its own). `scripts/run-cluster.sh -n 1 --suite postgres` runs `Docker/PostgresTests` and
  `Cluster/Live/ServerDatabaseClusterTests` on it; `--suite faults` runs the partition tests
  (`Cluster/Live/PartitionClusterTests`, `ChainMonitorZmqClusterTests`).
- `scripts/run-cluster.sh -n 3 --suite cln` builds once and runs 3 processes of `-p integration --trait
  Category=Interop.Cln`, each with `NLTG_TEST_BACKEND=cluster` and its own run id and namespace (`--class X` narrows it,
  `--explicit on` adds the captures, `--keep-on-failure` keeps a failed run's namespace). No Docker lock is needed.
- A new suite follows the same pattern: the fixture keeps its members and delegates to a Docker backend (the old code,
  unchanged) and a cluster backend (`ClusterTopologyFixture`, plus `InProcessTopologyFixture` for our nodes); test
  bodies reach the backend only through the fixture.

## Rules while batch work shares the machine

- Only namespaces `nltg-spike-*` labelled `nltg.run`/`nltg.spike=true` (that is what `TestRun` creates); never touch
  `default`, `kube-*` or another run's namespace; no cluster-scoped objects; at most 6 spike namespaces at once;
  pod requests at most 1 CPU / 1 GiB.
- Never rebuild or retag existing images; new images only as `nltg-spike-*` (`ImageVersions.SpikeImagePrefix`).
- Never run the Docker suites (`scripts/run-*.sh`, LNUnit fixtures) from this lane.

## Tests

- Unit tests (`test/NLightning.Testing.Cluster.Tests`, no cluster) run in the normal `dotnet test` run.
- Live tests carry `[Trait("Category", "Cluster")]` and `[Fact(Explicit = true)]`:
  `NLTG_KUBE_CONTEXT=orbstack dotnet run --project test/NLightning.Testing.Cluster.Tests -c Release -f net10.0 -- -explicit only -trait Category=Cluster`
  (or the built `bin/Release/net10.0/NLightning.Testing.Cluster.Tests` with the same arguments).
- `Live/DiagnosticsClusterTests` (`Category=Cluster`, green): a manual dump of bitcoind + CLN + LND, and a topology whose
  CLN node never becomes ready (an unknown lightningd option), dumped by the build and kept on failure.
  `Live/DiagnosticsFailureProofTests` fail **on purpose** (a `Poll` timeout, an assertion) to prove the hook; they carry
  `Category=ClusterFailureProof` instead of `Cluster` so the normal runs skip them:
  `scripts/run-cluster.sh -n 1 --trait Category=ClusterFailureProof --keep-on-failure`, then
  `nltg-cluster reap --run <id> --force`.
- `Live/LightningDialBackTests`: LND and CLN in pods dial a loopback listener in the test process at
  `host.orb.internal` and it receives their BOLT 8 act one (spike check 1 with real implementations).
- `Live/StartupTimingTests` (phase 2 startup cuts): `StartupTimingClnTests` and `StartupTimingLndTests`, one row
  each for the spike's two waves on PVCs, one wave on PVCs and one wave on `emptyDir`s; each logs `[timing]` lines
  (build per step, disposal). `Live/TopologyFixtureTests` is the warm topology proof (two tests, one namespace, one
  build). `scripts/run-cluster.sh -n 3 --class '...StartupTimingClnTests' --class '...StartupTimingLndTests'` runs six
  topologies at once.
- `Live/InClusterRunnerTests` need the runner image (`Runner/image/build.sh` first; a lane that runs beside others
  builds its own tag, `NLTG_RUNNER_TAG=<lane> ...build.sh` and `NLTG_RUNNER_IMAGE=nltg-spike-runner:<lane>`); `Live/ReachabilityTests` assert
  OrbStack's matrix and only record it on another context. `Live/MixedTopologyTests` is the integration proof (LND +
  CLN on the shared bitcoind through `TopologyBuilder`, a channel and a payment each way); `scripts/run-cluster.sh
  -n 3 --class NLightning.Testing.Cluster.Tests.Live.MixedTopologyTests` runs it three times at once.