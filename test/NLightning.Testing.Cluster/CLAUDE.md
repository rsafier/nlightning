# NLightning.Testing.Cluster — the Kubernetes-native test harness (spike)

Plan: `docs/agents/TEST_HARNESS_PLAN.md` (§3 requirements, §4 structure, §5 spike). This library replaces LNUnit and
the Docker fixtures over time; for now it is the spike on `wip/harness-spike`. It references no NLightning project:
every implementation, our own node included, is driven through the same seams.

## Layout and seams (use these, do not re-invent them)

- `Run/`
  - `TestRun.StartAsync(TestRunOptions, ct)` creates the run's namespace `<prefix>-<run id>` (+ `ResourceQuota` when
    `Options.Quota` is set); `DeployAsync(NodeWorkload, readyTimeout, ct)` returns a `KubeNodeHandle`;
    `DisposeAsync` deletes the namespace (refuses one that is not the run's own) and waits for it to go.
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
- `Kube/`
  - `NodeWorkload`: one node = StatefulSet (1 replica, `Parallel`) + headless Service of the same name
    (`publishNotReadyAddresses`) + optional PVC template (`Data = new DataVolume(mountPath, size)`), resources
    (`WorkloadResources.Default` 250m/256Mi requests, 1 CPU/1 GiB limits), `ReadinessProbe` (`Probes.Exec/Tcp`),
    `Command`/`Args`/`Env`/`Ports`/`ScratchVolumes`, image pull policy from the `ImageRef`, service links and the SA
    token off, and `CustomizePod` for init containers, sidecars or ConfigMap volumes. Container name = node name.
  - `KubernetesHelper` (ported from LNUnit PR #10, fixed): `WaitForPodReadyAsync`/`WaitForPodRunningAsync` (fail fast
    on `ErrImageNeverPull`, `CrashLoopBackOff`, ...; `previousUid` waits for a new pod), `ExecAsync` → `ExecResult`
    (exit code, binary stdout/stderr), `ReadFileAsync`/`ReadTextFileAsync`/`WaitForFileAsync`, `WriteFileAsync`
    (≤ 256 KiB, base64 in argv), `ReadLogAsync`, `DeletePodAsync`, `ApplyAsync`, standalone PVCs.
  - `PodStatusReader` (ready / fatal reason / one-line description for messages).
- `Nodes/`
  - `INodeHandle` (`KubeNodeHandle`): name/alias, namespace, `PodName` (`<name>-0`), `ServiceDnsName`,
    `PodDnsName`, `PodIp`, exec and file IO, logs, `RestartAsync` (graceful delete, same name + PVC) and `KillAsync`
    (a hard stop: delete with a 1 s grace, `KubeNodeHandle.KillGracePeriodSeconds`; a grace-0 delete let the
    replacement start while the old container still ran on the PVC). Crash, pause and partition are in `Faults/`.
  - `ILightningTestPeer`: the facade shape (node id, address, connect/disconnect, new address, open channel, list
    channels, invoice, pay). Implementations live per kind (`Nodes/<Kind>/`), amounts in `Sat`/`Msat` longs, node ids
    lower-case hex.
  - `NodeKind` (also the `nltg.kind` label value, lower case).
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
- `Images/ImageVersions`: the one version table (bitcoind 29.0 Polar and 31.1 official by digest, `custom_lnd:latest`
  Never, CLN v26.06.8 by digest, `nltg-eclair:0.14.3` Never, `nltg-ldk-server:dc02b76c` Never, postgres, busybox).
- `Poll` (library root): the one deadline-bound wait (`UntilAsync` for a bool, `UntilDoneAsync` for a check that
  says what is missing, `ForAsync<T>` for a value); every wait of the chain helpers, topologies and node adapters
  goes through it. Do not add another poll loop.
- `Nodes/BitcoinCore/` and `Chain/` (chain lane, details in `Chain/CLAUDE.md`): `BitcoinCoreNode` (the one bitcoind
  of the harness: StatefulSet + PVC, `miner` wallet, ZMQ 28332/28333/28334, user/password `nltg`), the typed RPC
  (`IBitcoinCoreRpc` over HTTP or `bitcoin-cli` exec, `RpcRoute.Auto`) and `RegtestChain` (mine, wait at the tip,
  tx waits, reorgs, fee seeding).
- `Topology/` and `Nodes/Cln/` (CLN lane, details in `Topology/CLAUDE.md`): the declarative `TopologyBuilder`
  (`AddBitcoinCore`/`AddLnd`/`AddCln`, fundings, channels), its chain `BitcoinCoreTopologyChain` (the shared bitcoind
  + `RegtestChain` as an `ITopologyChain`, the default `ChainFactory`), the deployers `ClnNodeDeployer` and
  `LndNodeDeployer` (both registered by default), `StableNodeAddress` and the CLN node.
- `Nodes/Lnd/` (LND lane): `LndNodeOptions` (alias, the bitcoind Service, extra flags) → `LndWorkload.Build`
  (`custom_lnd:latest` Never, LNUnit's `AddPolarLNDNode` flags, `lnddir` `/home/lnd/.lnd` on the PVC, readiness =
  `lncli getinfo` answers with `synced_to_chain`); `LndCredentials` (`tls.cert` + `admin.macaroon` read by exec);
  `LndGrpcConnection` (LNUnit.LND's generated `Lnrpc`/`Routerrpc`/`Walletrpc`/`Invoicesrpc` clients, the server
  certificate **pinned** to the node's `tls.cert`, macaroon header; pod IP from the host, pod DNS name in-cluster);
  `LndNode.DeployAsync(run, options, timeout, ct)` implements `ITopologyLightningNode` and reconnects after
  `RestartAsync`/`KillAsync`; `LndNodeDeployer` puts LND in a declarative topology (`AddLnd`); `LndMapping` (txids,
  `chan_id` → `BxTxO`, channel points). `LndNodeOptions` defaults follow the shared bitcoind (`miner`, `nltg`, ZMQ
  28332/28333).
- `Topology/Lnd/` (LND lane): `LndPairTopology.BuildAsync` = the shared bitcoind `miner`
  (`BitcoinCoreTopologyChain`) + `alice`/`bob` with an active alice → bob channel; `PayAsync` retries a failed
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
- `deploy/runner-rbac.yaml`: the in-cluster runner's RBAC (ported from PR #10). Not applied by the spike.
- Integration notes: an in-cluster runner's Job pod counts against the run's quota: size it with
  `QuotaSizing.ForWorkloads(workloads, extraPods: 1, job.Resources)`. An adopted run (`TestRunOptions.AdoptNamespace`)
  admits itself without the cap (the host already holds the slot) and its disposal also removes its nodes'
  `StableNodeAddress` Services.

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
- `Live/InClusterRunnerTests` need the runner image (`Runner/image/build.sh` first); `Live/ReachabilityTests` assert
  OrbStack's matrix and only record it on another context. `Live/MixedTopologyTests` is the integration proof (LND +
  CLN on the shared bitcoind through `TopologyBuilder`, a channel and a payment each way); `scripts/run-cluster.sh
  -n 3 --class NLightning.Testing.Cluster.Tests.Live.MixedTopologyTests` runs it three times at once.