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
    (grace 0). Pause and partition belong to a later `Faults/`.
  - `ILightningTestPeer`: the facade shape (node id, address, connect/disconnect, new address, open channel, list
    channels, invoice, pay). Implementations live per kind (`Nodes/<Kind>/`), amounts in `Sat`/`Msat` longs, node ids
    lower-case hex.
  - `NodeKind` (also the `nltg.kind` label value, lower case).
- `Images/ImageVersions`: the one version table (bitcoind 29.0 Polar and 31.1 official by digest, `custom_lnd:latest`
  Never, CLN v26.06.8 by digest, `nltg-eclair:0.14.3` Never, `nltg-ldk-server:dc02b76c` Never, postgres, busybox).
- `Nodes/Lnd/` (LND lane): `LndNodeOptions` (alias, the bitcoind Service, extra flags) → `LndWorkload.Build`
  (`custom_lnd:latest` Never, LNUnit's `AddPolarLNDNode` flags, `lnddir` `/home/lnd/.lnd` on the PVC, readiness =
  `lncli getinfo` answers with `synced_to_chain`); `LndCredentials` (`tls.cert` + `admin.macaroon` read by exec);
  `LndGrpcConnection` (LNUnit.LND's generated `Lnrpc`/`Routerrpc`/`Walletrpc`/`Invoicesrpc` clients, the server
  certificate **pinned** to the node's `tls.cert`, macaroon header; pod IP from the host, pod DNS name in-cluster);
  `LndNode.DeployAsync(run, options, timeout, ct)` implements `ILightningTestPeer` and reconnects after
  `RestartAsync`/`KillAsync`; `LndMapping` (txids, `chan_id` → `BxTxO`, channel points).
- `Topology/Lnd/` (LND lane): `LndPairTopology.BuildAsync` = bitcoind `miner` + `alice`/`bob` with an active
  alice → bob channel; `PayAsync` retries a failed payment (NL-319); `RestartAsync(node, kill)` restarts or kills a node
  and redials it **by its new pod IP**: LND stores the resolved IP of a peer it dialled by name, and the cluster DNS may
  answer with the old IP for a while after a restart. `LndTopologyChain` is a stand-in bitcoind (Polar 29.0,
  `bitcoin-cli` by exec) until the shared `BitcoinCore` node and `Chain/` helpers land.
- `deploy/runner-rbac.yaml`: the in-cluster runner's RBAC (ported from PR #10). Not applied by the spike.

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