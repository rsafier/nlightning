# NLightning test harness: a unified, isolated, parallel, Kubernetes-native replacement for LNUnit

Status: proposal, 2026-10-02 (revised the same day: Kubernetes-native, owner direction, plus a scale target of about 1,000 nodes across machines). Copied into `docs/agents/` for the spike (§5 step 1, branch `wip/harness-spike`, project `test/NLightning.Testing.Cluster`); the rest stays a proposal until it is scheduled.

## 1. Why

The Docker tests are the project's end-to-end proof. Interop runs against LND, CLN, Eclair and LDK, plus on-chain resolution, gossip, the ABCD multi-hop suite and the database round trips.

They work, but they run **one process at a time**. The fixtures reuse fixed container names, networks and cleanup-by-name, so two runs destroy each other. On a 28-core / 96 GB host (OrbStack VM: 28 CPUs, 63 GB), a full pass is the **sum** of the suites, about 75 min plus queueing:

- LND 8 min;
- on-chain 6 min;
- CLN 13 min;
- Eclair about 10 min;
- LDK 4 min;
- gossip 13 min;
- ABCD 1 min.

Integrations with several Docker-proving lanes spend hours waiting on one lock (batch10, 2026-10-02).

LNUnit carried the LND side. It is LND-shaped: Polar-style LND nodes, a fixed builder, container names equal to aliases. We have pushed past what it was built for. It has no isolation, our other implementations are hand-rolled next to it, there are three restart workarounds (NL-262, NL-276), and its address-hold trick for LND restarts. CLN, Eclair and LDK fixtures and clients already live in the solution, about 4,000 lines in `Docker/Utils` and `Fixtures`.

The current setup is slow but it works, so its behaviour is the requirements list. The aim is a fresh harness inside the NLightning solution that keeps the good parts of LNUnit and of our own fixtures.

## 2. Inventory: what the tests use today

- **LNUnit (lnunit 3.0.4):**
  - used in 47 of the 124 Docker test files;
  - the main surface is `LNDNodeConnection` (about 180 uses) and `fixture.GetLndNode(alias)` (about 116);
  - LND gRPC clients: `LightningClient` (87), `RouterClient` (11), `WalletKitClient` (3);
  - `LNUnitBuilder` with `AddBitcoinCoreNode`, `AddPolarLNDNode(alias, channels…, imageName: "custom_lnd")` and `Configuration.LNDNodes[..].Cmd` extra flags;
  - `LNDNodePool`, `RestartContainerAsync` / `RestartByAlias`, `BitcoinRpcClient`, `Destroy`, and the existing but unused `DockerNetworkId`.
- **In-house fixtures:**
  - `LightningRegtestNetworkFixture` (bitcoind `miner` + LND `alice/bob/carol/david`, channels pre-opened);
  - `ClnFixture`, `EclairFixture`, `LdkFixture`, each with its own bitcoind and network via `InteropChainHost`;
  - `PostgresFixture` / `SqlServerFixture`;
  - the gossip and on-chain fixtures under their collections.
- **In-house utilities:**
  - `ClnClient`, `EclairClient`, `LdkClient`, `LndTestHelpers`;
  - `ChainSync` (mine and wait until every node is at the tip), `Poll`;
  - `NLightningTestNode`, our node in-process, built from `AddNltgNodeServices`;
  - `RegtestBitcoinEndpoint`, `DockerDiagnostics`, `CrashableTcpService`, the raw recorders, `PortPoolUtil`.
- **Runners:**
  - `scripts/run-{onchain,gossip,abcd,interop}.sh`: build on the host, run the test dll in an SDK container with `--network host` (NL-276: the host process cannot reach the bridge IPs), N runs, logs under `TestResults/`;
  - the LND suite through the same SDK-container pattern by hand.
- **Images:**
  - `custom_lnd` (built from `test/Docker/custom_lnd`);
  - `nltg-eclair:0.14.3` and `nltg-ldk-server:<commit>` (built locally);
  - CLN `elementsproject/lightningd` pinned (v26.06.8);
  - bitcoind (polarlightning / bitcoin core).
- **Known pain, from the ledger:**
  - NL-262 (a restarted LND gets another IP);
  - NL-276 (host cannot reach the bridge);
  - NL-319 (LND lists a channel active before its router has the edge);
  - NL-505 (ABCD fixture funding race);
  - NL-469 (LND node not found in a full run);
  - NL-523 (LND keeps the old SCID after a splice);
  - NL-531 (CLN close after dual-fund in one process);
  - fee drift in CLN fixtures;
  - the single-process rule NL-276 / NL-359;
  - SQL Server under amd64 emulation (skipped by owner rule).

## 3. Requirements

### 3.1 Isolation and parallelism (the main goal)

- **R1, run id.** Every test process has a run id (`NLTG_TEST_RUN_ID`, generated if unset; lanes pass their slug). Everything the run creates is named and labelled with it:
  - networks `nltg-<run>-<topology>`;
  - containers `<run>-<role>`;
  - volumes, if any;
  - every object carries the label `nltg.run=<run>`, plus `nltg.suite` and `nltg.started`.
- **R2, private network per topology.** Each topology gets its own Docker network. Nodes keep **stable, plain aliases** on it (`miner`, `alice`, `cln`), so configs and peer addresses never contain the run id. The container name is unique; the alias is what the nodes use.
- **R3, cleanup by label, never by name.**
  - A fixture removes only its own run's objects.
  - A reaper (a library call plus a CLI) removes the objects of runs whose process is gone or which are older than a TTL.
  - Nothing ever touches unlabelled containers: `mutinynet-bitcoind`, `k8s_*`, other tools.
- **R4, concurrency.**
  - N suites run side by side. Start at 6 and tune it by measurement.
  - A host-wide semaphore replaces the single lock.
  - Each suite's resources (CPU and memory limits per container) are declared, so the semaphore can count weight, not just slots.
- **R5, ports.**
  - No fixed host ports anywhere.
  - Published ports are dynamic on `127.0.0.1` and recorded on the node handle.
  - Bridge IPs are reachable from the runner container (`--network host`) or, better, the runner joins the run's networks, which makes NL-276 go away.
- **R6, address pools.** Document and check (fail fast) the Docker `default-address-pools`: a large pool split into /24 per run network, so dozens of concurrent networks fit.

### 3.2 Topologies and nodes

- **R7, one model for all implementations.** Node kinds:
  - `BitcoinCore` (regtest, ZMQ, RPC);
  - `Lnd`, `Cln`, `Eclair`, `Ldk`;
  - `NLightning` (in-process, as today's `NLightningTestNode`, and later optionally as a container from our own image);
  - `Tor` (for the Tor interop proofs, NL-572);
  - `Postgres`, and `SqlServer` (kept but opt-in).
- **R8, a declarative topology.**
  - Nodes, their implementation and version, per-node flags (today's `Cmd.Add("--protocol.rbf-coop-close")`), channels to pre-open (capacity, push, public/private, anchors or legacy, dual-funded), wallet funding, and the chain backend to share.
  - Built once per test collection, like today's fixtures, or per test when a test needs a fresh world.
- **R9, a uniform Lightning facade** (`ILightningTestPeer`) over the per-implementation clients:
  - node id and address;
  - connect and disconnect;
  - open, close and force-close a channel;
  - list channels and peers;
  - create invoices, pay, keysend, send offers;
  - wait for a channel to be active and routable (this would absorb NL-319);
  - graph queries;
  - balance.

  The implementation-specific clients stay reachable for the tests that need them: LND gRPC (`Lightning`, `Router`, `WalletKit`, `Invoices`), CLN JSON-RPC, Eclair REST, LDK REST. Most cross-implementation tests (the CLN/LND/Eclair/LDK interop suites duplicate many flows) can then be written once and run per implementation.
- **R10, chain helpers.**
  - Mine to an address; mine and wait until every node in the topology has processed the tip (today's `ChainSync`); wait for a transaction in the mempool or in a block.
  - Reorg helpers: `invalidateblock` / `reconsiderblock`, competing-branch construction (as the on-chain smoke tests do).
  - Fee-rate control, which fixes the CLN fixture fee drift.
- **R11, lifecycle.**
  - Restart a node, keeping its IP: assign static IPs on the run network from the start, which removes the NL-262 address-hold containers.
  - Crash or kill it, pause and unpause it.
  - Restart the in-process NLightning node on the same database (today's `NLightningTestNode` restart semantics).
- **R12, images.**
  - Pinned by digest, built or pulled once per host before a parallel run, under an image lock (the only serialized step).
  - Build contexts stay in `test/Docker/`.
  - Versions are declared in one table (LND, CLN ≥ v26.06.7, Eclair 0.14.3, the ldk-server commit, bitcoind).

### 3.3 Runner and operations

- **R13, one runner.**
  - `scripts/run-docker.sh` (or a small .NET tool) replaces `run-onchain/gossip/abcd/interop.sh` and the hand-made LND runs.
  - It builds once, then runs a matrix of suites (by namespace, trait or class) with concurrency N.
  - Each suite runs in its own process with its own run id: an SDK container joined to its run's networks, or the host process once NL-276 is gone.
  - It also supports N repeated runs of one suite (today's `run-abcd.sh 3`), rerunning one failed class alone (the flake rule), and xunit v3 runner arguments passed through.
- **R14, results.**
  - Per-suite logs and xunit XML under `TestResults/<run>/`.
  - A summary table (totals, failures and the first error line of each).
  - Docker diagnostics captured on failure (container logs, `docker inspect`), as `DockerDiagnostics` does today.
- **R15, framework.** net10.0 by default; net11.0 selectable (the SDK image per framework, as `ONCHAIN_FRAMEWORK`/`ABCD_FRAMEWORK` do now).
- **R16, CI-optional.** It runs the same way locally and on a CI runner with Docker. The owner's current decision is local only (NL-180), so this is a property to keep, not a deliverable.

### 3.4 Non-goals

- A general-purpose Lightning testing product for outside use. This lives in the solution, for NLightning; LNUnit keeps that role for others.
- Replacing the in-process harnesses (`TwoNodeHarness`, `ThreeNodeSwitchTests`). They stay the fast proofs; Docker is the interop proof.

## 4. Proposed structure: Kubernetes-native

**Direction (owner, 2026-10-02):** build on Kubernetes, not on the Docker API.
- Docker.DotNet has gone stale, and the Docker-only route caps us at one machine.
- Locally: OrbStack's built-in cluster (k8s v1.35, node `orbstack`, Docker runtime).
  - Images built locally are usable without a registry (`imagePullPolicy: IfNotPresent` / `Never`).
  - Pod and service IPs are routable from the Mac.
- Elsewhere: any cluster (k3s, kind, k3d, a real multi-node cluster).
- Client: the official `KubernetesClient` NuGet package, maintained by the Kubernetes project.
- Prior art below: LNUnit PR #10.

**Prior art: [nbd-wtf/LNUnit#10](https://github.com/nbd-wtf/LNUnit/pull/10)** ("Basic Kubernetes backend support", open since 2025-11-25, +3,155 lines in 20 files). It has an `IContainerOrchestrator` with Docker and Kubernetes implementations, a 619-line in-cluster plan (`KUBERNETES_CLUSTER_SUPPORT.md`) and orchestrator tests.

- **Port** (into `Run/` and `Nodes/`):
  - from `KubernetesHelper`: `CreatePodAndWaitForRunning`, `CreateTestNamespace`/`DeleteNamespace` (random-suffix namespaces), `CreateService` (headless), `CreatePVC`;
  - exec-based file reads (`ExecAndReadTextFile`/`ExecAndReadBinaryFile`/`WaitForFileAndRead`, which is how LND's `tls.cert` and `admin.macaroon` come out of the pod);
  - the in-cluster config auto-detection (`KUBERNETES_SERVICE_HOST` → `InClusterConfig()`, else kubeconfig);
  - the RBAC manifest (ServiceAccount, Role, RoleBinding);
  - the plan's phases 3–8 (test-runner image, Job manifest, ConfigMap, namespace isolation, cleanup, CI);
  - its tests, as the spike's starting checks.
- **Do differently:**
  - **No Docker-shaped abstraction.** `IContainerOrchestrator` (networks, binds, links, start/stop) holds both backends to the lowest common denominator. Kubernetes-only lets the harness use StatefulSets, PVCs, quotas and NetworkPolicy directly.
  - **StatefulSets, not bare pods.** PR #10's restart deletes and recreates the pod from its spec, but its volumes are `emptyDir`, so **node data is lost on restart** and the pod IP changes. Our restart proofs (reestablish, data loss, mid-splice resume, `NLightningTestNode` restarts) need PVC-backed data and a stable name.
  - **Add** what it lacks:
    - CPU/memory requests and a ResourceQuota per namespace;
    - `nltg.run` labels and a reaper;
    - real readiness (LND: gRPC up, wallet unlocked, `synced_to_chain`; CLN: `getinfo`; bitcoind: RPC);
    - faults (pause, partition);
    - the image strategy;
    - CLN, Eclair, LDK, Tor, Postgres and NLightning nodes.

**How the requirements map:**

| Requirement | Kubernetes mechanism |
|---|---|
| R1 run id, R3 cleanup | One **namespace per run**: `nltg-<run>`, labelled `nltg.run`, `nltg.suite`, `nltg.started`. Cleanup deletes the namespace; the reaper deletes labelled namespaces whose run is gone or older than a TTL. Nothing outside `nltg-*` namespaces is ever touched. |
| R2 stable aliases | Each node is a **StatefulSet (replicas 1) + headless Service**. `alice`, `miner` and `cln` resolve inside the namespace, `alice-0.alice` is stable across restarts, and configs never contain the run id. |
| R4 concurrency | Each pod declares CPU and memory **requests and limits**, and each namespace gets a **ResourceQuota**. When the node is full, pods wait in `Pending`: the scheduler is the semaphore. The runner only caps how many runs it starts. |
| R5 ports | No host ports. Locally, the host-side test process reaches pod and service IPs directly (OrbStack routing). Anywhere else, the test process runs **in-cluster as a Job** in the run's namespace. NL-276 disappears either way. |
| R11 lifecycle | **Restart** = delete the pod: the StatefulSet recreates it with the same name, DNS and PVC data, so the NL-262 address-hold trick goes away. **Kill** = delete with a 1 s grace (grace 0 let the StatefulSet start the replacement while the old container still ran on the PVC; fixed at the spike integration). **Crash** = SIGKILL in place through exec (the container restarts in the same pod; needs `shareProcessNamespace` unless the node's process is not PID 1). **Pause** = `kill -STOP` via exec. **Network partition** = a NetworkPolicy isolating the pod: a real disconnect test without killing the node, which Docker could not do cleanly. |
| R12 images | One version table. Locally, `docker build` once (the cluster sees the image); multi-machine, push to a registry (an in-cluster registry or GHCR) under digests. |
| Node data | A PVC per StatefulSet (the local-path provisioner locally) for data that must survive restarts; `emptyDir` where it need not. |

**Project layout:** `test/NLightning.Testing.Cluster`, a library:
- `Run/`: namespace lifecycle, labels, quota, reaper, run-id plumbing;
- `Images/`: the version table, local build or registry push, digests;
- `Nodes/`: one manifest builder, readiness probe, client factory and `ILightningTestPeer` adapter per kind:
  - `BitcoinCore`, `Lnd`, `Cln`, `Eclair`, `Ldk`, `Tor`, `Postgres`;
  - `Nltg`: our daemon as a container image, needed for scale and multi-process realism;
  - in-process `NLightningTestNode` stays the default for small interop tests;
- `Topology/`: the declarative builder → manifests, plus the built `Topology` with node handles;
- `Chain/`: mining, sync, reorg and fee-control helpers against the topology's bitcoind;
- `Peers/`: the `ILightningTestPeer` facade (absorbs `ClnClient`, `EclairClient`, `LdkClient`, `LndTestHelpers`);
- `Faults/`: restart, kill, pause, partition, heal;
- `Diagnostics/`: on failure, `kubectl logs/describe` equivalents for every pod of the namespace into `TestResults/<run>/`.

**The LND client:** generate the gRPC clients from LND's protos (Grpc.Tools) inside the solution, pinned to the LND version. This is the part of LNUnit we really use.

## 4a. Scale: about 1,000 nodes across machines

The same topology model, sized up, on a multi-node cluster.

- **Our node as a container.** An `nltg` daemon image is required at scale; hundreds of in-process nodes do not fit in one test process. Use the AOT single binary (batch10 makes NativeAOT a supported target), configured by env/ConfigMap, with IPC reached by a sidecar or a port.
- **Chain backends.** A small number of bitcoind instances, one miner plus a few followers peered together; Lightning nodes are spread across them. Block production is driven by the test driver.
- **Topology generators:**
  - random, scale-free (Barabási–Albert) or hub-and-spoke graphs;
  - **mainnet graph snapshots replayed onto regtest**: our gossip probe already syncs the mainnet graph, so take its channel list, scale capacities and open the same shape;
  - a mix of implementations by ratio (e.g. 70 % LND, 20 % CLN, 10 % NLightning).
- **Channel bootstrap at scale:** batch funding transactions (one tx funding many wallets), opens throttled by a controller, gossip convergence measured instead of assumed.
- **Drivers as Jobs:** payment-load generators, fault injectors and assertions run in-cluster; results go to a volume or object storage.
- **Observability:**
  - our `Meter`s (gossip, onion messages, payments, accounting) exported through OpenTelemetry/Prometheus, scraped per namespace;
  - the time to converge the graph, the payment success rate, latency percentiles, per-node memory (gossip `MaxMemoryMb`) and accounting reconcile drift across the fleet.
- **Resources (rough):** LND ≈ 150–300 MB, CLN ≈ 100–200 MB, NLightning (AOT) to be measured, bitcoind ≈ 300 MB–1 GB each. A 1,000-node network is ≈ 150–300 GB of RAM plus the backends: a multi-machine cluster (this Mac alone tops out around 250–350 nodes at the default requests).
- **Uses:**
  - gossip at scale (the G5 limits, relay backlog, query sync against hundreds of peers);
  - pathfinding and mission control on a realistic graph;
  - MPP and route blinding at depth;
  - onion message rate limits;
  - splice and dual-fund behaviour among many peers;
  - BOLT 5 under mass force-closes (a fee spike plus many commitments);
  - accounting reconcile over long runs;
  - soak tests (the 24 h gossip soaks moved off mainnet).

## 5. Migration plan (each phase keeps the suites green)

1. **Spike (≈1 day), starting from LNUnit PR #10's `KubernetesHelper` and tests.** One CLN topology and one LND topology in namespaces, each run 3 times concurrently on OrbStack. It must prove:
   1. host ↔ pod reachability both ways (LND dialling back to the in-process node; else run the test process as a Job);
   2. a restart keeps the DNS name and PVC data;
   3. local images work without a registry;
   4. startup time compared with today's Docker fixtures.
2. **Core (≈2–3 days).**
   - Namespaces, labels, quota, the reaper, images, `BitcoinCore`, `Cln`, the topology builder, chain helpers.
   - Port the CLN fixture and prove the CLN suite running 3 times concurrently, green.
3. **LND (≈3–4 days).**
   - `Lnd` nodes, the generated gRPC clients, the regtest topology (miner + alice/bob/carol/david, pre-opened channels).
   - Port `LightningRegtestNetworkFixture` and move the 47 LNUnit files behind an adapter that keeps the member names they use (`GetLndNode`, `LightningClient`...).
   - Remove the `lnunit` package.
4. **Eclair, LDK, Tor, Postgres, faults (≈2–3 days).** Port the remaining fixtures; Tor closes NL-572; NetworkPolicy partition tests are new coverage.
5. **Runner (≈1–2 days).**
   - `run-cluster` replaces `run-{onchain,gossip,abcd,interop}.sh` and the hand-made LND runs.
   - It builds once, then runs the suite matrix with N runs in flight, reruns one failed class alone, prints a summary and collects diagnostics.
   - Update `test/CLAUDE.md` and root `CLAUDE.md`.
6. **Proof.**
   - The full matrix twice concurrently, then at the tuned N, green apart from documented flakes.
   - Wall time compared with today's serial pass (target ≈15 min instead of ≈75).
   - NL-262 and NL-276 closed.
7. **The `nltg` daemon image and scale (later, its own plan).**
   - The container image of our node, the generators, the drivers, the metrics.
   - A first 100-node run on OrbStack, then a multi-machine cluster for 1,000.
8. **Facade convergence (incremental).** Rewrite duplicated interop flows once against `ILightningTestPeer` and run them per implementation, which raises Eclair/LDK coverage (NL-554, NL-556).

### Spike check 1 record: host ↔ pod reachability and the in-cluster Job (2026-10-02, branch `harness-reach`)

Measured on OrbStack (k8s v1.35.6+orb1, one node at 192.168.139.2, Docker runtime) by `ReachabilityCheck`
(`test/NLightning.Testing.Cluster/Reach/`): a busybox echo node (`tcpsvd`) plus a ClusterIP Service, probed from the
host test process, and a TCP listener in the host test process probed from the pod with busybox `nc`. Live tests:
`Live/ReachabilityTests` (one run, then 3 runs at once), `Live/InClusterRunnerTests`.

**Host → cluster** (all from the macOS test process, no port forwarding):

| Target | Result |
|---|---|
| Pod IP (192.168.194.x) | works, 2-3 ms; a pod that just turned ready answered only on the 3rd attempt (about 2 s) once, while 3 runs started at once |
| `<pod>.<svc>.<ns>.svc.cluster.local`, `<svc>.<ns>.svc.cluster.local` (headless) | resolve on the Mac to the pod IP and work, 50-200 ms with the lookup |
| ClusterIP and its `svc.cluster.local` name | work, but only **4-9 s after the Service is created** (kube-proxy's sync; pods see the same delay, so it is not a host limit). Headless Services do not have it, which is one more reason the harness uses them |
| `<svc>.<ns>.k8s.orb.local` | resolves (OrbStack's ingress address 192.168.138.3) but is refused: only LoadBalancer/ingress objects use it |

**Pod → host** (the LND/CLN dial-back to the in-process NLightning node):

| Pod dials | Listener on 127.0.0.1 | Listener on 0.0.0.0 | Peer address the listener sees |
|---|---|---|---|
| `host.orb.internal` (cluster DNS → 0.250.250.254) | works, 30-60 ms | works | 127.0.0.1 |
| `host.docker.internal` (same address) | works | works | 127.0.0.1 |
| The Mac's LAN IP (192.168.1.173) | refused | works | the LAN IP |
| The Mac's OrbStack bridge IP (192.168.139.3) | refused | works | the node IP 192.168.139.2 |
| The node IP (192.168.139.2), the Mac's 192.168.194.0 / 192.168.97.0 | refused / 3 s timeout | refused / 3 s timeout | — |

Conclusions:
- On OrbStack the test process stays on the host. In-process nodes listen on **loopback** and are announced to the
  peers as `host.orb.internal:<port>` (`HostEndpoints.ForPods`, `NLTG_HOST_ADDRESS` overrides it); nothing is exposed
  on the LAN.
- OrbStack forwards `host.orb.internal` to the Mac's loopback, so **our node sees every pod peer as 127.0.0.1**. A
  loopback peer is saved `IsInboundOnly` without an address (NL-497), so tests where LND/CLN connect to us must still
  have our node dial out (or reconnect) to them. Their addresses are stable DNS names, so that works.
- Elsewhere (kind, k3d, a real cluster) none of this holds. The fallback is proven: `InClusterTestRunner` runs the
  test assembly as a Job in the run's namespace. The pieces are the image `nltg-spike-runner` (`Runner/image/build.sh`:
  the SDK 10.0 image plus `bin/Release/net10.0`, built in about 2 s from a build) and a namespaced
  ServiceAccount/Role/RoleBinding (`RunnerRbac`, ported from PR #10 and narrowed). The tests adopt the namespace that
  the host created (`NLTG_ADOPT_NAMESPACE=1`): they do not create or delete namespaces, and on dispose they remove only
  their own nodes.
- Job proof: the scaffold's namespace smoke test ran in the Job. The runner pod was running 1.8 s after the Job was
  created. Inside it the run adopted the namespace (`InCluster` config), deployed the busybox StatefulSet (ready in
  7.3 s), removed it, and exited 0 after 11.9 s. The whole host-side test took 26 s, including namespace creation and
  deletion. A bad runner argument returned exit 3, and the Job ended Failed with no retry.
- Access reviews for the runner's ServiceAccount:
  - allowed: StatefulSets and pod exec in its namespace, and `get` on its own namespace object;
  - denied: creating, listing or deleting namespaces, reading `default`, anything in `default` or `kube-system`,
    creating Roles, and deleting the ResourceQuota.

### Spike integration record (2026-10-02, branch `wip/harness-spike`)

The six lanes (chain, cln, lnd, faults, runner, reach) were merged onto the scaffold in that order and unified:

- **One bitcoind.** The CLN lane's stopgap `TopologyBitcoind` and the LND lane's `LndTopologyChain` were removed. Every
  topology now runs the chain lane's `BitcoinCoreNode` + `RegtestChain` through `Topology/BitcoinCoreTopologyChain`
  (the default `TopologyBuilder.ChainFactory`; `LndPairTopology` uses it too). `ITopologyChain` gained the ZMQ raw
  block/tx ports, and `LndNodeOptions` defaults follow the shared bitcoind (`nltg`, ZMQ 28332/28333).
- **LND in the declarative topology.** `LndNode` is an `ITopologyLightningNode` and `LndNodeDeployer` is registered
  by default next to the CLN one (`TopologyBuilder.AddLnd`), so LND and CLN mix in one topology.
- **One poll helper.** `ChainPoll`, `TopologyPoll` and the LND lane's private wait loops became the library's `Poll`;
  `BitcoinCoreNode` probes its RPC port with the reach lane's `TcpProbe`.
- **`KillAsync` no longer overlaps two processes on a PVC** (found by the CLN and faults lanes): a grace-0 delete
  removed the pod object at once, so the StatefulSet started the replacement while the old container still ran; CLN
  then refused to start on its PID file. It is now a 1 s grace; the live fault test asserts the old pod's SIGTERM
  comes before the new pod's start.
- `TestRun.StartAsync` does both the runner lane's owner records, derived ids and admission cap and the reach lane's
  namespace adoption (an adopted run skips the cap: the host holds the slot). An adopted run's disposal also removes
  its nodes' `StableNodeAddress` Services.

Live evidence on OrbStack after the integration (every `Category=Cluster` class, all green; machine shared with the
batch Docker suites):

| What | Result |
|---|---|
| CLN pair (`ClnTopologyTests`): build, pay | built in 25.2 s, paid in 0.2 s; 3 concurrent pairs built in 22-32 s, each deleted by 86-91 s |
| CLN crash and restarts | channel active again 1.2 s after bob's lightningd crash, 5.9 s after bob's restart (new pod IP) |
| LND pair (`LndPairTopologyTests`) | built in 25.7 s (bitcoind 13.3 s, LND 10.2 s, funding 0.9 s, channel 0.9 s); restart to channel active 5.3 s, kill 6.2 s; torn down in 20 s |
| LND + CLN in one topology (`MixedTopologyTests`) | built in 25.3 s with a channel each way; both payments green; `scripts/run-cluster.sh -n 3` ran it 3 times at once: 55-83 s each, all green, nothing left behind |
| Faults (`FaultInjectorClusterTests`, `BitcoindFaultClusterTests`) | restart 4.0 s, kill 4.0 s (TERM before the new START), crash 1.6 s in place; pause, partitions and the shared bitcoind workload with `WithProcessFaults()` green |
| Runner (`RunLifecycleTests`, `InClusterRunnerTests`, `ReachabilityTests`, `ClusterSmokeTests`, `BitcoinCoreClusterTests`) | green; the runner image rebuilt from the integrated tests |

Spike checks: (1) reachability, see the record above; (2) a restart keeps the DNS name and the PVC data (faults, CLN
and LND lanes); (3) local images work without a registry (`custom_lnd:latest` and `nltg-spike-runner` with
`imagePullPolicy: Never`, CLN and bitcoind by digest); (4) a 2-node topology with a channel is up in about 25 s. The
Docker fixtures' times for the same topologies were not measured in the spike (the batch Docker suites held the
machine); that comparison is left to the core phase.

## 6. Risks and open questions

- **Timing flakes under load.** Six suites mining and paying at once on one VM raise the risk. Mitigations: per-container CPU and memory limits, readiness waits that check real state (graph edge present, not just "channel active"), the flake rule, and N tuned down if needed.
- **LND's behaviour around restarts and SCID changes** (NL-262, NL-523). Static IPs fix the first; the second stays a peer quirk.
- **Image drift.** Pinning by digest plus one version table; an explicit command to refresh it.
- **The in-process NLightning node:** it reaches bitcoind ZMQ/RPC and the peers through pod/service IPs (OrbStack) or by running in-cluster; peers dialling back need host reachability from pods (spike item 1).
- **Open: keep LNUnit alive for outside users?** Not this project's concern; NLightning stops depending on it.
- **Our own node in a container:** required for scale (§4a); optional per topology for small tests (in-process stays the default for fast iteration).
- **OrbStack specifics.** Pod-IP routing to the Mac and the shared image store are OrbStack conveniences. On kind/k3d/real clusters, plan on in-cluster test Jobs and a registry from the start, so the harness does not quietly depend on OrbStack.
- **Kubernetes overhead on small tests.** Pod scheduling plus readiness should be seconds locally. If a tiny suite gets slower than with Docker, keep a topology warm per collection (as today's fixtures do), not per test.
- **Cluster-wide state.** Namespaces isolate names and DNS; cluster-scoped objects (StorageClass, PriorityClass) are shared and created once, never per run.
