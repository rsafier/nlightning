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
   - Done apart from the package removal ("Phase 3 completion, phase 5 and phase 6 record"): the fixture delegates to
     `DockerLndBackend` or `ClusterLndBackend`, and LNUnit is confined to the Docker backend. The removal is NL-820, an owner
     decision: re-implement the Docker backend, or retire it.
   - **Prepared: in-tree LND client (wip/lnd-grpc).** The generated clients, `LndNodeConnection` and `LndNodePool`
     exist and are proven against LND 0.21.4; the swap is a `using`/type rename (record below).
4. **Eclair, LDK, Tor, Postgres, faults (≈2–3 days).** Port the remaining fixtures; Tor closes NL-572; NetworkPolicy partition tests are new coverage.
   Done apart from Tor ("Phase 3/4 record"); phase 3's fixture wiring is done ("Phase 3 completion record"); the `lnunit` removal remains.
5. **Runner (≈1–2 days).**
   - `run-cluster` replaces `run-{onchain,gossip,abcd,interop}.sh` and the hand-made LND runs.
   - It builds once, then runs the suite matrix with N runs in flight, reruns one failed class alone, prints a summary and collects diagnostics.
   - Update `test/CLAUDE.md` and root `CLAUDE.md`.
   - Done ("Phase 5 runner record", "Phase 3/5/6 integration record"): `scripts/run-cluster.sh --matrix`; `lnd` runs in
     it, and so do `abcd`, `onchain` and `anchors` since their phase 6 proofs; a suite whose proof is pending runs when
     named.
6. **Proof.**
   - The full matrix twice concurrently, then at the tuned N, green apart from documented flakes.
   - Wall time compared with today's serial pass (target ≈15 min instead of ≈75).
   - NL-262 and NL-276 closed.
   - Done ("Phase 6 proof record"): green at 6 namespaces in 18 min (1,088 s) against about 72 min of the same suites
     one at a time on Docker; twice at once green (two flakes rerun green, both explained); the 15 min target needs
     about 7 namespaces (NL-844); NL-262 and NL-276 moot on the cluster backend.
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

Spike checks: see the "Spike checks record" below (all four checks and the concurrency proof, re-run on the
integrated branch).

### Spike checks record (2026-10-02, integrated `wip/harness-spike` at 2a53cb14 plus `Live/LightningDialBackTests`)

Re-run of the plan's checks on the integrated branch, OrbStack (k8s v1.35.6+orb1, one node, 28 CPUs, 64 GiB, no
metrics-server). The machine was shared with the batch Docker suites (CLN, then Eclair interop) the whole time; the
harness never touched their containers or images. Release build, net10.0; logs and xunit XML under
`TestResults/spike-checks/` and `TestResults/cluster/chk5*/`.

| Check | Evidence | Result |
|---|---|---|
| 1. Host ↔ pod both ways | `ReachabilityTests` (2) + `InClusterRunnerTests` (3): 5/5 green in 87 s. Host → pod IP 2 ms, pod DNS 123 ms, headless Service 129 ms, ClusterIP 1005 ms (routed 5.8 s after the Service was created); `k8s.orb.local` refused. Pod → `host.orb.internal` / `host.docker.internal` OK to a loopback listener (31-104 ms, listener sees 127.0.0.1); node and bridge IPs refused. 3 runs at once: 51.3 s, each its own pods. Job runner: pod running 2.3 s after the Job, exit 0 after 12.7 s; bad argument → non-zero exit; RBAC confined to the run's namespace | green |
| 1b. Real LND/CLN dial-back | New `LightningDialBackTests`: LND and CLN in pods `connect` to `<G>@host.orb.internal:<port>`, a listener bound to 127.0.0.1 in the test process receives each one's BOLT 8 act one (50 bytes, `00 03 ...`) in 253 ms (LND) and 252 ms (CLN), peer seen as 127.0.0.1 | green: **decision: host-side tests on OrbStack** (in-process node on loopback, announced as `host.orb.internal`, our node dials out because peers show up as 127.0.0.1, NL-497); the in-cluster Job is the proven fallback for other clusters |
| 2. Restart keeps DNS name and PVC data | 8 tests, 8/8 green in 156 s (5 classes in parallel). LND pair (real bitcoind + 2 LND): restart bob → channel active 5.6 s, kill alice → 6.2 s, new pod UIDs and IPs, same node ids, same funding txid and scid 103x1x0, bob's balance 29,000,000 msat after 3 payments. CLN pair: crash in place (same IP) → channel active 1.8 s; bob restart 9.6 s, IP .92 → .103, active 0.9 s later; alice restart 6.5 s; same `hsm_secret` ids, funding txid, 4 payments in bob's balance. Busybox node: restart 3.5 s, kill 4.0 s, crash 2.1 s, DNS follows the pod at +0.0 s, data file intact, TERM before the new START. bitcoind: restart 2.0 s, crash 2.3 s, height kept (101) | green |
| 3. Local images, no registry | Pod specs and `imageID`s read during the concurrent batch: LND `custom_lnd:latest`, `imagePullPolicy: Never`, `sha256:9347d265...` (kubelet event "already present on machine"); CLN `elementsproject/lightningd:v26.06.8@sha256:56f1cebe...` and bitcoind `polarlightning/bitcoind:29.0@sha256:4521294a...`, `IfNotPresent`, served from the local store; runner `nltg-spike-runner:latest` `Never` | green |
| 4. Startup time | See the table below | k8s ≈ 21-22 s alone, 25-44 s with 6 at once; Docker fixtures ≈ 4-11 s, serial only |
| 5. Concurrency | `scripts/run-cluster.sh -n 3 --method '*ClnTopologyTests.Given_BitcoindAndTwoClnNodes*' --method '*LndPairTopologyTests.*'`, three batches (`chk5`, `chk5b`, `chk5c`): each 3 processes × (CLN pair + LND pair) = **6 namespaces at once** (watcher peak 6), 18/18 tests green, batch wall 112 s / 104 s / 110 s, runs 67-110 s each. No cross-talk: same aliases in every namespace, every chain independent (every CLN channel 108x1x0, every LND channel 103x1x0), each payment to its own invoice. Cleanup: every run deleted its own namespaces (reaper after the batch: 0 to delete, `nltg-cluster list`: none left). Resources (`docker stats` of the spike's `k8s_*` containers, 0.5 s frames, `chk5c`): peak 18 containers, CPU peak 2.28 cores (p95 1.57, mean 0.27), memory peak 1,173 MiB (mean 424 MiB) | green |

Startup, wall time to ready (topology built = chain ready, nodes at the tip, wallets funded, channel active on both
ends):

| Topology | Alone | 6 topologies at once (3 batches) | Phases (alone) |
|---|---|---|---|
| bitcoind + 2 CLN + 1 channel (k8s) | 21.9 s (integration record: 25.2 s) | 30.3, 35.3, 40.1, 30.9, 41.4, 35.9, 43.8, 33.7, 33.0 s | chain 9.0 s, 2 CLN at the tip +5.6 s, funded +0.8 s, channel active +5.4 s |
| bitcoind + 2 LND + 1 channel (k8s) | 20.8 s (integration record: 25.7 s) | 26.0, 32.4, 41.5, 41.2, 25.1, 30.7, 40.1, 28.7, 24.7 s | bitcoind 8.3 s, LND 10.2 s, funding 0.8 s, channel 0.7 s |
| Docker `LightningRegtestNetworkFixture` (bitcoind + 4 LND + 4 channels, LNUnit) | 9.5-11.4 s | n/a (fixed container names: one per machine) | xunit XML `w4int-*.xml` (2026-09-26, 6 runs): assembly start to the `regtest` collection's first test |
| Docker `ClnFixture` (bitcoind + 1 CLN, no channel) | 3.4-6.8 s | n/a | log file birth to the first test's node log line, 7 CLN suite logs (`lh1`, `sp1`, `spr`) |

Existing evidence only (the Docker suites were not run): the LND suite logs give 10.6-26.7 s from the process start to
the first test's node log (`lh1`, `sp1`, `b12`, `spr`, `m6`), consistent with the XML. Teardown is the larger cost
under load: deleting a run namespace took 19-20 s at best (integration record: 20 s alone) and up to 62 s with 6 at
once (graceful stops, the CLN drain, PVC removal by the local-path provisioner).

Where the k8s time goes (events of a kept solo run): each StatefulSet wave waits about 6 s before its container starts
(PVC `WaitForFirstConsumer`, one `FailedScheduling` "Operation cannot be fulfilled on persistentvolumeclaims" retry of
about 2 s, local-path provisioning about 3 s), and the topology has two waves (bitcoind, then the Lightning nodes): about
12 of the 21 s. bitcoind is ready about 2 s after its container starts, CLN about 1 s, LND about 4 s. Follow-ups
for the core phase: start the Lightning nodes' StatefulSets together with bitcoind (LND and CLN wait for bitcoind themselves),
keep a topology warm per collection as the fixtures do, and delete run namespaces in the background (the reaper owns
cleanup) instead of awaiting the deletion in each test.

### Spike results (2026-10-02)

Summary of the spike on `wip/harness-spike` (stacked on `wip/fafo` at 44aeaf5f; three new projects
`test/NLightning.Testing.Cluster`, `.Cli` and `.Tests`, about 17,700 added lines, nothing in `src/` changed). The
detailed evidence is in the three records above; this section is the verdict.

**The checks (all on OrbStack k8s v1.35.6+orb1, one node, shared with the batch Docker suites the whole time):**

| # | Check | Result | Evidence (numbers) |
|---|---|---|---|
| 1 | Host ↔ pod reachability, both ways | **pass** | Host → pod IP 2 ms, headless Service DNS 123-129 ms; ClusterIP routed only 4-9 s after creation (so nodes are addressed by headless Service). Pod → `host.orb.internal` reaches a listener on the Mac's loopback in 31-104 ms. Real LND and CLN in pods dial the in-process listener: BOLT 8 act one (50 bytes) received in 253 ms / 252 ms (`Live/LightningDialBackTests`). In-cluster Job runner: pod running 2.3 s after the Job, exit code propagated, RBAC confined to the run's namespace |
| 2 | A restart keeps the DNS name and PVC data | **pass** | 8/8 live tests. LND pair: restart → channel active 5.6 s, kill → 6.2 s, same node ids, funding txid and scid, balance kept across 3 payments. CLN pair: crash in place → active 1.8 s, restart (new pod IP) 9.6 s → active 0.9 s later. bitcoind: restart 2.0 s, crash 2.3 s, height kept. DNS follows the new pod at +0.0 s |
| 3 | Local images without a registry | **pass** | `custom_lnd:latest` with `imagePullPolicy: Never` ("already present on machine"), CLN v26.06.8 and bitcoind 29.0 pinned by digest with `IfNotPresent` from the local store, `nltg-spike-runner:latest` with `Never` |
| 4 | Startup compared with today's Docker fixtures | **pass, slower per topology** | k8s bitcoind + 2 nodes + 1 channel: 20.8-21.9 s alone, 24.7-43.8 s with 6 at once. Docker: LNUnit regtest fixture (4 LND, 4 channels) 9.5-11.4 s, `ClnFixture` 3.4-6.8 s, but one at a time per machine. About 12 s of the k8s 21 s is two StatefulSet waves each waiting ~6 s for PVC binding and local-path provisioning. Teardown 19-62 s per namespace |
| 5 | Concurrency (the goal) | **pass** | `scripts/run-cluster.sh -n 3` with a CLN pair and an LND pair per run: 6 namespaces at once, 3 batches, 18/18 green, 104-112 s per batch; independent chains (all CLN channels 108x1x0, all LND 103x1x0), no cross-talk, every namespace removed by its own run (reaper found 0). Peak 18 containers, 2.28 CPU cores, 1,173 MiB |
| — | Faults (beyond the plan's checks) | **pass** | Restart, kill (1 s grace; grace 0 overlapped two processes on one PVC, fixed), crash in place, pause/resume (SIGSTOP/SIGCONT, 0.1 s), NetworkPolicy partitions and heal (two bitcoinds: peer held at 103 while the miner reached 108, caught up 0.4 s after the heal) |

**Decisions taken:**

- **Host-side vs in-cluster.** On OrbStack the test process stays on the host: in-process NLightning nodes listen on
  loopback and are announced to peers as `host.orb.internal:<port>` (`HostEndpoints.ForPods`, `NLTG_HOST_ADDRESS`
  overrides). Pods appear to our node as 127.0.0.1, so a pod peer that connects to us is saved inbound-only
  (NL-497): tests have our node dial out to the peers (their addresses are stable DNS names). On any other cluster
  (kind, k3d, multi-machine) the test assembly runs as a Job in the run's namespace (`InClusterTestRunner`,
  `nltg-spike-runner` image, namespaced RBAC, `NLTG_ADOPT_NAMESPACE=1`); this path is proven, not just planned.
- **NetworkPolicy on OrbStack: supported.** OrbStack's k3s runs kube-router's policy controller (flannel host-gw), and
  a policy is enforced for **new** connections in both directions (pod-to-pod dropped, host-to-pod refused) within
  about 1 s. **Established TCP connections survive** (conntrack), so a partition test must also disconnect the peers
  (a node command or a restart) after applying it. DNS stays reachable by default; the runner's CIDR
  (`NLTG_RUNNER_CIDRS`, `192.168.194.0/32` for the OrbStack host) can be let in so an isolated node is still driven.
  Clusters without a policy controller (plain kind) need Calico/Cilium for partition tests; the harness should check
  and skip with a clear message there.
- **Image strategy.** One version table (`Images/ImageVersions`). Public images (bitcoind, CLN, postgres, busybox) are
  pinned by digest with `IfNotPresent`; images we build (`custom_lnd`, `nltg-eclair`, `nltg-ldk-server`, the runner)
  are used by tag with `Never` locally, because OrbStack's cluster shares the Docker image store. No registry
  locally. Multi-machine clusters push to a registry (in-cluster or GHCR) under digests and switch the policy to
  `IfNotPresent`; the runner image takes `NLTG_RUNNER_IMAGE` for that. The spike built only `nltg-spike-runner`.
- **Shape.** StatefulSet (1 replica) + headless Service + PVC per node, one namespace per run with labels, owner
  annotations, a ResourceQuota sized from the pod specs, an admission cap (`NLTG_MAX_CONCURRENT_RUNS`, default 6) and
  a reaper (`nltg-cluster reap`). LND uses LNUnit.LND's generated gRPC clients for now with the server certificate
  pinned (the plan's own Grpc.Tools generation is still to do).

**Not done in the spike:** the Eclair, LDK, Tor, Postgres and `nltg` (container) node kinds; no existing suite is
ported (the Docker suites and LNUnit are untouched and remain the CI/local path); in-process `NLightningTestNode`
against a cluster topology is not wired yet (only the raw dial-back is proven); the LND gRPC clients still come from
the `LNUnit.LND` package; diagnostics collection on failure (`kubectl logs/describe` per pod) is missing; the runner
is a spike runner (`run-cluster.sh`), not the suite-matrix runner of step 5.

**Revised next steps and estimates** (the spike delivered much of the core and LND phases' plumbing, so those
shrink; the porting of fixtures and suites is the real remaining cost):

1. **Startup cut (≈0.5 day).** Deploy the Lightning nodes' StatefulSets together with bitcoind (they wait for it
   themselves), keep a topology warm per xunit collection, delete namespaces in the background and let the reaper
   finish. Target: ≤ 12 s to a ready pair alone. Optionally a pre-created StorageClass `nltg-spike-*` with
   `Immediate` binding to skip the `WaitForFirstConsumer` retry.
2. **Core + CLN suite port (≈2 days, was 2-3).** Wire `NLightningTestNode` into a topology (bitcoind RPC/ZMQ by pod
   address, dial-out to peers), port `ClnFixture`, prove the CLN interop suite 3 times concurrently, green; add
   failure diagnostics.
3. **LND regtest port (≈3 days, was 3-4).** The miner + alice/bob/carol/david topology with pre-opened channels,
   an adapter keeping the member names the 47 LNUnit-based files use, then generate the gRPC clients in-tree and
   drop `lnunit`/`LNUnit.LND`.
4. **Eclair, LDK, Tor, Postgres and partition tests (≈2-3 days, unchanged).**
5. **Matrix runner (≈1 day, was 1-2).** Grow `run-cluster.sh` into the suite matrix with rerun-alone and summary.
6. **Proof (≈1 day).** The full matrix twice concurrently, then at the tuned N; wall time against today's serial
   ≈75 min; close NL-262 and NL-276.

Total to replace the Docker suites: about 9.5-10.5 working days (the plan's §5 had 9-13 plus the spike).

### Phase 2 lane C record: startup and teardown cuts (2026-10-02, branch `hp2-startup` from b379b779)

What changed in `test/NLightning.Testing.Cluster` (details in its `CLAUDE.md` and `Topology/CLAUDE.md`):

- **One wave.** The Lightning nodes' StatefulSets are applied together with bitcoind's
  (`ILightningNodeDeployer.DeploysWithChain`, true for CLN and LND; `TopologyBuilder.DeployNodesWithChain`, default
  on; `LndPairTopology.Settings.DeployNodesWithChain`). They know the chain from `ITopologyChainEndpoint` (host, ports,
  credentials) before it is up, and each pod waits for bitcoind in an init container from bitcoind's own image
  (`BitcoinCoreWorkload.StartupWaitContainer`: `getblockchaininfo` every 0.2 s, starts anyway after 180 s). Checked
  without it: **LND exits at once** when bitcoind does not answer ("unable to create partial chain control: lookup ...
  no such host", pod `Failed`), **CLN after 30 s** ("The Bitcoin backend died").
- **No PVC where none is needed.** `NodeStorage.Ephemeral` (an `emptyDir`; `TopologyBuilder.Storage`, per node, or
  `NLTG_NODE_STORAGE`) for nodes a test never restarts or kills (`KubeNodeHandle` refuses both); PVC stays the default
  and restart tests pin it. A pre-bound volume is not possible within the rules (a PV and an `Immediate`
  StorageClass are cluster-scoped), and the provisioning, not the binding, is the cost: 6 s alone, 12-15 s with other
  runs (events of kept runs: `ProvisioningSucceeded` 12-15 s after the claim).
- **Fast maturity.** The 101 maturity blocks: 1 to the wallet, 100 to `BitcoinCoreTopologyChain.BurnAddress` (P2WSH of
  `OP_RETURN`). 100 blocks to the wallet took 4.2 s, to a foreign address 0.12 s (same spendable 50 BTC at 101).
- **No early DNS miss.** CoreDNS caches NXDOMAIN for 5 s (a Service created right after a miss resolved 5.0 s after the
  miss). On `emptyDir` the wave waits (up to 5 s, `ChainAddressWait`) until the chain's Service has an address before
  the nodes start (`KubernetesHelper.WaitForServiceAddressAsync`); before that, the startup wait logged "answers after
  5 s, 24 failed call(s)". On a PVC it does not wait, so a node may lose up to 5 s to the cache.
- **Teardown.** `TestRun.DisposeAsync` stops the pods first (StatefulSets deleted with `Orphan`, pods with a 1 s grace,
  `TeardownGracePeriodSeconds`, wait until none runs), then deletes the namespace and returns
  (`WaitForDeletion` default false). Pods first because the namespace controller, finding unfinished pods, waits for
  their largest spec grace (bitcoind 30 s) before it looks again. The admission cap keeps counting terminating
  namespaces (`RunAdmission.HoldsSlot`, unit test `Given_ATerminatingRunNamespace_When_Counted_Then_ItStillHoldsASlot`),
  so a batch never has more than the cap's namespaces alive, terminating ones included; their slot frees when they
  are gone.
- **Warm topology.** `ClusterTopologyFixture<TDefinition>` (`IClusterTopologyDefinition`) builds once per xunit
  collection and resets nothing; isolation expectations in its XML docs and `Topology/CLAUDE.md`.
- Found on the way: an LND node of a declarative topology could be "at the tip" before its wallet had synced and
  then refuse the open ("channels cannot be created before the wallet is fully synced"); `LndNode.GetBlockHeightAsync`
  now counts the height only once `synced_to_chain`.

Evidence (OrbStack k8s v1.35.6+orb1, Release, net10.0; machine shared with lanes A, B, D and the Docker batch the whole
time, so the "other harness namespaces" column matters; logs under `TestResults/cluster/<batch>/`). Built = from the
namespace to the channel active on both ends.

| Topology | Before (b379b779, cluster otherwise empty) | After: one wave, PVC (default) | After: one wave, `emptyDir` |
|---|---|---|---|
| CLN pair alone (`ClnTopologyTests` build) | 25.5, 22.1, 22.2 s | 19.5, 26.0, 18.2, 20.5, 20.9 s (43.7 s once with 6 other namespaces) | 6.1 s (`f1-cln`), 10.0 s (`t2-cln`) |
| LND pair alone (`LndPairTopologyTests`) | 31.8, 28.8, 21.3 s | 14.8, 25.1, 24.6, 22.1, 17.5 s | 8.6 s (`f1-lnd`, builder pair) |
| Same run, rows side by side (`StartupTiming*Tests`, spike's two waves / one wave PVC / one wave `emptyDir`) | — | CLN 21.7 / 15.4 / 6.1 s, LND 18.0 / 17.7 / 8.6 s (`f1`); CLN 26.7 / 19.7 / 10.0 s (`t2`) | |
| 6 at once, CLN + LND pairs (3 × 2) | CLN 27.7, 42.0, 32.4 s; LND 43.6, 37.6, 30.7 s | CLN 36.9, 41.6, 31.8 / 43.0, 27.0, 52.5 s; LND 39.8, 27.4, 43.9 / 39.2, 29.9, 44.3 s (`b-six`, `f2-six`): PVC provisioning is the bottleneck at this concurrency | 6 CLN pairs at once: 13.2, 19.5, 13.2, 19.4, 13.8, 13.5 s (`g6-cln`) |
| 6 at once, rows (`d-six`, build without the slot wait; two waves / one wave PVC / `emptyDir`) | — | CLN 41.8, 37.2, 31.6 / 19.6, 14.0, 19.1 s; LND 26.1, 46.9, 38.7 / 17.4, 20.0, 17.0 s | CLN 9.6, 11.5, 7.9 s; LND 7.7, 9.3, 7.9 s |
| Disposal as the test sees it | CLN 40.3-41.4 s, LND 16.1-29.7 s alone; 56-84 s 6 at once | 3.4-4.4 s (CLN: the 1 s grace plus the kubelet's 2 s minimum), 0.6-1.5 s (LND) | same |
| Namespace gone after the delete (background) | = the disposal | 14-21 s alone (before the pods-first stop); 22-57 s 6 at once (`f2-six`) | 13 s alone (`w4`); 13-44 s with 6 at once (`g6-cln`) |

Readings:
- Target "≤ 12 s to a ready pair alone": met on `emptyDir` (6.1 s CLN, 8.6 s LND; the warm fixture's start incl.
  namespace 11.1-13.8 s). On PVCs the one wave saves 5-10 s alone, and about half the build at 6 at once, but the
  local-path provisioner then dominates both start and teardown.
- With 6 at once the namespace controller is the teardown limit (no pods or PVCs left and still 13-44 s); with the cap
  counting terminating namespaces, back-to-back batches wait for it (`d2-six`: up to 157 s in admission). Faster
  turnover needs fewer namespaces per test (the warm fixture) more than faster deletion.
- Warm topology (`TopologyFixtureTests`, CLN pair on `emptyDir`): started once in 11.1-13.8 s, each test 0.2 s, one
  namespace for both tests.
- All `Category=Cluster` tests on the final code (`full1`, every class at once, 32 tests, many runs over the cap): 29
  green; 3 failed under that load and passed alone (`re-*`): the CLN pair's graceful restart (channel not active again
  within 60 s), the reaper test (its live run waited for a slot past 30 s, and the admission's own reap removed the
  orphan first) and the partition baseline connect. Unit tests 466/466.

### Phase 2 lane D record: failure diagnostics (2026-10-02, branch `hp2-diag` from b379b779)

`test/NLightning.Testing.Cluster/Diagnostics/` (details in that project's `CLAUDE.md`): on a failure the harness
writes `<root>/<test or fixture>/<namespace>/` with every pod's describe-equivalent, current and previous logs, the
namespace's events (oldest first), PVC/PV status, StatefulSets/Services/NetworkPolicies and each node's state
(bitcoind `getblockchaininfo`/`getpeerinfo`, CLN `getinfo`/`listpeerchannels`, LND `getinfo`/`listchannels`), never a
secret file and with passwords masked. Triggers: a `Poll` timeout, a failed readiness wait or topology build, and a
failed test through the xunit v3 hook `[assembly: ClusterDiagnostics]` (runs still alive at the test's end: fixture
runs); `run.DumpAsync` by hand; `NLTG_CLUSTER_DIAG=always|failure|off`; `NLTG_KEEP_NAMESPACE=failure` keeps a failed
run's namespace (annotated `nltg.keep`, reaped after its TTL). `run-cluster.sh` writes the dumps under
`<run>/diag/`, counts them per run and lists the folders of each failed run.

Proofs on OrbStack (all net10.0, Release): `DiagnosticsClusterTests` 2/2 green in 168 s (manual dump of bitcoind +
CLN + LND: 20 files, 0 errors, 0.4 s; a CLN node with an unknown option never becomes ready: the build fails after
59.8 s, one dump of 15 files in 0.2 s with the crash in `alice.previous.log`, the namespace kept and annotated), run
concurrently with `DiagnosticsFailureProofTests` (`--trait Category=ClusterFailureProof --keep-on-failure`, both fail
on purpose as intended: the `Poll` timeout dumped at the timeout (14 files, 1.1 s) and the hook appended the test's
failure; the assertion failure dumped by the hook (15 files, 0.7 s); both namespaces kept, then reaped by hand). No
regression: the CLN pair test 3 times at once, 3/3 green in 65-84 s, no dumps.

### Phase 2 lane A record: our in-process node in a topology (2026-10-02, branch `hp2-node` from b379b779)

`test/NLightning.Integration.Tests/Cluster/` (details in its `CLAUDE.md`): `InProcessNodeDeployer` is the
`ILightningNodeDeployer` of `NodeKind.NLightning` (one `NLightningTestNode` per topology node, listening on loopback and
announced to the pods as `host.orb.internal`, on the topology's bitcoind by pod IP), `InProcessNode` the
`ITopologyLightningNode` adapter over the daemon's client command handlers (our node dials out, NL-497; v1, dual-fund
and `Auto` opens; restart on the same key, database and port). The library stays free of product references; the
integration project references it. Proofs `Live/InProcessNodeClusterTests` (CLN: dual-funded open, pay both ways,
cooperative close; LND: v1 open by the topology with a push, pay both ways, restart, pay, close): 6/6 runs green in two
batches of 3 at once. Found: CLN v26.06.8 sends a P2TR `shutdown` script on a dual-funded channel without
`option_shutdown_anysegwit` negotiated, which our default features refuse, so the close stalls (the proof advertises
the option).

### Phase 2 integration record: lanes A, C and D on `wip/harness-spike` (2026-10-02)

Merged `hp2-node`, `hp2-startup`, `hp2-diag` (conflicts in `TestRunOptions` (lane C's `WaitForDeletion` default false
kept with lane D's `KeepNamespaceOnFailure`/`Diagnostics`), `TopologyBuilder.BuildAsync` (one wave and the chain
address wait inside lane D's `CaptureOnFailureAsync`), `run-cluster.sh` options and this plan's records). Added on
top:

- `ClusterTopologyFixture.OnStoppingAsync`: after the topology's adapters, before the namespace is deleted (also after
  a failed start).
- `InProcessTopologyFixture` (Integration.Tests): the warm topology with the in-process deployer registered and
  stopped by the fixture; **the seam the CLN port uses**. Live proof `Live/InProcessTopologyFixtureClusterTests`
  (bitcoind + our node + CLN on `emptyDir`, our v1 channel with a push, two tests sharing it): 3 runs at once, 3/3
  green, each topology started once in 9.3-9.7 s (namespace to channel active on both ends), tests 0.5-0.8 s.
- `[assembly: ClusterDiagnostics]` in Integration.Tests; its first catch was this proof's own bug (the second test
  read our balance while the first test's HTLC was still on our side); the dump's CLN `listpeerchannels` showed it.
- `RunLifecycleTests` reaper test: the live run starts before the orphan, because a run waiting for a slot reaps
  orphans every 30 s and removed the test's orphan first when the cap was full (failed in lane C's `full1` and in the
  integrated full run below; the class 3/3 green after the change).

Evidence (OrbStack, Release, net10.0): every `Category=Cluster` test of `NLightning.Testing.Cluster.Tests` in one
process (`hp2i-c1`, over the cap of 6, 526 s): 33/34, the miss the reaper test above (`hp2i-reap`: class 3/3 after the
fix). Integration.Tests `Category=Cluster` (`hp2i-i2`): 4/4 (CLN dual-funded proof built in 15.3 s and closed at 23.3
s, LND proof built in 19.3 s and closed at 22.1 s, the warm pair 13.4 s). Non-Docker suite on net10.0: all green.

### Phase 2 lane B record: the CLN interop suite on the cluster (2026-10-02, `wip/harness-spike`)

`wip/fafo` (1fee5a8d) was merged in first, so the port covers the current suite (`ClnCloseRestartTests`, the Tor
interop suite). Then:

- **Backend switch.** `NLTG_TEST_BACKEND=docker|cluster` (`Fixtures/TestBackend`, Docker when unset; a typo throws).
  `ClnFixture` keeps every member the classes use and delegates to `Fixtures/Cln/IClnBackend`: `DockerClnBackend` is
  the former fixture (same images, containers, flags and ports; its CLN command line is pinned by a unit test), and
  `ClusterClnBackend` is a warm `ClusterTopologyFixture` (suite `cln-interop`: bitcoind `miner` + CLN `nltg-cln` on
  `emptyDir`, the same CLN release by digest, the same flags and alias), with bitcoind and CLN reached by pod IP from the
  host and CLN dialling us at `host.orb.internal` (`ClnFixture.HostAddressForCln`).
- **CLNs of a class's own.** `ClnFixture.StartClnAsync(ClnNodeSpec)` replaces the containers three classes created
  themselves: `ClnDualFundTests` (`nltg-cln-df` per test), `ClnSpliceReestablishTests` (`nltg-cln-sp2`, `Restartable`:
  a PVC and its stable `<node>-p2p` ClusterIP name on the cluster, a fixed port on Docker; `RestartAsync`) and the
  Explicit gossip captures (`nltg-cln2`, reached only by CLN). On the cluster `TestRun.RemoveNodeAsync` (new,
  `RunNodeRemoval`) takes each out again.
- **The rest of the seam.** `ClnClient` runs `lightning-cli` through a `ClnExec` delegate (`docker exec` or a Kubernetes
  exec; `ExecAsync` for the onion message proof's raw call); `ClnFixture.DumpClnLogAsync` replaces the container log
  dumps. Test bodies are unchanged apart from those calls.
- **Tor stays on Docker.** On the cluster backend `TorInteropFixture` starts nothing and `ClnTorInteropTests` skip with
  the reason (`TestBackend.SkipOnCluster`).
- **Runner.** `scripts/run-cluster.sh -n 3 --suite cln` (N processes, each with its own run id and namespace, no Docker
  lock; `--explicit on` adds the 4 captures). `scripts/run-interop.sh cln` is unchanged and stays on Docker.
- **Found and fixed on the way.**
  - ZMQ start race: a node funded right after its start never saw the 6 blocks. A subscriber gets only what is
    published after its subscription reached bitcoind, and the subscription to a pod is slower to set up than to
    Docker's 127.0.0.1 port. Two hits in the first 3-at-once batch (231 executions). First worked around with a
    test-only `RegtestBitcoinEndpoint.ZmqStartupGuard`; the review found that this hid a product gap (any block ZMQ
    misses, also after a reconnect, waited for the next block), so it was replaced by the chain monitor's tip poll
    (see the review-fix record below).
  - `ClnCloseRestartTests` agreed-close case: CLN lists `CLOSINGD_COMPLETE` once it sent its `closing_signed`, which may
    still be in flight; the test now waits for it before the same assertion.
  - `ClnGossipCaptureTests` (Explicit): our node never funded the anchors reserve (NL-379), so CLN's open to it was
    refused on either backend.

Evidence (OrbStack, Release, net10.0; logs under `TestResults/cluster/hp2b-*`):

| Run | Result | Time |
|---|---|---|
| Cluster, alone (`hp2b-full1`) | 77/77 (81 discovered, the 4 Explicit captures not run); topology up in 13.3 s | 964 s |
| Cluster, the 4 Explicit captures (`hp2b-capt2`) | 4/4 | 46 s |
| Cluster, 3 at once (`hp2b-cln3`, before the fixes) | 75, 76 and 75 of 77; topologies up in 5.0-6.8 s | 877-933 s |
| Cluster, 3 at once (`hp2b-cln3b`, ZMQ guard) | 76, 76 and 75 of 77 | 864-886 s |
| Docker (`run-interop.sh cln`, under the lock, at c71d9d39) | 77/77, 4 Explicit not run (81 discovered); matches the batch10 baseline | 876 s |
| Docker again on the final test code (bca39fb6) | 77/77, 4 Explicit not run | 886 s |

Every 3-at-once run failed `ClnQuiescenceTests.Given_OurHtlcInFlight_*` (NL-477, closed in d13 as "not reproduced").
CLN logs "STFU but you still have updates pending?" when its `update_fulfill_htlc` for our HTLC (sent about 24 ms
after our `revoke_and_ack`) crosses our `stfu` on the wire (same millisecond in CLN's log). The pod's extra latency and
the load make the crossing likely. It also failed once alone on the cluster (`hp2b-alone`) and passed in `hp2b-full1`
and on Docker. This was first read as a protocol and CLN question and left failing; the review fixes below found our
side of it (Nagle) and fixed it. The other
cluster failures were the two fixed above.

### Phase 2 review fixes and proof record (2026-10-02, `wip/harness-spike`)

The review of 7439d024..09760b27 confirmed three findings; all three are fixed, two of them in the product:

- **ZMQ guard hid a chain-monitor gap (medium).** `BlockchainMonitorService.StartAsync` caught up over RPC and only then
  subscribed to ZMQ (NetMQ connects in the background) and never read the tip again, so a block mined in between, or
  while ZMQ reconnects, waited for the next block (about 10 min on mainnet). The test-only `ZmqStartupGuard` fed the tip
  in and hid it. Fixed in the product: `Bitcoin:TipPollInterval` (default 30 s, 0 = off) reads the tip over RPC; a
  poll that finds the monitor behind only notes it, the next one at the same processed height catches up under the ZMQ
  path's lock, logs a Warning and counts `TipPollCatchUps` (none while halted). `NLightningTestNode` sets 1 s on both
  backends; the guard is reverted. Unit tests in `BlockchainMonitorServiceTests` (5) and `BitcoinOptionsTests`. In the
  proof runs below the poll never had to catch up (0 hits in 4 cluster runs): the race is rare, and when it happens the
  product now recovers.
- **A failed `RemoveNodeAsync` blocked the name (low).** A failed removal now keeps the handle marked
  (`IsRemovalPending`) and the next `DeployAsync` of the name finishes it first. Live proof
  `RunLifecycleTests.Given_ANodeRemovalThatFailed_*` (removal cut at 300 ms, redeployed with a new pod in 3.3 s).
- **NL-477 is back (low; ledger).** Root cause found: **Nagle**. CLN read our `revoke_and_ack` at .152 and our `stfu`
  at .193 although we wrote them 1 ms apart: the `stfu` waited for CLN's delayed ACK (about 40 ms), and CLN fulfills
  our HTLC about 20 ms after that `revoke_and_ack`, so its fulfill was out first and CLN v26.06.8 answered "STFU but you
  still have updates pending?". Every peer TCP connection now has `NoDelay` (`TcpService` dialed and accepted,
  `TorSocksDialer`), as Go sets it for LND. Before: every attempt of two cluster runs crossed (`hp2f-q1`, with a test
  tolerance that was then dropped); after: no crossing in 6 runs (`hp2f-q2` x2, `hp2f-alone`, `hp2f-cln3` x3), the
  proof unchanged. The Docker suite also got faster (807 s against 876-886 s). The ledger entries (NL-477 reopened with
  the cause, the tip poll gap) are left to the integrator.
- The record's "81/81" counts are corrected: 81 discovered, 77 executed, the 4 Explicit captures not run, on both
  backends.

Also: `ClnFixture` logs "[fixture] CLN fixture (<backend>) ready in N s" after the start and the tip wait.

Proof (OrbStack k8s v1.35.6+orb1, Release, net10.0, `--no-incremental` build 0 warnings; logs under
`TestResults/cluster/hp2f-*` and `TestResults/hp2f/`):

| # | Run | Result | Time |
|---|---|---|---|
| 1 | Cluster alone (`hp2f-alone`) | 77/77 (81 discovered, 4 Explicit not run), 0 dumps | 889 s |
| 2 | Cluster 3 at once (`hp2f-cln3`), one namespace each | 77/77, 77/77, 77/77; every namespace gone after the batch | 882, 846, 823 s; batch 890 s |
| 3 | Docker (`run-interop.sh cln` under the machine lock) | 77/77, 4 Explicit not run | 807 s |
| 4 | Fixture ready (the new log line) | cluster 7.2 s alone, 5.5 / 6.5 / 5.5 s 3 at once (topology 5.4-7.1 s); Docker 3.0 s (3 starts) | |
| 5 | k8s containers of the batch (`docker stats`, 5 s samples) | peak 9 containers, 0.91 CPU cores, 539 MiB; median 0.13 cores | |

No cross-talk: each run on its own chain (heights 1149, 1149, 1150 at the end; one shared chain would have reached
about three times that), and each run's nodes reach only their own namespace's pods (bitcoind and CLN by the pod IPs of
the run's own topology). Wall time per run is about Docker's (14-15 min against 13.5); the gain is concurrency: 3
suites in 890 s instead of about 2,400 s one after another under the Docker lock.

### Phase 2 record (2026-10-02, `wip/harness-spike` at 1d776c4c)

Verdict: **phase 2 is done.** The CLN interop suite, the first real suite, runs unchanged on the cluster harness behind
a backend switch, green alone and 3 times at once, while the Docker CLN suite stays green on the same code. The lane
records above hold the detail; this section is the summary.

**What each lane built**

| Lane | Built | Where |
|---|---|---|
| A. In-process node in a topology | `InProcessNodeDeployer` (`NodeKind.NLightning`: one `NLightningTestNode` per topology node, on loopback, announced to pods as `host.orb.internal`, on the topology's bitcoind by pod IP) and `InProcessNode` (the `ITopologyLightningNode` adapter over the daemon's client handlers: connect, v1/dual-fund/`Auto` opens, invoices, pay, close, restart on the same key, database and port) | `test/NLightning.Integration.Tests/Cluster/` |
| C. Startup and teardown cuts | One deployment wave (Lightning StatefulSets with bitcoind, an init container waiting for bitcoind), `NodeStorage.Ephemeral` (`emptyDir`), maturity blocks to a burn address, the chain Service address wait (CoreDNS NXDOMAIN cache), pods-first teardown with background namespace deletion, `ClusterTopologyFixture<T>` (warm topology per collection) | `test/NLightning.Testing.Cluster/` (`Topology/`, `Run/`) |
| D. Diagnostics | Per-pod describe, current and previous logs, namespace events, PVC/StatefulSet/Service/NetworkPolicy state and per-node state (bitcoind, CLN, LND) on a `Poll` timeout, failed readiness or build, and a failed test (`[assembly: ClusterDiagnostics]`); `NLTG_CLUSTER_DIAG`, `NLTG_KEEP_NAMESPACE=failure`; `run-cluster.sh` lists the dumps per run | `test/NLightning.Testing.Cluster/Diagnostics/` |
| Integration | `ClusterTopologyFixture.OnStoppingAsync` (stop in-process nodes before bitcoind goes), `InProcessTopologyFixture` (the seam the CLN port uses), the diagnostics hook in Integration.Tests, the reaper test's admission race | both projects |
| B. The CLN port | `NLTG_TEST_BACKEND`, `ClnFixture` over `IClnBackend` (`DockerClnBackend`, `ClusterClnBackend`), `StartClnAsync(ClnNodeSpec)` for the classes' own CLNs, `ClnExec` (docker or Kubernetes exec), `TestRun.RemoveNodeAsync`, `run-cluster.sh --suite cln` | `test/NLightning.Integration.Tests/Fixtures/`, `scripts/run-cluster.sh` |
| Review fixes | Chain monitor tip poll (`Bitcoin:TipPollInterval`, product), `TCP_NODELAY` on every peer connection (product, NL-477), a failed node removal finished by the next deploy, an accepted already-reset socket no longer ends the listener (product) | `src/NLightning.Infrastructure{,.Bitcoin}/`, `test/NLightning.Testing.Cluster/Run/` |

**The CLN port design and the switch**

- `NLTG_TEST_BACKEND=docker|cluster` (`Fixtures/TestBackend`): Docker when unset, any other value throws, so a typo
  never quietly runs Docker. One process uses one backend.
- `ClnFixture` keeps every member the 17 CLN classes use and delegates to `IClnBackend`. `DockerClnBackend` is the
  former fixture moved over verbatim (images, container names, flags, ports; a unit test pins CLN's command line).
  `ClusterClnBackend` is a warm `ClusterTopologyFixture` (suite `cln-interop`, one namespace per test process: bitcoind
  `miner` + CLN `nltg-cln` on `emptyDir`, the same CLN release by digest, the same flags and alias).
- Test bodies changed only where they named Docker: `HostAddressForCln` instead of `host.docker.internal`,
  `DumpClnLogAsync` instead of container log dumps, `StartClnAsync(ClnNodeSpec)` instead of their own containers
  (`ClnDualFundTests`, `ClnSpliceReestablishTests`, the captures), `ClnClient.ExecAsync` for a raw `lightning-cli`.
- Running it: `scripts/run-cluster.sh -n 3 --suite cln` (cluster, no Docker lock) and `scripts/run-interop.sh cln`
  (Docker, unchanged, under the machine lock).

**Proof** (OrbStack k8s v1.35.6+orb1, Release, net10.0, final head; 81 tests discovered, 77 run, 4 Explicit captures
run separately, 4/4 on the cluster in `hp2b-capt2`):

| Run | Result | Wall time |
|---|---|---|
| Cluster alone (`hp2f-alone`) | 77/77, 0 diagnostics dumps | 889 s |
| Cluster 3 at once (`hp2f-cln3`, 3 namespaces) | 77/77, 77/77, 77/77; namespaces gone after | 823-882 s per run, batch 890 s |
| Docker (`run-interop.sh cln`, machine lock) | 77/77 | 807 s (876-886 s before the `NoDelay` fix) |
| Fixture ready | cluster 7.2 s alone, 5.5-6.5 s 3 at once; Docker 3.0 s | |
| Resources, 3 at once | peak 9 containers, 0.91 CPU cores, 539 MiB | |
| Warm in-process pair (`InProcessTopologyFixtureClusterTests`, 3 at once) | 3/3, topology to active channel 9.3-9.7 s | |
| Startup, pair alone (spike → phase 2) | CLN 22-25 s → 6.1-10.0 s, LND 21-32 s → 8.6 s on `emptyDir` | |
| Non-Docker suite, net10.0 | 13,990 passed, 6 skipped, 0 failed | |

The gain is concurrency, not per-run speed: 3 CLN suites in 890 s, against about 2,400 s one after another under the
Docker lock.

**Decisions**

- **In-process node placement.** On OrbStack our node runs in the test process on the host, listens on loopback and
  is announced to pods as `host.orb.internal:<port>`. Pods appear to it as 127.0.0.1 and would be saved inbound-only
  (NL-497), so our node always dials out to the pods (stable DNS names or pod IPs). Other clusters run the test
  assembly as a Job in the run's namespace (`InClusterTestRunner`, proven in the spike, not used by the CLN port yet).
- **PVC vs `emptyDir`.** The library default stays a PVC (restart and kill tests need it), but fixtures use `emptyDir`
  for every node a test never restarts: local-path provisioning was 6 s alone and 12-15 s under load, the biggest
  single startup cost. Only `ClnSpliceReestablishTests`' restartable CLN takes a PVC (plus a stable `-p2p` ClusterIP
  name). A pre-bound PV or an `Immediate` StorageClass would be cluster-scoped and is out of the rules.
- **Warm topologies.** One topology per xunit collection per process (`ClusterTopologyFixture<T>`), never reset, the
  same isolation contract as the Docker fixtures: tests must assert on their own channels and payments and wait for
  settled state (the diagnostics hook caught one test that did not).
- **Background deletion.** Disposal stops the pods first (1 s grace) and returns while the namespace terminates; a
  terminating namespace keeps its admission slot until it is gone, so the cap of 6 counts it; the reaper owns
  leftovers. Disposal went from 16-84 s to 0.6-4.4 s as the test sees it.
- **Tor stays on Docker.** `ClnTorInteropTests` (`Category=Interop.Tor`) need a Tor daemon topology; on the cluster
  backend the fixture starts nothing and they skip with the reason. Porting them belongs to phase 4 (with NL-572).
- **Product fixes over test workarounds.** Two harness symptoms were product gaps: blocks ZMQ never announced (now the
  tip poll) and Nagle holding our `stfu` behind our `revoke_and_ack` (NL-477, now `NoDelay`). Test-only guards for both
  were tried and dropped.

**Revised estimate for phases 3-6** (the CLN port took about a day once lanes A, C and D existed; the backend-switch
pattern carries over):

1. **Phase 3, LND (≈3-4 days).** The regtest topology (miner + alice/bob/carol/david, pre-opened channels, alice's
   `--protocol.rbf-coop-close` and the other per-node flags), an `LndBackend` behind `LightningRegtestNetworkFixture`
   keeping the members the 47 LNUnit-based files use, in-tree gRPC clients generated from LND 0.21.4's protos
   (Grpc.Tools) replacing `lnunit`/`LNUnit.LND`, then the LND suite, on-chain, gossip and ABCD suites on the switch.
   The long pole: four suites, address-hold restarts (NL-262) and LND's router timing (NL-319).
2. **Phase 4, Eclair, LDK, Tor, Postgres, partitions (≈2-3 days, unchanged).** Same backend pattern per fixture;
   Tor needs a Tor daemon in the namespace; partitions use the NetworkPolicy support proven in the spike.
3. **Phase 5, matrix runner (≈1 day).** `run-cluster.sh` already runs N processes of a suite; it grows into the suite
   matrix with rerun-alone and a summary, then replaces `run-{onchain,gossip,abcd,interop}.sh`.
4. **Phase 6, proof (≈1 day).** The full matrix twice concurrently, then at the tuned N; close NL-262 and NL-276. Per-run
   wall time is about Docker's, so the target (≈15 min instead of ≈75) comes from running 4-6 suites at once.

Total left: about 7-9 working days.

### Prepared: in-tree LND client (2026-10-02, branch `wip/lnd-grpc` from wip/fafo)

`test/NLightning.Testing.Lnd` (library, no product references) replaces the `lnunit.lnd` NuGet package, which can no
longer be published; its `README.md` has the full swap table and the design notes.

- **Clients.** Grpc.Tools client stubs from LND's 16 service protos under `lnrpc/` at v0.21.4-beta (commit
  `390b2d2b`), committed under `Protos/` by `scripts/lnd-protos/update.sh <tag>`. The script downloads by commit, adds
  one `option csharp_namespace` line per file and writes `manifest.txt` (sha256 per file, upstream and committed) and
  LND's MIT notice `Protos/LICENSE-LND.txt`. The types live in `NLightning.Testing.Lnd.Lnrpc`, `.Routerrpc`,
  `.Walletrpc`, `.Invoicesrpc`... so the assembly sits next to lnunit.lnd's global `Lnrpc`/`Routerrpc` in one test
  assembly; the wire names (`lnrpc.Lightning`...) are unchanged.
- **Connection layer**, ported from LNUnit.LND (MIT, `LICENSE-LNUnit.txt`): `LndSettings` (`FromFiles`, `FromBytes`,
  `FromBase64`; `tls.cert` pinned instead of LNUnit's accept-any), `LndNodeConnection` (LNUnit's member names, plus
  `ConnectAsync`), `LndNodePool` (thread-safe, readiness by `SERVER_ACTIVE`, LNUnit's 50/50 rebalance).
- **Proven.** 108 unit tests (`NLightning.Testing.Lnd.Tests`: proto manifest and license, pinning and macaroon
  against an in-process Kestrel HTTP/2 TLS fake, pool, coexistence with lnunit.lnd 3.0.4). Live:
  `Docker/LndGrpcLiveTests` (`Explicit`, `Category=LndGrpc`) starts its own `polarlightning/bitcoind:29.0` and two
  `custom_lnd:0.21.4-beta` nodes on its own network (`nltg-lndgrpc-<id>-*`) and drives them only through the new
  client: `GetInfo` (0.21.4-beta), `NewAddress` + funding by mining, `ConnectPeer`, `OpenChannelSync` to active,
  `AddInvoice` + `Invoices.SubscribeSingleInvoice` to `Settled`, `Router.SendPaymentV2` streamed to `SUCCEEDED` (one
  run first got LND's `insufficient_balance` on the fresh channel, NL-319, and the retry paid), `LookupInvoiceV2`,
  `ListChannels` balances (b 100,000 sat), `WalletKit.ListUnspent`, and an `LndNodePool` round over both (2 ready,
  rebalance 1 payment of 396,530 sat to 500,000/496,530). Green 2 runs in a row, about 10-13 s each, its containers
  and network removed every time (also after the two failed runs while writing it).
- **Swap table (phase 3).** `using LNUnit.LND;` → `using Testing.Lnd;`, `using Lnrpc;` → `using Testing.Lnd.Lnrpc;`
  (the same for `Routerrpc`, `Walletrpc`, `Invoicesrpc`, `Signrpc`, `Chainrpc`, `Peersrpc`, `Devrpc`, `Verrpc`);
  `LNDNodeConnection` → `LndNodeConnection`, `LNDSettings` → `LndSettings`, `LNDNodePool` → `LndNodePool`,
  `LNDNodePoolConfig` → `LndNodePoolConfig`, `GetLNDNodeConnection` → `GetLndNodeConnection`,
  `RebalanceNodePool` → `RebalanceNodePoolAsync`; the client properties (`LightningClient`, `RouterClient`,
  `WalletKitClient`, `InvoiceClient`...) and `LocalNodePubKey`/`LocalAlias` keep their names. Not 1:1: about 11
  Docker files qualify names (`Routerrpc.SendToRouteRequest`): write `Testing.Lnd.Routerrpc.X`, or a non-clashing
  alias; `<Using Alias="Routerrpc"/>` gives CS0576 while LNUnit is still referenced (Tests.Utils brings it in), so it
  works only in the step that drops the package. LND 0.21 removed `SendPaymentSync`/`SendToRouteSync`,
  `Router.SendPayment`/`SendToRoute`/`TrackPayment` and `outgoing_chan_id` on `SendPaymentRequest`/`QueryRoutesRequest`
  (none used by the tests; `BuildRouteRequest.OutgoingChanId` stays). The Docker fixtures run
  `custom_lnd:0.21.4-beta` since NL-768 (LND suite 89/89, on-chain 47/47, gossip 30/30, ABCD 11/11), and the
  harness's version table uses the same tag (`LndPairTopologyTests` 1/1 on 0.21.4, 35 s); phase 3 lane B moves the
  tests onto these clients.

### Phase 3 lane record: the LND regtest topology on the cluster (2026-10-02, branch `hp3-lnd-topo` from b88b2717)

The Docker suites' `LightningRegtestNetworkFixture` network, declared and built on the harness, with an adapter
surface for the fixture's cluster backend (the wiring is the next step; no Docker LND test file or `LndTestHelpers`
was touched).

- **LND clients in the library.** `NLightning.Testing.Cluster` no longer references LNUnit.LND: `LndNode.Connection`
  is an in-tree `LndNodeConnection` (`NLightning.Testing.Lnd`, LND 0.21.4 protos) from `LndCredentials.ToSettings`
  (`LndSettings.FromBytes` over the exec-read `tls.cert`/`admin.macaroon`, the certificate pinned), dialled at the pod's
  DNS name, so the connection object survives a restart or kill (replaced only when the credentials change);
  `LndGrpcConnection` is gone. LND counts as ready only at `SERVER_ACTIVE` (`WaitServerActiveAsync`) and synced.
  `LndNodeConnection.CreateWithoutNodeInfo` became public for that (a connection opened while LND starts).
- **`Topology/Lnd/LndRegtestNetwork`.** `LndRegtestNetworkSpec.Default` mirrors what LNUnit builds (read from the
  decompiled `LNUnitBuilder.Build`): miner + alice (`--protocol.rbf-coop-close`, `--accept-keysend`), bob, carol,
  david; every LND a permanent peer of every other; 2 x 42.69 BTC per wallet; public opens at 10 sat/vB (alice → bob
  10M; bob → alice, carol → alice, carol → bob 10M with 1M pushed); each funder's side at 0 msat / 0 ppm / delta 40.
  `Declare` + `SetUpAsync` (also as the warm `LndRegtestNetworkFixture`): the chain and LND nodes in one wave on
  PVCs, then the miner's reserve (30 coinbases to its wallet, matured by 100 burn blocks: about 1,208 BTC left), the
  mesh (dialled at once: 6 s → 0.3 s), one `sendmany`, the opens, 6 blocks, active on both ends, the policies, and
  every channel in every LND's graph with both policies enabled and the funder's set (`LndGraph.EdgeProblem`; the
  `HasOwnChannelEdgeAsync` check of `LndTestHelpers`, stricter). `RestartAsync(alias, kill)`: a StatefulSet restart
  (same DNS name and PVC, new pod IP; no NL-262 address-hold containers), `SERVER_ACTIVE`, every node of the network it
  was connected to dials it again (LND peers at the new pod IP, joined nodes at its alias) and its channels are active
  on both ends again. `JoinAsync` (any deployer: our in-process node), `OpenChannelAsync`, `PayAlongAsync` (one exact
  route through `BuildRoute` + `SendToRouteV2`).
- **Integration side.** `Fixtures/Lnd/ILndNetworkBackend` (the fixture members the LND Docker tests call: `Bitcoin`,
  `BitcoinZmqPorts`, `LndNodes`, `GetLndNode`, `RestartLndAsync`) and `ClusterLndBackend` (the warm network, the
  miner's endpoint by pod IP, `JoinInProcessNodeAsync`, `LndPeerHost`).
- **Found (NL-780, for the wiring step).** The Docker helper `NLightningTestNode.ConnectToAsync(LndNodeConnection)`
  resolves the connection's host to an **IP** and our node stores it; after a cluster LND restart (new pod IP) our
  node keeps redialling the old IP and LND cannot dial back (it saw an ephemeral port), the cluster form of NL-262.
  On the cluster, dial the Service name (`ClusterLndBackend.LndPeerHost`); joined nodes are redialled by the network.

Evidence (OrbStack, Release, net10.0; `TestResults/cluster/hp3l-*`):

| Run | Result | Time |
|---|---|---|
| `LndRegtestNetworkTests` alone (`hp3l-net1`) | 3/3 | network ready 37.5 s (chain 13.3, nodes 20.9, graph 36.3); every channel and alice → bob → carol paid in 1 attempt each (0.8 s); alice restart: `SERVER_ACTIVE` 7.7 s, 3 channels active 8.7 s; bob kill 6.5 / 8.5 s; whole class 57 s |
| `LndRegtestNetworkTests` 2 at once (`hp3l-net2`) | 3/3, 3/3 | network ready 44.7 / 47.3 s (mesh 0.3 s); restarts 6.3-6.8 s to `SERVER_ACTIVE`, 6.7-7.3 s to channels active; 63 / 64 s |
| `LndRegtestNetworkClusterTests` alone (`hp3l-int1`) | 1/1 | network 33.2 s; our node joined and its channel to alice active at 38.1 s; paid carol through alice and was paid by alice in 1 attempt each; alice restarted (4 channels, ours included) in 7.6 s; paid again; 46.6 s |
| `LndRegtestNetworkClusterTests` 2 at once (`hp3l-int2`) | 1/1, 1/1 | network 53.0 / 54.3 s; restart 8.1-8.4 s; 70 s each |
| Regression: `LndPairTopologyTests` + `MixedTopologyTests` (`hp3l-reg1`), `InProcessNodeClusterTests` LND proof (`hp3l-reg2`) | 2/2, 1/1 | 39 s, 25 s |
| Non-Docker suites touched | Testing.Cluster.Tests 522/522, Testing.Lnd.Tests 108/108, Integration.Tests (`!~Docker`) 1102/1102 | |

### Phase 4 record: the Eclair interop suite on the cluster (2026-10-02, branch `hp3-eclair` from b88b2717)

- **Harness (`test/NLightning.Testing.Cluster/Nodes/Eclair/`).** `NodeKind.Eclair` gets a workload, a facade and a
  deployer: `EclairNode.Workload` runs the existing local `nltg-eclair:0.14.3` (PullPolicy Never; not rebuilt) with the
  Docker fixture's `eclair.conf` (carried in an environment variable and written to `/data/eclair.conf` by the
  container's command, which then `exec`s the image's start script, so SIGTERM reaches the JVM), its data on a PVC, two
  init containers (the chain's startup wait, then `eclair-wallet`: curl + jq in Eclair's image create or load the
  bitcoind wallet `eclair` and wait until bitcoind left IBD, since Eclair refuses a chain in IBD), readiness = not
  draining and the API answers `getinfo`, `preStop` = the 5 s drain of `ClnNode`. `EclairTestPeer`
  (`ITopologyLightningNode` over the JSON API at the pod IP from the host, the pod DNS name in the cluster;
  `RestartAsync` retargets the API), `EclairNodeDeployer` (`DeploysWithChain`, a `StableNodeAddress`),
  `TopologyBuilder.AddEclair` (registered by default), `ITopologyChainEndpoint.ZmqHashBlockPort` (Eclair's
  `zmqblock`), Eclair state in the failure dumps (`getinfo`/`channels`/`peers`/`onchainbalance`).
- **Fixture.** `EclairFixture` keeps its members and delegates to `IEclairBackend`: `DockerEclairBackend` (the former
  fixture moved over; its `eclair.conf` pinned by `EclairBackendTests`) and `ClusterEclairBackend` (warm topology
  `eclair-interop`: bitcoind 31.1 on `emptyDir`, Eclair on a PVC because two tests restart it; the config differs from
  Docker only in the chain's alias and ZMQ ports, asserted). Our in-process nodes dial Eclair at its stable ClusterIP
  name (NL-497: they dial out; the address survives `RestartEclairAsync` as Docker's fixed host port does), call its
  API at the pod IP (`EclairClient.Retarget` after a restart) and are dialled by Eclair at `host.orb.internal`
  (`HostAddressForEclair`). Test bodies changed only where they named Docker (`HostAddressForEclair`,
  `DumpEclairLogAsync`, `EclairFixture.MineAsync` instead of the Docker-only `Chain`).
- **Runner.** `scripts/run-cluster.sh -n 1 --suite eclair` (`--explicit on` adds E-X1).
- **Findings.** None in the product: the suite was green on the cluster at the first full run, so no ledger entry
  (NL-785..NL-789 unused).

Evidence (OrbStack, Release, net10.0, `--no-incremental` build 0 warnings; logs under `TestResults/cluster/hp4e-*`):

| Run | Result | Time |
|---|---|---|
| Cluster alone (`hp4e-full1`), one namespace | 28/28 (29 discovered, the Explicit E-X1 not run), 0 dumps | 1,129 s (xunit 1,128 s) |
| Cluster, the Explicit E-X1 (`hp4e-explicit`) | 1/1 | 60 s |
| Fixture ready | cluster 18.5 s (topology 13.1 s, then the ClusterIP routing wait); Docker 5.8 s | |
| Docker (`run-interop.sh eclair` under the machine lock, final code) | 28/28 (29 discovered, 1 Explicit not run), the batch10 baseline | 1,084 s (xunit) |

### Phase 4 LDK lane record: the LDK interop suite on the cluster (2026-10-02, branch `hp3-ldk` from b88b2717)

The CLN pattern carried over unchanged; no product bug surfaced (the suite was green on the cluster at the first full
run).

- **Library (`Nodes/Ldk/`).** `LdkNode.Workload` runs the local `nltg-ldk-server:dc02b76c` (pulled `Never`; not
  rebuilt, the pin is unchanged) as a StatefulSet with `/data` on a PVC. The container writes the configuration from
  `NLTG_LDK_CONFIG` to `/data/config.toml` at every start (the Docker fixture's layout, `LdkNode.BuildConfig`) and
  `exec`s ldk-server as PID 1; readiness is `ldk-server-cli get-node-info` without the drain file, and the `preStop`
  drains 5 s as CLN's does, then ldk-server stops on SIGTERM (a graceful restart takes about 8 s). `LdkRpc` runs the CLI
  over a Kubernetes exec; `LdkTestPeer` implements `ITopologyLightningNode` (connect, open to the address `list-peers`
  has, list channels with the 64-bit SCID as `BxTxO`, invoices, `pay --wait`, height, balance); `LdkNodeDeployer` is
  registered by default (`AddLdk`, `DeploysWithChain`) and creates the node's `StableNodeAddress` Service first, so the
  node announces that ClusterIP (`ReadClusterIpAsync`), which peers and the host dial and which survives a restart.
  The diagnostics dump `get-node-info`, `list-channels`, `list-peers`, `get-balances` of an LDK pod.
- **Fixture.** `LdkFixture` keeps its members and delegates to `Fixtures/Ldk/ILdkBackend`: `DockerLdkBackend` (the
  former fixture, its `config.toml` pinned by `LdkBackendTests`) and `ClusterLdkBackend` (warm topology `ldk-interop`:
  bitcoind 31.1 `miner` on `emptyDir`, ldk-server `nltg-ldk` on a PVC). `LdkClient` runs over an `LdkExec` delegate
  (`docker exec` or Kubernetes exec). Test bodies changed only where they named Docker: `HostAddressForLdk` instead of
  `host.docker.internal`, `DumpLdkLogAsync` instead of container log dumps, `GetTipAsync` instead of the chain host's.
- **Runner.** `scripts/run-cluster.sh --suite ldk` (`-p integration --trait Category=Interop.Ldk`, no Docker lock);
  `scripts/run-interop.sh ldk` is unchanged. The `--help` range now prints the whole header.

Evidence (OrbStack, Release, net10.0; logs under `TestResults/cluster/hp4l-*`, `TestResults/hp4l/`):

| Run | Result | Time |
|---|---|---|
| Library live proof `Live/LdkTopologyTests` (bitcoind 31.1 + LDK + CLN, LDK's channel to CLN, pay both ways, LDK restart, pay) | 1/1 | 28.7 s; topology 13.1 s, restart 8.1 s |
| Cluster, `LdkInteropTests` alone (`hp4l-c1`) | 8/8; fixture ready in 12.7 s | 142 s |
| Cluster, the whole suite alone (`hp4l-full1`, one namespace) | 27/27, 0 diagnostics dumps, no tip-poll catch-up; fixture ready in 16.4 s | 655 s |
| Docker (`run-interop.sh ldk` under the machine lock) | 27/27 (baseline 27/27); fixture ready in 8.6 s | 579 s tests, 9:49 with the build |

### Phase 4 lane record: Postgres and network partitions (2026-10-02, branch `hp3-pg-faults` from b88b2717)

**Postgres on the cluster backend.** `PostgresFixture` keeps its members (and its connection string format, which
`PostgresTests` edits) over `Fixtures/Postgres/IPostgresBackend`: `DockerPostgresBackend` is the former fixture moved
over verbatim, `ClusterPostgresBackend` starts a run namespace (suite `postgres`, or `postgres-<name>` for
`StartNamed`) with one `PostgresNode` (`Nodes/Postgres/`: `postgres:16.2-alpine` pinned by its index digest, the
fixture's user, password and database, `PGDATA` on an `emptyDir`, readiness = `pg_isready` over TCP, which the image's
Unix-socket-only init server never passes) and connects to the pod IP. `Docker/PostgresTests` run unchanged on both.
`MultiNodeHarnessTests`' Postgres fact takes its server from `StartNamed`, so it follows the switch, but it still needs
the LND Docker fixture (phase 3 lane); its cluster equivalent is `Cluster/Live/ServerDatabaseClusterTests` (our node on
a fresh database of the collection's server connects to an LND pod, stops, starts and redials it from the stored
peer). `scripts/run-cluster.sh -n 1 --suite postgres` runs both classes.

**Partitions (new coverage).** `Faults/` grew what Lightning tests need:
- `PartitionFromHostAsync` (the *outside* shape): a pod keeps every pod of its run and DNS and loses the host, i.e.
  the test process and our in-process nodes. The isolate and split shapes cut the host too (policies only allow), but
  also cut a CLN from its bitcoind.
- `LimitIngressPortsAsync` (the *ports* shape): new connections reach only the listed ports (bitcoind: RPC and P2P).
- `RestartInPlaceAsync`: the node's own clean stop and the kubelet's restart in the same pod (same IP, `emptyDir`
  kept). It drops every connection, which a policy never does (conntrack keeps established TCP).
- `PauseProcessAsync`: one process name and its ancestors (CLN's `lightningd` behind a live `connectd`).
- `Reach/TcpConnectionTable`: established connections from `/proc/net/tcp{,6}` (is a ZMQ subscriber connected).
- **NL-795 (harness, fixed):** `ResumeAsync` killed CLN. The signal script walked `/proc` in name order, so CLN's bash
  entrypoint got SIGCONT before `lightningd`, saw its child stopped and exited 147 (128 + SIGSTOP); the container
  restarted. STOP now goes parents first and CONT children first, by depth in the process tree, and a named pause
  also stops the waiting ancestors.

How a test drops the established connection matters: our `DisconnectPeer` is "on purpose" and never redialled (by
design, the operator's `disconnect`); CLN's `disconnect` or our own ping timeout (the BOLT 1 liveness check,
`IPeerService.PingAsync`) are drops our node redials with its backoff.

| Test (`Cluster/Live/`, Explicit, `Category=Cluster`) | What it proves |
|---|---|
| `PartitionClusterTests` HTLC to a frozen CLN | CLN frozen (SIGSTOP) with our `update_add_htlc` + `commitment_signed` on the wire, partitioned; our ping gets no pong and drops the link. For 10 s the HTLC stays (payment InFlight, channel Open, nothing failed or broadcast). CLN resumes behind the partition and processes what reached its socket (it revoked: reestablish "ours 3/2, theirs 4/2"). After the heal our node reconnects by itself, CLN retransmits, the payment succeeds and both sides agree on the balances. |
| `PartitionClusterTests` partition outlasting our reconnects | CLN drops us behind the partition. For 10 s the channel stays Open, disconnected and not reestablished, and a payment fails at once ("no usable channel") without adding an HTLC; CLN's invoice stays unpaid. After the heal the channel is active again within 0.7-0.8 s, with no connect call from the test. |
| `PartitionClusterTests` reestablish never answered | `lightningd` frozen, `connectd` alive: our redial completes `init` and our `channel_reestablish` goes unanswered. The channel stays gated for 10 s with the transport up, and a payment is refused without an HTLC. Then partition, resume, heal and redial: reestablished and paying. |
| `PartitionClusterTests` CLN split from bitcoind | CLN's height stays at 113 while the tip goes to 116; our node follows the tip and pays CLN over the established connection; after the heal CLN catches up and pays us. (`--bitcoin-retry-timeout=600`: bcli would end lightningd after 60 s.) |
| `ChainMonitorZmqClusterTests` | bitcoind keeps only RPC and P2P and restarts in place; no ZMQ subscriber is connected (`TcpConnectionTable`). Our node (tip poll every 5 s) follows 3 blocks over RPC alone (`TipPollCatchUps` +1). It opens a channel to CLN, sees it confirm and pays, still without ZMQ. After the heal the subscriber reconnects and the next block arrives in about 100 ms with no catch-up: NL-775 end to end. |
| `ServerDatabaseClusterTests` | The server-database restart on a Postgres pod (above). |

**Evidence** (OrbStack, Release, net10.0, at most 2 namespaces of this lane at once):

| Run | Result | Time |
|---|---|---|
| `PostgresTests` on the cluster (`NLTG_TEST_BACKEND=cluster`, `-class`) | 23/23 | 43.7 s, server ready in 4.5 s |
| `ServerDatabaseClusterTests` alone, twice | 1/1, 1/1 | 13.7 s, 16.9 s (topology 9.7 s, restart to reconnected 0.3 s) |
| `run-cluster.sh -n 1 --suite postgres` (`hp4-pg1`, `hp4-pg2`) | 24/24, 24/24 | 57 s, 56 s |
| `run-cluster.sh -n 1 --suite faults` (`hp4-ft1`, `hp4-ft2`) | 5/5, 5/5 | 151 s, 113 s |
| `PartitionClusterTests` alone (`hp4pt-c1`, before the NL-795 ancestor fix) | 3/4 (the named pause killed CLN) | 108 s |
| Docker `PostgresTests` from the host, under the machine lock | 23/23 | 22.7 s |
| Docker `MultiNodeHarnessTests` Postgres fact (SDK container, `--network host`, same lock) | 1/1 (`StartNamed` on Docker) | 17.6 s |
| Partition timings (both faults runs) | HTLC held 10 s, settled 4.3-4.6 s after the heal; channel back 0.8-1.3 s after the heal; gated 10 s, back 0.4-0.7 s; CLN held at 113 while the tip went to 116; 3 blocks over RPC alone in 5.7-7.2 s, ZMQ back 0.0 s | |

Not found: a product bug. Observations: NL-796 (our node has no deadline for the peer's `channel_reestablish`),
NL-797 (the ZMQ subscriber came back 0-12.5 s after the port cut healed; the tip poll covered the gap).

### Phase 3/4 integration record (2026-10-02, `wip/harness-spike`)

Merged `--no-ff` from b88b2717 in order: `hp3-lnd-topo` (e4d116ce), `hp3-eclair` (521b0a28), `hp3-ldk` (6986266f),
`hp3-pg-faults` (84f4e46c). Conflicts were in the CLAUDE.md files, this plan, `run-cluster.sh` (every suite kept:
`cln`, `eclair`, `ldk`, `postgres`, `faults`; `--help` now prints the header up to its first non-comment line instead
of a fixed line range), `TopologyBuilder` (both deployers and `AddEclair`/`AddLdk`) and `NodeStateCommands` (both
kinds' state commands). Two semantic conflicts surfaced only after the merges: `ServerDatabaseClusterTests` still used
LNUnit's `Lnrpc` after lnd-topo moved `LndNode` onto the in-tree client (fixed in the pg-faults merge), and
`TopologySpecTests`' missing-deployer case named `Ldk`, which now has a default deployer (7d08dc24: it names
`NLightning`, the one Lightning kind whose deployer comes with `UseInProcessNodes`).

Review findings, all fixed (no false positives):
- **NL-800 (found here, 4d1e2997).** xunit v3 creates a collection's or class's fixtures whenever the selection holds
  any of its tests, Explicit ones included. `ClusterTopologyFixture` built in `InitializeAsync`, so CI's
  `FullyQualifiedName!~Docker` started the warm topologies, and with no cluster reachable the Explicit tests failed
  (2/2 for the warm CLN collection with `KUBECONFIG` unset). Now `EnsureStartedAsync()` builds on the first call from a
  test class's `IAsyncLifetime.InitializeAsync`; the cluster backends of the Docker-era fixtures call
  `EnsureStartedAsync(ct)` with their start token (lnd-topo low: the token was ignored).
- **NL-801 (pg-faults high + medium, 3deb5976).** `ServerDatabaseClusterTests` left the `postgres` collection (it made
  every non-Docker run start the Docker Postgres container) and starts its own server with
  `PostgresFixture.StartNamed("pg-restart", TestBackendKind.Cluster)`, asserting the cluster backend. So
  `run-cluster.sh --suite postgres` now runs its two classes in parallel on 3 namespaces (the collection's server, the
  test's server, its topology).
- **NL-802 (lnd-topo medium + lows, 8968d060).** `PayAlongAsync` bounds every call by its timeout (an HTLC a hop holds
  silently ends as a failed payment naming the route); a restart's LND redial is checked from both ends, sent at the
  new pod IP and drops a stale connection first, and `WaitMeshAsync` is asserted by the restart test; polls count a
  failed gRPC call as "not yet"; the workload de-duplicates only whole flags; the miner's balance is checked before the
  wallet fundings and `Validate`'s docs say what it checks.
- **NL-803 (Eclair low, 06e8072b).** The wallet init passes `NLTG_RPC_USER`/`NLTG_RPC_PASSWORD`, so failure dumps mask
  the password (it was printed as `NLTG_RPC_AUTH=user:password`).
- **pg-faults medium (3deb5976).** The frozen-`lightningd` partition test asserts the redial, the transport up for the
  whole hold and an established socket on CLN's 9735; before, it only logged them.
- **NL-804 (pg-faults low, 3deb5976).** `TcpConnectionTable` reads `/proc/net/tcp6` only when it exists.

Found and not fixed: a torn read of a channel's stored state. `ChannelDbRepository.GetAllAsync` maps the channel's
parameters from its first query and `ChannelStateDbRepository.LoadAsync` re-reads `Channels` and the state rows in
separate queries, with no read transaction. A splice-lock save committing in between made `listchannels` throw
"Balances add up to 1100000000 msat, not 1000000000" once (`EclairSpliceTests.Given_WeSpliceIn_*` in the full
cluster Eclair run; the class 7/7 alone). Product code these lanes did not touch; reported for a ledger ID.

Evidence (OrbStack, Release, net10.0, at most 2 harness namespaces of this job at once; `TestResults/cluster/hpi-*`):

| Run | Result | Time |
|---|---|---|
| `--no-incremental` Release build, `dotnet format`, `check-sln-configs.py` | 0 warnings, clean, OK | |
| Non-Docker suite (`FullyQualifiedName!~Docker&Category!=Cluster`) | 14,167 passed, 6 skipped (platform), 1 failed (the missing-deployer test, fixed in 7d08dc24, then Testing.Cluster.Tests 588/588); no Postgres container appeared in `docker ps` during the run | |
| `LndRegtestNetworkTests` + `LndRegtestNetworkClusterTests` at once (`hpi-lndnet`, `hpi-lndint`) | 3/3, 1/1 | network ready 34.3 s; alice restart `SERVER_ACTIVE` 7.3 s, 3 peers redialled, mesh whole; 51 s, 60 s |
| `--suite ldk` (`hpi-ldk`) and `--suite eclair` (`hpi-eclair`) at once | 27/27; 27/28 + 1 Explicit not run (the torn read above), `EclairSpliceTests` alone 7/7 (`hpi-eclsplice`) | 557 s; 1,098 s; 432 s |
| `Live/LdkTopologyTests` (`hpi-ldktopo`) | 1/1 | 30 s |
| NL-800 lazy start: `TopologyFixtureTests`, `InProcessTopologyFixtureClusterTests` (`hpi-warm`, `hpi-warmnl`) | 2/2, 2/2 (one namespace each) | 18 s, 15 s |
| `PostgresTests` on a Postgres pod (`hpi-pgt`), `ServerDatabaseClusterTests` without `NLTG_TEST_BACKEND` (`hpi-pgsrv`) | 23/23, 1/1 (cluster Postgres) | 39 s, 15 s |
| `--suite faults` (`hpi-faults`) | 5/5: HTLC held 10 s, settled 4.0 s after the heal; gated 10 s with the transport up (1 socket on CLN's 9735); CLN held at 113 while the tip went to 116; ZMQ cut covered by 1 tip-poll catch-up | 112 s |
| No cluster reachable (`KUBECONFIG` pointing nowhere), the warm classes without `-explicit` | Not Run 2 / Not Run 5, 0 failed (before NL-800: 2 failed) | |

### Phase 3/4 record (lanes: LND topology, Eclair, LDK, Postgres, partitions) (2026-10-02, `wip/harness-spike` at cbdee79f)

The summary of phases 3 and 4 so far. The four lane records and the integration record above have the details; this
record adds the proof stage and says what is left.

**What each lane built** (four lanes from b88b2717, merged `--no-ff` in this order):

| Lane (branch, merge) | Built | Product findings |
|---|---|---|
| LND topology (`hp3-lnd-topo`, e4d116ce) | `Topology/Lnd/LndRegtestNetwork` (miner + alice/bob/carol/david on `custom_lnd:0.21.4-beta`, PVCs, LNUnit's channels, flags and policies, every edge in every graph), restart/kill with the redial checked from both ends, `JoinAsync`/`OpenChannelAsync`/`PayAlongAsync`; `NLightning.Testing.Cluster` on the in-tree LND client (`NLightning.Testing.Lnd`), no LNUnit; `Fixtures/Lnd/ILndNetworkBackend` + `ClusterLndBackend` ready for the fixture's cluster backend | NL-780 (open: the Docker helper stores LND's IP, so after a cluster restart our node redials the old pod IP; dial `LndPeerHost`) |
| Eclair (`hp3-eclair`, 521b0a28) | `Nodes/Eclair/` (workload on the existing `nltg-eclair:0.14.3`, wallet init, facade, deployer, dumps), `EclairFixture` over `IEclairBackend` (Docker / cluster), `--suite eclair` | none in the lane; the proof found NL-805 and NL-806 |
| LDK (`hp3-ldk`, 6986266f) | `Nodes/Ldk/` (workload on the existing `nltg-ldk-server:dc02b76c`, exec RPC, facade, deployer with a stable ClusterIP), `LdkFixture` over `ILdkBackend`, `--suite ldk` | none |
| Postgres and partitions (`hp3-pg-faults`, 84f4e46c) | `PostgresFixture` over `IPostgresBackend` (`Nodes/Postgres/`), `ServerDatabaseClusterTests`; `Faults/` partition from host, ingress ports, restart in place, process pause, `TcpConnectionTable`; `PartitionClusterTests` (4) and `ChainMonitorZmqClusterTests`; `--suite postgres`, `--suite faults` | NL-795 (harness, fixed), NL-796 (open: no deadline for the peer's `channel_reestablish`), NL-797 (wontfix, the NL-775 tip poll covers it) |

Integration and review (6d4c1a3b and before): NL-800 (xunit built the warm fixtures of Explicit tests not run; lazy
`EnsureStartedAsync`), NL-801..NL-804 (review fixes), all fixed.

**Proof stage** (OrbStack k8s, Release, net10.0, `--no-incremental` build 0 warnings; at most 6 run namespaces at
once, none left at the end; cluster logs `TestResults/cluster/pf-*`, Docker logs `TestResults/proof/docker-*.log`; the
Docker stream ran under the machine lock in parallel with the cluster runs):

| Run | Cluster | Docker (machine lock) |
|---|---|---|
| Eclair suite alone (`pf-ecl1`) | 28/28 (+1 Explicit not run), 1,148 s | 28/28 (+1 Explicit), 1,077 s |
| Eclair suite 3 at once (`pf-ecl3`) | 28/28, 27/28 (NL-805), 27/28 (NL-806); 1,165-1,227 s | — |
| `EclairSpliceTests` with the NL-805 fix: alone (`pf-eclsp1`), 3 at once (`pf-eclsp3`) | 7/7; 7/7 x3 (411-444 s) | — |
| LDK suite alone (`pf-ldk1`), 3 at once (`pf-ldk3`) | 27/27, 643 s; 27/27 x3, 573-632 s | 27/27, 502 s |
| Postgres (`pf-pg1`: `PostgresTests` + `ServerDatabaseClusterTests`, 3 namespaces) | 24/24, 47 s | `PostgresTests` 23/23, 22.5 s |
| `LndRegtestNetworkTests` 3 at once (`pf-lndnet3`) | 3/3 x3; network ready 43.1-46.4 s; restarts 5.8-7.7 s to `SERVER_ACTIVE` | — |
| `LndRegtestNetworkClusterTests` 3 at once (`pf-lndint3`) | 1/1 x3; network 37.0-49.1 s, our node's channel active about 5 s later | — |
| Partition and ZMQ-loss tests (`pf-ft1`) | 5/5, 106 s | — (new coverage) |
| CLN suite regression (`pf-cln1`) | 77/77 (+4 Explicit), 864 s | 77/77 (+4 Explicit), 761 s |

Fixture ready, cluster against Docker: CLN 6.3 s / 3.0 s, Eclair 25.8 s (21.8-36.2 s 3 at once) / 10.8 s, LDK 15.6 s
(13.0-18.2 s) / 4.9 s, Postgres 6.1 s / 2.0 s. Suite wall time on the cluster is within 1.1-1.3x of Docker alone and
3 runs at once take about the time of one, which is the point: the Docker suites run one at a time machine-wide.

**Product fixes and findings from the proof:**
- **NL-805 (fixed, cbdee79f): a channel load torn by a save committing between its queries.** `listchannels` threw
  "Balances add up to 1100000000 msat, not 1000000000" in `EclairSpliceTests` 3 at once (the torn read the integration
  record reported without an ID). `ChannelDbRepository` maps the channel row first and `ChannelStateDbRepository`
  reads the state rows after it, with no read transaction; a splice lock committing in between mixed the old capacity
  with the new balances. The fix: the engine's restore error is a `ChannelStateInconsistentException` and the
  channel is read again, whole, up to 3 times; a state inconsistent on every read is still refused with the same
  message. Two SQLite tests (an interceptor commits the lock between the queries). **Overlap:** another job fixes the
  same read on `wip/nl810` as NL-810 with one database snapshot per load (`ReadConsistentlyAsync`, all three
  providers). That is the stronger fix; when both reach `wip/fafo`, keep NL-810's snapshot read and either drop the
  re-read or keep it as a second line, and mark one of the two entries `duplicate`.
- **NL-806 (open): a dead connection kept after a cluster Eclair restart.** One of our in-process nodes never got the
  close of its idle connection (Eclair showed it OFFLINE, our node "connected, reestablished"), and our keep-alive
  (random 30-300 s) did not notice within the test's 2 min. Once in 8 cluster Eclair runs; Docker never hits it
  (docker-proxy closes the host socket). A product or owner call (why OrbStack's host-to-ClusterIP path lost the
  close, or a shorter keep-alive: LND pings every minute); no assertion was weakened.

**Decisions:**
- Each Docker-era fixture keeps its members and delegates to a Docker backend (the old code moved over) and a cluster
  backend; `NLTG_TEST_BACKEND` picks per process, Docker when unset. Test bodies changed only where they named Docker.
- Existing images are reused as they are (`nltg-eclair:0.14.3`, `nltg-ldk-server:dc02b76c`, `custom_lnd:0.21.4-beta`,
  `postgres:16.2-alpine` by digest); no image was rebuilt or retagged.
- Nodes a restart test restarts sit on PVCs and are dialled at a stable name (StatefulSet DNS or a `StableNodeAddress`
  ClusterIP); the rest use `emptyDir`.
- Warm cluster fixtures start lazily from the first test class that runs (NL-800), so CI's non-Docker run never
  touches a cluster.
- Tor stays on Docker (`ClnTorInteropTests` skip on the cluster; NL-572 is not closed by these phases).

**What remains** (revised estimate about 6-8 working days, against the 5-8 days phases 3-4 had left in §5):
1. **The LND fixture's cluster backend wired to the topology (about 1 day).** The `p3-lnd-swap` lane landed on
   `wip/fafo` (2f0cce7b..50f108fe: the Docker tests on `NLightning.Testing.Lnd`, NL-770); merge `wip/fafo` into this
   branch, then make `LightningRegtestNetworkFixture` delegate to `ILndNetworkBackend` (Docker = LNUnit's builder,
   cluster = `ClusterLndBackend`) and fix NL-780 by dialling `LndPeerHost`.
2. **Per-suite cluster proofs (about 2-3 days):** the LND suite, on-chain legacy and anchors (`run-onchain.sh`'s
   suites), gossip and ABCD, each alone and 3 at once, against their Docker baselines; `MultiNodeHarnessTests` on the
   cluster.
3. **Removing `lnunit` (about 1 day):** once every LND suite runs on the in-tree client, the Docker backend needs its
   own container builder (or Docker LND runs end), then the package goes.
4. **Phase 5 runner (1-2 days):** `run-cluster` replaces `run-{onchain,gossip,abcd,interop}.sh`, builds once, runs the
   matrix with N in flight, reruns a failed class alone, summarizes and collects diagnostics.
5. **Phase 6 full-matrix proof (about 1 day plus reruns):** the whole matrix twice concurrently, then at the tuned N;
   wall time against today's serial pass; NL-262 and NL-276 closed.
Open items carried: NL-780, NL-796, NL-806, and the NL-805/NL-810 merge.

### Phase 3 completion record: the LND fixture on the cluster backend (2026-10-03, branch `hf-lnd-wire` from 7593fdda)

What remained as item 1 of the "Phase 3/4 record": `LightningRegtestNetworkFixture` behind `NLTG_TEST_BACKEND` and
the LND Docker suite on the cluster.

- **Fixture.** `LightningRegtestNetworkFixture` (collections `regtest`, `onchain-regtest`, `gossip-regtest`) keeps its
  members and delegates to `Fixtures/Lnd/ILndNetworkBackend`, picked by `TestBackend.Current` when xunit creates it;
  it is now an `IAsyncLifetime` (the network starts in `InitializeAsync`, not in the constructor).
  `Fixtures/Lnd/DockerLndBackend` is the former fixture moved over unchanged (LNUnit's builder, the same containers,
  flags, channels and image; it removes containers on dispose only once it got to the builder, so a backend that never
  started never removes another process's containers); `ClusterLndBackend` is the warm `LndRegtestNetwork`. New
  members on both: `GetLndPeerEndpointAsync(lnd)`, `HostAddressForLnd` (`HOST_ADDRESS`/`host.docker.internal`, or
  `host.orb.internal`), `DumpLndLogsAsync(aliases)` (container or pod logs), `Backend`, `Cluster`.
- **NL-780 fixed.** `NLightningTestNode`s made from the fixture dial LND at `GetLndPeerEndpointAsync`: the resolved
  container IP on Docker (as before), the Service name `alias.<ns>.svc.cluster.local:9735` on the cluster. Our node
  stores that name, so after a cluster restart of alice (new pod IP) its reconnect backoff reaches the new pod
  (`ReestablishFlowTests` (b): "Unable to reconnect ... retrying in 2 s", then "Reconnected" about 2 s later and the
  channel reestablished). The NL-262 address-hold containers stay in `ReestablishFlowTests` on Docker only.
- **Test bodies** changed only where they named Docker: `AbcNetworkTests` (LND dials us at `HostAddressForLnd`),
  `ReestablishFlowTests` (the address hold and its assertion on Docker only) and the 12 classes that dumped container
  logs on failure (`_fixture.DumpLndLogsAsync`).
- **Runner.** `scripts/run-cluster.sh --suite lnd`: the `Docker`, `Docker.Utils` and `Docker.Mock` namespaces, no trait
  filter, `-notrait Database=SqlServer -parallel none` (the collections one after the other, so one process holds at
  most 2 namespaces: a collection's network and `MultiNodeHarnessTests`' own Postgres pod).
- Nothing in the product changed: the suite was green on the cluster at the first full run.

Evidence (OrbStack, Release, net10.0; cluster logs `TestResults/cluster/hfl-*`, Docker log
`TestResults/proof/docker-lnd.log`; the LND suite is now 90 tests: `PostgresTests` gained the NL-810 round trip):

| Run | Result | Time |
|---|---|---|
| `--no-incremental` Release build, `dotnet format`, `check-sln-configs.py` | 0 warnings, clean, OK | |
| Integration.Tests non-Docker (`FullyQualifiedName!~Docker&Category!=Cluster`) | 1121/1121 (new: `DockerLndBackendTests` 3, `ClusterLndBackendTests` +1) | 58 s |
| `AbcNetworkTests` on the cluster (`hfl-abc`) | 3/3 (alice dialled, bob dials us at `host.orb.internal`) | 39 s |
| LND suite on the cluster, alone (`hfl-lnd1`) | 90/90: regtest 56, postgres 24, onchain-regtest 6, gossip-regtest 2, Utils 2; network ready 36.5 s | 638 s |
| LND suite on the cluster again (`hfl-lnd2`, while the Docker run below ran) | 90/90; network ready 28.0 s | 607 s |
| LND suite on Docker (SDK container, `--network host`, machine lock) | 90/90; network ready 14.1 s | 497 s |

Per collection, cluster (`hfl-lnd2`) against Docker: regtest 357 / 338 s, gossip-regtest 89 / 90 s, postgres 36 / 30 s,
onchain-regtest 28 / 27 s.

What remains of phase 3: the per-suite cluster proofs of on-chain, gossip and ABCD (their fixtures now take the cluster
backend too, but `run-onchain.sh`/`run-gossip.sh`/`run-abcd.sh` still run Docker, and some of their bodies name Docker:
`GraphStoreFlowTests` resolves LND's IP, the anchors relay bitcoind is a container), the LND suite 3 at once, and the
`lnunit` removal (the Docker backend still needs LNUnit's builder).

### Phase 5 runner record: `run-cluster.sh --matrix` (2026-10-03, branch `hf-runner` from 7593fdda)

`scripts/run-cluster.sh` is the one runner of the Docker-class suites on the cluster; the Docker scripts
(`run-{onchain,gossip,abcd,interop}.sh`) stay as the fallback and for Tor.

- **Matrix mode.** `--matrix [s1,s2,...]` (default every suite) builds the integration project and `nltg-cluster` once,
  plans (`nltg-cluster matrix plan`), and runs the suites longest first through an admission queue: at most `-j`
  suites (default 3) and `--max-namespaces` run namespaces (default and cap 6) at once, each suite weighing the
  namespaces it declares. A suite is one test process (`NLTG_TEST_RUN_ID=<batch>-<suite>`, `NLTG_TEST_BACKEND=cluster`,
  `-trait- Database=SqlServer` always) with a hang timeout (per suite, 10-90 min, or `--timeout`; TERM, KILL 30 s
  later, marked TIMEOUT). When it ends, its namespaces are reaped and the runner waits until they are gone,
  terminating ones included, before the slot is free. Then each failed class (1 to `--rerun-max`, default 3; none
  after a timeout, a crash or a fixture error) is rerun alone as `<batch>-<suite>-r<n>`: green marks the suite
  `rerun-green` and names the flake, red is a real failure. The summary (`summary.txt`): suite, result, tests,
  passed/failed/skipped/not run, rerun, start, wall, fixture ready, namespaces created/planned, dumps, first error;
  then the flakes, the skips, the log and `diag/` folders of every failed suite, the totals and the batch's sampled
  namespace peak; exit 1 on a real failure. Green runs' logs are gzipped after the summary (`--keep-logs` keeps them).
  Ctrl-C stops the batch's processes and reaps their namespaces. `-n N --suite S` and the `--class/--method/--trait`
  runs keep working, now with the same hang timeout.
- **The catalog** (`NLightning.Testing.Cluster/Run/Matrix/`, `nltg-cluster matrix list|plan|rerun-classes|summary`;
  the script keeps only processes, admission, timeouts and reaping): `lnd` (the `regtest` collection's classes of
  the `Docker` namespace and `Docker.Utils`), `onchain` (`Docker.Onchain.Onchain*` + `BackupRestoreFlowTests`),
  `anchors` (`Docker.Onchain.Anchors`), `gossip` (`Docker.Gossip.*` without `Capture`, `Docker.Day0.*`,
  `ChannelPolicyPublicFlowTests`, `SpliceLndObserverTests`), `eclair`, `cln`, `abcd`, `ldk`, `faults`, `postgres` and
  `tor` (listed as skipped: Docker only). Namespaces per process: postgres 3 (2 serially), faults 2 (1), lnd 2, the
  rest 1. A suite whose parallel collections exceed the budget runs with `-parallel none`, and then
  `NLTG_WAIT_NAMESPACE_DELETION=1` makes each collection's run wait until its namespace is gone
  (`TestRunOptions.WaitForDeletion`), so the serial count holds. The LND suites (`lnd`, `onchain`, `anchors`,
  `gossip`, `abcd`) are planned as skipped, and refused by `--suite`, until `LightningRegtestNetworkFixture` names
  `ILndNetworkBackend` (`LndBackendProbe`): the unwired fixture ignores `NLTG_TEST_BACKEND` and would start Docker
  containers outside the Docker lock.
- **Tests without a cluster:** `Testing.Cluster.Tests/Run/Matrix/` (catalog, planner, an evaluator of xunit v3's simple
  filters, results, judgement, summary, CLI), `Integration.Tests/Cluster/SuiteCatalogMembershipTests` (every class of
  the container namespaces runs in exactly one suite unless all its tests are Explicit or SQL Server, and each suite
  holds only the classes of its fixture collection) and `scripts/tests/run-cluster-tests.sh` (32 checks of the bash
  side against `scripts/tests/fake-xunit.sh`: the admission queue never over the budget, the flake rule, the hang
  timeout, crashes, skips, log compression, option errors; about 35 s).

Findings (ledger IDs for the integrator):
- **NL-816 (fixed here):** `ClnChannelSessionTests` and `ClnSpliceRbfHelperTests` (13 container-free tests in
  `Docker.Interop.Cln`) had no `Interop.Cln` trait, so neither CI (`FullyQualifiedName!~Docker`) nor
  `run-interop.sh cln`/`--suite cln` ran them. Found by the membership test; tagged `Interop.Cln`, all 13 pass (the
  CLN suite grows from 81 to 94 tests; the proofs below ran before the tag).
- **NL-817 (fixed here, harness):** a finished run's namespaces outlived its slot. `nltg-cluster reap --wait` waits only
  for namespaces it deletes, and `TestRun` disposal deletes in the background, so after a suite ended its namespaces
  were still terminating when the next suite started; within a `-parallel none` suite the next collection's namespace
  was created while the previous one terminated (the first proof's postgres created 3 namespaces under a serial count
  of 2). Fixed by the runner's wait until gone and `NLTG_WAIT_NAMESPACE_DELETION`; the second proof's sampled peak was
  2 of 2.
- **NL-818 (open, observation):** suite logs are huge: one `output.log` held 0.8 GB for CLN (3.3 M lines, 0.82 M of
  them Debug) and 0.3 GB for LDK, so a full matrix writes several GB per pass. The runner now gzips green runs' logs
  (about 4x); the volume itself (Debug logging of every in-process node into the test output) is a harness choice
  left to phase 6 (per-node log files, or Information by default).

Evidence (OrbStack, Release, net10.0, at most 2 namespaces of this lane at once; `TestResults/cluster/hfr-*`):

| Run | Result | Time |
|---|---|---|
| `--no-incremental` Release build, `dotnet format`, `check-sln-configs.py` | 0 warnings, clean, OK | |
| `Testing.Cluster.Tests` (`Category!=Cluster`), `Integration.Tests` (`FullyQualifiedName!~Docker&Category!=Cluster`) | 659/659, 1,122/1,122 | |
| `scripts/tests/run-cluster-tests.sh` | 32/32 | 35 s |
| `--matrix cln,ldk,postgres -j 2 --max-namespaces 2` (`hfr-proof1`, before the NL-817 fix) | cln 81 (77 passed, 4 Explicit not run), ldk 27/27, postgres 25/25 (serial; 3 namespaces created) | 967 s |
| The same with the final runner (`hfr-proof2`) | cln 77/77 (+4 not run), ldk 27/27, postgres 25/25 serial in 2 namespaces; peak 2 of 2; nothing left | cln 901 s, ldk 558 s, postgres 91 s from +915 s; matrix 1,008 s |
| Hang timeout live (`hfr-timeout`: `--matrix postgres --timeout 25s`) | TIMEOUT after 25 s, process stopped, its 2 namespaces reaped | 42 s |
| Single-suite mode (`hfr-single`: `-n 1 --suite postgres --class ...PostgresTests`) | 24/24 | 46 s |

Fixture ready: CLN 6.1-7.5 s, LDK 14.6-14.7 s, Postgres 5.0-5.4 s (each in line with the phase 3/4 record).
Next: the LND fixture's cluster backend (lane `hf-lnd-wire`) turns the five LND suites on in the matrix without a
runner change; phase 6 runs the full matrix twice concurrently.

### Lane record: LNUnit confined to the Docker LND backend (2026-10-02, branch `hf-lnunit` from 7593fdda)

- **Done (NL-819).** `LightningRegtestNetworkFixture` is backend-neutral: it holds an `ILndNetworkBackend` (interface
  unchanged) and keeps only the alias/image constants and `GetOrCreateAsync`. The LNUnit code moved to
  `Fixtures/Lnd/DockerLndBackend` (the `LNUnitBuilder` network, the in-tree `LndNodeConnection`s, the `custom_lnd`
  image build when missing); its dispose now disposes the builder instead of the old unawaited `Destroy()`, which
  could only fail on containers the fixture had already removed. The fixture picks the backend from `TestBackend.Current`
  (lane `hf-lnd-wire`; the integration merged both). `LNUnit.Setup`'s `PullImageAndWaitForCompleted` (CLN,
  Postgres, SQL Server) is replaced by `DockerContainerUtils.EnsureImageAsync`/`ImageExistsAsync` (moved from
  `InteropChainHost`; pull only when the image is missing, as the Eclair/LDK/Tor fixtures already did).
  `Testing.Lnd.Tests` lost `LnUnitCoexistenceTests` and its `LNUnit.LND` reference. `LNUnit` stays referenced by
  Integration.Tests only, and brings `lnunit.lnd` (global `Lnrpc`, ...) transitively; nothing uses it.
  `Fixtures/LnUnitConfinementTests` (non-Docker) fails when another source uses an LNUnit namespace or another
  project references the package.
- **Proof.** Release build 0 warnings, format, sln check; Integration.Tests non-Docker 1122/1122, Testing.Lnd.Tests
  106/106; Docker (machine lock) `PostgresTests` 24/24 and `ChannelOpeningFlowTests` 5/5, twice, no leftover
  containers.
- **Full removal (NL-820, open, owner decision).** Two ways. (a) Re-implement `DockerLndBackend` on Docker.DotNet
  (about 1-1.5 days): bitcoind `miner` on the default bridge (wallet, mature coins), four `custom_lnd:0.21.4-beta`
  containers named by alias with `LndWorkload`'s flags (alice's `--protocol.rbf-coop-close`/`--accept-keysend`),
  `tls.cert`/`admin.macaroon` read from the container's archive, `SERVER_ACTIVE` waits, funding, permanent peers, the
  pushed opens, the funders' policies and the graph wait of `LndRegtestNetworkSpec.Default` (the cluster's
  `LndRegtestNetwork` already does all of it, but on a `TestRun` topology with pod handles (pod IP/UID in the
  restart); sharing it needs a runtime seam under it), the restart as a plain container restart (the NL-262 address holds stay in the test), and the
  image build with `System.Formats.Tar` instead of LNUnit's SharpCompress tar. The container names, the bridge
  network and `host.docker.internal` must stay, because `ReestablishFlowTests`, `RelayBitcoind` and
  `RegtestBitcoinEndpoint` rely on them. (b) Retire the Docker LND backend once the cluster backend is the default
  for the LND, on-chain, gossip and ABCD suites (phase 6); the Tor fixture uses CLN only, so it does not need it. Either
  way the `LNUnit` reference goes, with it `lnunit.lnd` and SharpCompress 0.41.0, so the NL-170
  `NuGetAuditSuppress` in `test/Directory.Build.props` can go too.

### Phase 3/5/6 integration record (2026-10-03, `wip/harness-spike` from 7593fdda)

Merged with `--no-ff` in this order: `hf-lnd-wire` (e9305ee0), `hf-runner` (304c2976), `hf-lnunit` (b74efadf); review
fixes in dbf77b1e. Conflicts: `run-cluster.sh` takes the catalog-driven runner (hf-lnd-wire's `--suite lnd` case is
now the catalog's `lnd`); `LightningRegtestNetworkFixture` keeps hf-lnd-wire's backend selection and is backend-neutral;
`DockerLndBackend` combines hf-lnd-wire's members with hf-lnunit's image check and builder `Dispose()`.

- **The `lnd` suite split.** hf-lnd-wire proved 90 tests with `-parallel none` over four fixture collections; the
  catalog runs the `regtest` collection as `lnd` (2 namespaces with parallel collections: the network and
  `MultiNodeHarnessTests`' Postgres pod; `SuiteCatalogMembershipTests` keeps other collections out) and puts
  `PostgresTests` in `postgres`, `BackupRestoreFlowTests` in `onchain`, `ChannelPolicyPublicFlowTests` and
  `SpliceLndObserverTests` in `gossip`. Re-proven on the cluster below: 58 + 6 + 2 (+ postgres 24 from the runner
  proof) = the 90.
- **NL-821 (medium):** with the fixture wired, `--suite`/`--matrix` could run test code that drives Docker containers
  by fixed name (`LndChannelDbRollback` stops `david` and rewrites its `channel.db`; `RelayBitcoind` starts a container
  on the miner's network) on the cluster backend, outside the Docker lock, against another process's containers. Now
  `LightningRegtestNetworkFixture.SkipUnlessDocker`/`RequireDocker` skip those tests on the cluster (live: both skip
  with the reason), the remaining container-log dumps go through `DumpLndLogsAsync`, `GraphStoreFlowTests` dials
  `GetLndPeerEndpointAsync`, `onchain`/`anchors`/`gossip`/`abcd` carry `ClusterProofPending` (out of the default
  matrix, run when named) and `LndBackendProbe` needs `new ClusterLndBackend(` (hf-lnunit's intermediate fixture named
  `ILndNetworkBackend` but always built Docker).
- **NL-822 (low):** runner robustness: kubectl with `--request-timeout=15s`, a failed listing never read as "gone";
  the namespaces of ended suites (kept on failure, still terminating) count against the budget and a queue they block
  gives up (NOT RUN) instead of spinning; `--keep` refused with `--matrix`; Ctrl-C TERMs the test processes, KILLs them
  after 30 s, then reaps; `--suite` caps `-j` at 6 / its namespaces per run (`lnd`: 3).
- **NL-823 (low):** judgement: exit 3 when nothing ran or a named suite was skipped; a fixture failure (xunit v3
  reports "<kind> fixture type 'X' threw in ..." on every test of the collection or class, `errors` 0) is not rerun as
  a flake; only runs the summary calls green get their logs gzipped (`nltg-cluster matrix green-attempts`).
- **NL-824 (low):** guards that pinned nothing: `LnUnitConfinementTests` fails (instead of skipping) when the allowed
  file moved, catches `lnunit.lnd`'s global LND 0.20 namespaces in LNUnit-referencing projects and scans
  `.props`/`.targets`; the backends' host-address tests inject the environment and assert literals.
- Rejected: none. The finding "lnd may exceed its admission weight without `-parallel none`" holds only for
  hf-lnd-wire's four-collection selection; the catalog's `lnd` spans one fixture collection (above), so its weight of
  2 is right as planned.

Evidence (OrbStack, Release, net10.0):

| Run | Result | Time |
|---|---|---|
| `--no-incremental` Release build, `dotnet format`, `check-sln-configs.py` | 0 warnings, clean, OK | |
| Non-Docker suite (`FullyQualifiedName!~Docker&Category!=Cluster`, `--blame-hang-timeout 5m`) | 14,283 passed, 6 platform/explicit skips, 1 failure: `ClassificationEngineTests` "label regex matches" (NL-729, the known loaded-run regex timeout; class 53/53 alone) | |
| `scripts/tests/run-cluster-tests.sh` | 48/48 (new: fixture failure, exit-0 empty run, named skips, held namespaces, pending suite, lnd jobs cap, `--keep`, stop with KILL escalation) | |
| `run-cluster.sh -n 1 --suite lnd` (`hfi-lnd1`) | 58/58, 2 namespaces | 385 s |
| `--suite onchain --class BackupRestoreFlowTests` (`hfi-onchain-br`), `--suite gossip --class ChannelPolicyPublicFlowTests --class SpliceLndObserverTests` (`hfi-gossip-moved`) | 6/6, 2/2 | 64 s, 125 s |
| Docker-only guards on the cluster (`hfi-skip-relay`, `hfi-skip-o5`) | `AnchorsPackageRelayTests` and the O5 LND rollback skipped with the reason | 27 s each |
| Docker LND suite (SDK container, `--network host`, machine lock; hf-lnd-wire's selection) | 90/90; network ready 10.8 s | 502 s |

Still open: the cluster proofs of `onchain`, `anchors`, `gossip` and `abcd` (phase 6; then drop their
`ClusterProofPending`), NL-818 (log volume), NL-820 (LNUnit removal).

### Phase 6 ABCD lane record: the ABCD suite on the cluster (2026-10-03, `wip/harness-spike` from 3e31759f)

- `Docker/Abcd/` needed no change: since the phase 3 completion it reaches LND only through the backend-neutral
  `LightningRegtestNetworkFixture`, and bob and carol dial alice and david at `GetLndPeerEndpointAsync`. The lane only
  drops the catalog's `ClusterProofPending` for `abcd` (8dc1fb09), so the default matrix runs it; no product bug
  surfaced (NL-835..NL-839 unused).
- Evidence (OrbStack, Release, net10.0): `--suite abcd` 11/11 alone (`abcd-c1`, 58 s), `-n 2` 2 x 11/11 at once
  (`abcd-c2`, 64 s / 68 s), `--matrix abcd` green 11/11 (`abcd-mx1`, 51 s, fixture ready 27.8 s, 1/1 namespace);
  per class on the cluster: `AbcdRestartTests` 3/3, `AbcdPaymentTests` 2/2, `AbcdSendReceiveTests` 2/2,
  `AbcdReestablishTests` 1/1, `AbcdAccountingTests` 1/1, `OnceOnlyBuildTests` 2/2. Docker unchanged:
  `scripts/run-abcd.sh 1` under the machine lock 11/11 (network ready 9.9 s, 34 s). Matrix unit tests 76/76,
  `SuiteCatalogMembershipTests` green, `scripts/tests/run-cluster-tests.sh` 48/48 (its "pending suite runs when named"
  case still names `abcd`, which now simply runs as a proven suite).

### Phase 6 on-chain lane record: the on-chain and anchors suites on the cluster (2026-10-03, `wip/harness-spike` from 3e31759f)

- What the suites (`onchain`: `Docker.Onchain.Onchain*` + `BackupRestoreFlowTests`; `anchors`:
  `Docker.Onchain.Anchors`; one `onchain-regtest` network each) need from the chain and LND already went through the
  backend-neutral fixture: reorgs (`invalidateblock`/`reconsiderblock`, `generateblock`), `setmocktime`,
  `prioritisetransaction`, the mempool feeds (ZMQ raw tx at the miner's pod IP), LND 0.21's sweeper pacing
  (`ChainSync.MineUntilLndSweptAsync`, NL-770). The first cluster run (`oc-legacy-1`, `oc-anchors-1`) was 32/33 + 18/18
  with three tests skipped (the Docker-only helpers) and one real failure.
- NL-825 (fixed in 251feaa8): `OnchainO4Tests`' O4 (d) restarted our node on the files it had replaced instead of the
  snapshot it restored (`ours 2/1` at the reestablish, `RemoteCommitment` instead of `RemoteNextCommitment`).
  Microsoft.Data.Sqlite keeps closed connections pooled and open; on the macOS host, where the cluster backend runs the
  test process, `File.Copy` replaces a file by a new inode, so the pool kept reading the old one (the Docker runner is a
  Linux container, where the copy rewrites the inode in place). `NLightningTestNode` now clears its pool when it stops,
  as a stopped daemon process holds no file. A test-harness defect, not a product one; NL-826..NL-829 unused.
- The Docker-only helpers got cluster paths (4ab28ea7), so nothing skips any more: `LndChannelDbRollback` copies
  david's `channel.db` on his PVC in a stopped window (ee530155: `KubeNodeHandle.RestartAsync(readyTimeout,
  whileStopped, ct)` scales the StatefulSet to 0, runs a maintenance pod on the PVC, scales back; through
  `LndRegtestNetwork.RestartAsync(alias, whileStopped: ...)`, which also has the LND peers redial the new pod IP), and
  `RelayBitcoind` is a harness bitcoind pod `relay` (`-connect=miner:18444`, `-maxmempool=5`) in the network's
  namespace, removed with `RemoveNodeAsync`. Both suites lose `ClusterProofPending` (5c68c654); the planner tests take
  the pending suite from the catalog.
- Evidence (OrbStack, Release, net10.0, both suites at once beside the gossip lane's runs): `--suite onchain` 33/33
  (+2 `Explicit` not run; `oc-l3`, network ready 30.0 s, 312 s) and `--suite anchors` 18/18 (`oc-a3`, 41.4 s, 210 s);
  `--matrix onchain,anchors -j 2 --max-namespaces 2` green 33/33 and 18/18 (`oc-mx1`, matrix wall 336 s, fixture ready
  30.9 / 39.3 s, peak 2 namespaces). Per class on the cluster: `OnchainSmokeTests` 2, `OnchainO2Tests` 2,
  `OnchainO3Tests` 4, `OnchainO4Tests` 5, `OnchainO5Tests` 2 (+2 `Explicit`), `OnchainO6Tests` 3,
  `OnchainFinalHopTests` 1, `OnchainMempoolTests` 2, `OnchainWatchCatchUpTests` 1, `OnchainSpliceTests` 5,
  `BackupRestoreFlowTests` 6; `AnchorsChannelTests` 2, `AnchorsO3Tests` 3, `AnchorsO4Tests` 3, `AnchorsO5Tests` 1,
  `AnchorsCpfpTests` 2, `AnchorsMempoolPenaltyTests` 1, `AnchorsPackageRelayTests` 1 (relay mempool minimum raised by 41
  fills), `AnchorsPeerCommitmentBumpTests` 1, `AnchorsReserveTests` 2, `AnchorsReserveGapTests` 2. Docker unchanged
  under the machine lock: `ONCHAIN_SUITE=all scripts/run-onchain.sh 1` 45/45 + 2 `Explicit` not run (network ready
  9.5 s, 375 s; the rollback still copies the tar archive and the relay is still a container) and
  `BackupRestoreFlowTests` 6/6 (41 s). Unit tests: `StoppedNodeMaintenanceTests` 3, matrix 76/76, Integration
  `Cluster` namespace 27/27.

### Phase 6 gossip lane record: the gossip suite on the cluster (2026-10-03, `wip/harness-spike` from 3e31759f)

- The catalog's `gossip` (collection `gossip-regtest`, one network): `Docker.Gossip.*` without the Explicit `Capture`
  sub-namespace, `Docker.Day0.*`, `ChannelPolicyPublicFlowTests` and `SpliceLndObserverTests`, 35 tests (the Docker
  baseline's 30 of `run-gossip.sh`'s `Docker.Gossip` namespace plus the 5 day-0/observer/policy tests the integration
  moved into the suite). The bodies needed no change for the cluster: they reach LND only through the backend-neutral
  fixture, and `GraphStoreFlowTests` already dials `GetLndPeerEndpointAsync` (NL-821). The first run was green 35/35
  alone (`gsp-c1`, network ready 29.4 s, 1,158 s).
- NL-830 (test, fixed in b246306e): two runs at once (`gsp-c2`) were 35/35 and 34/35: `SpliceLndObserverTests` "bob
  still has 494x2x1 73 blocks later". The test waited only for alice's edge of the A-B open before splicing; the splice
  confirmed 7 s after the open's sixth block while bob, who hears the open only through alice's 5 s trickle, was still
  taking it in, and his graph closed 0 channels at the splice's block (alice's 1), so he kept the spent edge for good
  (LND 0.21's info log cannot show why; most likely the new edge's spend filter came after the spending block, whose
  re-scan the graph builder skips). The test now also waits for bob's edge on the open's outpoint before the splice,
  which is also what its step (4) proves him to forget. Not a product bug (the edge LND kept is LND's own
  graph state); NL-831..NL-834 unused.
- `gossip` loses `ClusterProofPending` (6f5b02ce): every LND suite now runs in the default matrix.
- Evidence (OrbStack, Release, net10.0, beside the on-chain lane's runs): the class 2 x 1/1 at once with the fix
  (`gsp-obs1`, 115-123 s); `-n 2 --suite gossip` 2 x 35/35 at once (`gsp-c3`, network ready 40.3 / 34.8 s, 1,173 /
  1,166 s); `--matrix gossip -j 1 --max-namespaces 2` green 35/35 (`gsp-mx1`, fixture ready 27.0 s, wall 1,166 s, peak 1
  namespace). Per class on the cluster (`gsp-c3` x 2, `gsp-mx1`): `GossipProofHelperTests` 8, `GossipFixtureTests` 2
  (62 s), `PublicChannelFlowTests` 4 (270 s), `GraphStoreFlowTests` 4 (155 s), `GossipSyncFlowTests` 3 (84 s),
  `PublicPaymentFlowTests` 3 (82 s), `RouteBlindingFlowTests` 4 (85 s), `Bolt11BlindedPathFlowTests` 2 (8 s),
  `Day0FlowTests` 1 (256 s), `Day0UpgradeInPlaceTests` 2 (35 s), `SpliceLndObserverTests` 1 (80-86 s),
  `ChannelPolicyPublicFlowTests` 1 (14 s). Docker unchanged under the machine lock: `scripts/run-gossip.sh 1` with the
  catalog's `-class` filters 35/35 (network ready 9.9 s, 1,140 s), the NL-830 wait included. Gates: Release build 0
  warnings, format, sln check; matrix unit tests 74 passed + 2 skipped (the pending-suite cases: none is pending),
  `SuiteCatalogMembershipTests` green, `scripts/tests/run-cluster-tests.sh` 48/48.

### Phase 6 proof record: the full matrix on the cluster (2026-10-03, `wip/harness-spike` from 16564892)

Every Docker-class suite but Tor ran as one matrix on OrbStack's cluster (`scripts/run-cluster.sh --matrix`), alone at
6 namespaces and twice at once at 3 namespaces each, before and after tuning the catalog. One harness user (this
job), Release, net10.0, `--no-incremental` build 0 warnings; logs and summaries in `TestResults/cluster/p6-*/`.

**Runs** (a matrix pass is 330 passed tests + 7 `Explicit` not run: lnd 58, onchain 33 (+2), anchors 18, gossip 35,
eclair 28 (+1), cln 90 (+4), abcd 11, ldk 27, faults 5, postgres 25):

| Batch | Catalog | Budget | Result | Matrix wall | Peak namespaces |
|---|---|---|---|---|---|
| `p6-full1` | 10 suites (before tuning) | `-j 6 --max-namespaces 6` | 10 green | 1,206 s (20.1 min) | 6 of 6 |
| `p6-twin-a` + `p6-twin-b` at once | 10 suites | `-j 3 --max-namespaces 3` each | 10 + 10 green | 2,229 s, 2,251 s | 3 of 3; 4 of 3 (1 terminating, NL-840) |
| `p6-tuned1` | 12 suites (NL-841) | `-j 6 --max-namespaces 6` | 12 green | **1,088 s (18.1 min)** | 6 of 6 |
| `p6-twin2-a` + `p6-twin2-b` at once | 12 suites | `-j 3 --max-namespaces 3` each | 12 green; 10 green + 2 rerun-green (NL-842, NL-843) | 2,087 s, 2,255 s | 3 of 3, 3 of 3 |

Two matrices at once take 35-38 min for two full passes, about the time of two passes in a row at 6 namespaces: the
budget, not the runner, sets the pace. No namespace was left after any batch; no test host was left.

**Suite wall times** (cluster: the suite's own wall in `p6-full1` / `p6-tuned1`, six suites in flight; Docker: xunit
time of the latest serial run of the same tests under the machine lock, one at a time):

| Suite | Tests | Cluster `p6-full1` | Cluster `p6-tuned1` | Docker | Docker source |
|---|---|---|---|---|---|
| lnd | 58 | 401 s | 428 s | 338 s | the regtest collection of the 90-test LND run (502 s), phase 3 completion |
| onchain + anchors | 33 (+2) + 18 | 345 + 231 s | 364 + 239 s | 375 + 41 s | `ONCHAIN_SUITE=all` 45 (+2) and `BackupRestoreFlowTests` 6, on-chain lane |
| gossip + day0 | 30 + 5 | 1,177 s (one suite) | 808 + 459 s | 1,140 s | `run-gossip.sh` with the catalog's classes, gossip lane |
| eclair + eclair2 | 21 (+1) + 7 | 1,141 s (one suite) | 747 + 465 s | 1,077 s | phase 3/4 proof |
| cln | 90 (+4) | 854 s | 902 s | 761 s | phase 3/4 proof (77 + 4; the 13 container-free tests tagged since take milliseconds) |
| ldk | 27 | 585 s | 624 s | 502 s | phase 3/4 proof |
| abcd | 11 | 53 s | 84 s | 34 s | ABCD lane |
| postgres | 25 | 49 s | 73 s | 30 s | `PostgresTests` 24 in the LND run (no Docker `ServerDatabaseClusterTests`) |
| faults | 5 | 126 s | 182 s | — | cluster only (new coverage) |
| **Sum** | | 4,962 s | 6,131 namespace-s | **4,298 s (71.6 min)** | |

Serial Docker pass: about 72 min of test time for these suites (batch10's serial pass of the same runners, 2026-10-02:
67.9 min wall from the LND suite's start to ABCD's end, with gossip then at 30 tests and 747 s). Cluster: 18.1 min,
**4.0x faster**, and the cluster pass also runs the partition tests and the server-database restart the Docker pass
does not have. A suite alone on the cluster is 1.1-1.5x its Docker time (fixtures ready in 13-56 s against 3-14 s on
Docker; six suites mining and paying at once on one VM); the gain is that six of them run at once and that two whole
matrices can run side by side, where the Docker suites run one at a time machine-wide. No Docker suite was rerun for
this record: every baseline above is from 2026-10-02/03 runs of the same tests.

**The ≈15 min target** is not reached at 6 namespaces (NL-844): `p6-tuned1` used 6,131 namespace-seconds (lnd holds 2
for 428 s, postgres 3, faults 2), so 6 namespaces cannot finish in less than 1,022 s; 1,088 s is within 7 % of that
bound, and a simulation of the admission queue with these times over 300,000 random orders of the 12 suites found none
below 1,052 s. 15 min needs about 7 namespaces (the machine has 28 cores and 64 GiB for the VM; the cap of 6 is an
owner rule), or shorter long poles (CLN 902 s is now the longest suite).

**Findings** (IDs NL-840..NL-844):
- **NL-840 (harness, fixed in 69fd5caa):** in `p6-twin-b` the faults suite held 3 namespaces under a count of 2 (one of
  them terminating; the batch's sampled peak 4 of 3): its classes run in parallel and `PartitionClusterTests` builds a
  topology per test, whose namespace was still terminating when the next test created one (`-faults-3`). The
  machine-wide admission held (the test logged "6/6 runs under nltg-spike; waiting for a slot"). `run-cluster.sh` now
  sets `NLTG_WAIT_NAMESPACE_DELETION=1` for every run, not only for `-parallel none`; after it no faults run used a
  third name and every batch peaked at its budget. (The summary's NS column counts a rerun's namespace too, so a suite
  with a rerun shows one more, by design: `MatrixReportTests` pins it.)
- **NL-841 (harness, fixed in 6e87db78):** `p6-full1`'s wall was gossip's (1,177 s) and eclair's (1,141 s), each one
  collection in one process, and the catalog's "longest first" order put onchain and anchors before them. The catalog
  splits them (`gossip` = `Docker.Gossip`, `day0` = `Docker.Day0.*` + `ChannelPolicyPublicFlowTests` +
  `SpliceLndObserverTests`; `eclair2` = `EclairSpliceTests`, `eclair` the rest; each its own process and topology) and
  orders `lnd` first (2 namespaces: the admission backfills, so it would wait for two free slots at once), then by
  measured wall time. 1,206 s → 1,088 s.
- **NL-842 (test, fixed in 47e9a2ea):** `PublicChannelFlowTests` G1 (b) in `p6-twin2-b`: alice's `OpenChannelSync`
  failed "not enough witness outputs to create funding transaction, need 0.01000000 BTC only have 0 BTC available".
  Alice's log shows her sweeper taking her two wallet outputs as fee inputs of HTLC-timeout sweeps of an earlier test's
  force close one second before the open. The test now has alice keep enough confirmed, unleased coins above her
  anchors reserve before she funds (`ChainSync.EnsureLndSpendableAsync`: two 0.1 BTC outputs from the miner and a block
  when short). Class reruns: 2 x 4/4 at once on the cluster (`p6-g1b`), 4/4 on Docker (`run-gossip.sh 1 -class ...`
  under the machine lock, 287 s); the funding branch by a throwaway probe (+0.2 BTC, then the early return); and in the
  final `gossip` run (`p6-final`, 30/30) the same state came back ("alice can spend -100000 sat, needs 1100000: funding
  it") and G1 (b) passed. The state depends on xunit's random test order, so the Docker backend could hit it too.
- **NL-843 (test timing, open):** `ChainMonitorZmqClusterTests` in `p6-twin2-b` (two matrices at once): "the block after
  the heal took 7.9 s (ZMQ not back?)" against 3 s; the class alone green (the runner's rerun), and green in the five
  other matrices' faults runs. Most likely ZMQ's slow joiner: the test mines as soon as the subscriber's TCP
  connection is back, and bitcoind's publisher drops what it publishes before it has read the subscription, so that
  block came by the 5 s tip poll. No assertion was relaxed; the failure message now also prints when ZMQ came back and
  the tip-poll catch-ups for that block, so the next occurrence tells a lost block from a slow one (2ee6e42f). Since:
  the class 2 x 1/1 at once (`p6-zmq`), faults 5/5 in `p6-final` (next block after the heal in 102 ms).
- **NL-844 (observation, open):** the 15 min target needs more than 6 namespaces (above).
- **NL-262 and NL-276: moot on the cluster backend, to be closed by the integrator.** NL-262 (a restarted LND container
  can come back at another address; the `nltg-address-hold-N` containers): on the cluster an LND restart is a
  StatefulSet restart that keeps the DNS name and PVC, the LND peers redial the new pod IP and our nodes the Service
  name; `ReestablishFlowTests` (alice restarts) passed without address holds in every matrix of this proof (6 x 3/3),
  as did the other restart tests (`AbcdRestartTests`, the O5 rollback's stopped window, Eclair, CLN and LDK restarts).
  NL-276 (the host process cannot reach Docker bridge IPs; concurrent Docker runs remove each other's containers): the
  cluster backend runs the test process on the macOS host and reaches every pod at its pod IP or Service name with no
  SDK container, and each test process has its own namespaces, so two whole matrices ran side by side. Both workarounds
  stay only in the Docker fallback (`DockerLndBackend`, the `run-*.sh` runners), which Tor still needs.
- NL-818 (log volume, open) update: a matrix pass is 1.1 GB after the runner's gzip, most of it `results.xml`, which
  holds every test's output again (214 MB for CLN) and is not compressed.

Gates: `--no-incremental` Release build 0 warnings, `dotnet format` clean, `check-sln-configs.py` OK,
`Testing.Cluster.Tests` matrix tests 74 + 2 skipped, `SuiteCatalogMembershipTests` 2/2,
`scripts/tests/run-cluster-tests.sh` 48/48, `Testing.Cluster.Tests` (`Category!=Cluster`) 673 + 2 skipped, Integration.Tests non-Docker
(`FullyQualifiedName!~Docker&Category!=Cluster`) 1,136/1,136. After the fixes `--matrix gossip,day0,faults -j 3
--max-namespaces 4` (`p6-final`): 30/30, 5/5, 5/5, wall 801 s, peak 4 of 4.

### Phase 3 completion, phase 5 and phase 6 record (2026-10-03, `wip/harness-spike` at f40d9416)

The summary of the work after the "Phase 3/4 record": the LND fixture on the cluster, the remaining suites ported, the
runner and the full-matrix proof. The records above have the details; this one says where things stand and what is
left for the owner.

**The LND wiring (phase 3 completion; `hf-lnd-wire` e9305ee0, `hf-lnunit` b74efadf, review fixes dbf77b1e).**
`LightningRegtestNetworkFixture` (collections `regtest`, `onchain-regtest`, `gossip-regtest`) keeps its members and
delegates to `Fixtures/Lnd/ILndNetworkBackend`, chosen by `NLTG_TEST_BACKEND` (Docker when unset):
`DockerLndBackend` (LNUnit's container builder, the only LNUnit user left, guarded by `LnUnitConfinementTests`) or
`ClusterLndBackend` (the warm `Topology/Lnd/LndRegtestNetwork`: miner + alice/bob/carol/david on
`custom_lnd:0.21.4-beta`, PVCs, LNUnit's channels, flags and policies). Every test reaches LND through the in-tree
client `NLightning.Testing.Lnd` (LND 0.21.4). Test nodes dial LND at `GetLndPeerEndpointAsync` (the Service name on
the cluster, NL-780), and test code that drove Docker containers by name got cluster paths: `LndChannelDbRollback`
rewrites david's `channel.db` in a stopped window on his PVC (`StoppedNodeMaintenance`), `RelayBitcoind` is a pod
(NL-821, 4ab28ea7, ee530155).

**The suite ports (phase 6 lanes).** With the fixture wired, the LND-based suites needed no change to their test
bodies beyond NL-821; each was proven alone, twice at once and in a matrix, against its Docker run under the machine
lock:

| Suite | Cluster | Docker | Found |
|---|---|---|---|
| `lnd` (regtest collection) | 58/58 alone (`hfi-lnd1`, 385 s); the moved classes 6/6 + 2/2 | 90/90 over all four collections (502 s) | — |
| `onchain` (Onchain* + `BackupRestoreFlowTests`) | 33/33 (+2 `Explicit`) alone, at once with anchors, matrix | 45/45 (+2) `ONCHAIN_SUITE=all`, `BackupRestoreFlowTests` 6/6 | NL-825 (harness) |
| `anchors` | 18/18 alone, at once, matrix | in the 45 above | — |
| `gossip` (+ `day0` since NL-841) | 35/35 alone, 2 x 35/35 at once, matrix | 35/35 with the catalog's classes | NL-830 (test) |
| `abcd` | 11/11 alone, 2 x 11/11 at once, matrix | 11/11 | — |

Earlier phases had already ported `cln` (phase 2), `eclair`, `ldk`, `postgres` and the new `faults` (phases 3/4).
Nothing skips on the cluster any more except `ClnTorInteropTests` (Tor stays on Docker).

**The runner (phase 5; `hf-runner` 304c2976, NL-816..NL-818, NL-822, NL-823, NL-840, NL-841).**
`scripts/run-cluster.sh --matrix [suites] [-j N] [--max-namespaces M]` is now the primary runner of the Docker-class
suites. It builds once, plans from the catalog (`nltg-cluster matrix list|plan`; 12 suites: `lnd`, `cln`, `gossip`,
`eclair`, `ldk`, `eclair2`, `day0`, `onchain`, `anchors`, `faults`, `abcd`, `postgres`; `tor` listed as skipped,
Docker only; SQL Server always left out), and runs `lnd` first, then the longest first, through an admission queue
within the namespace budget (cap 6). Each suite is one test process with a hang timeout. A slot is freed only once
the suite's namespaces are gone. A failed class is rerun alone (green = a named flake, red = a real failure;
fixture failures, timeouts and crashes are not rerun). The runner writes a summary with diagnostics folders and exits
1 on a real failure and 3 when nothing ran. `-n N --suite S` runs one suite N times. The Docker runners
(`run-{onchain,gossip,abcd,interop}.sh`, under the machine lock) stay as the fallback and for Tor. The runner logic is
tested without a cluster (matrix unit tests, `SuiteCatalogMembershipTests`, `scripts/tests/run-cluster-tests.sh`
48/48).

**The matrix proof (phase 6; "Phase 6 proof record").** One matrix pass is 330 passed tests + 7 `Explicit` not run,
over 12 suites.

| Batch | Budget | Result | Wall |
|---|---|---|---|
| `p6-full1` (10 suites, before tuning) | 6 namespaces | green | 1,206 s |
| `p6-twin-a` + `p6-twin-b` at once | 3 + 3 | green + green (4 of 3 peak, NL-840) | 2,229 s, 2,251 s |
| `p6-tuned1` (12 suites, NL-841) | 6 namespaces | green | **1,088 s (18.1 min)** |
| `p6-twin2-a` + `p6-twin2-b` at once | 3 + 3 | green; green after 2 flakes rerun green (NL-842 fixed, NL-843) | 2,087 s, 2,255 s |
| `p6-final` (gossip, day0, faults after the fixes) | 4 namespaces | 30/30, 5/5, 5/5 | 801 s |

Against Docker: the same suites one at a time on Docker take about 72 min of test time (4,298 s, the latest Docker
runs of 2026-10-02/03; batch10's serial pass 67.9 min wall). The cluster takes 18.1 min, **about 4.0x faster**, and also
runs the partition tests and the server-database restart that Docker does not have. Alone, a suite on the cluster
takes 1.1-1.5x its Docker time (fixtures ready in 13-56 s against 3-14 s). The gain comes from running six suites
at once, and two whole matrices side by side. NL-262 and NL-276 are moot on the cluster backend and closed. The
≈15 min target needs about 7 namespaces (NL-844).

**Product bugs found by the cluster.** The cluster found these product bugs, each fixed in the product with unit tests:

| Phase | Bug |
|---|---|
| Phase 2 | NL-477: CLN failed our `stfu` crossed by its fulfill; fixed with `TCP_NODELAY` on peer connections |
| Phase 2 | NL-775: the chain monitor never re-read bitcoind's tip; fixed by the tip poll `Bitcoin:TipPollInterval` |
| Phases 3/4 | NL-805/NL-810: a channel load torn by a concurrent save; fixed with one database snapshot per load, on `wip/fafo` |
| Phase 3 completion, phases 5 and 6 | None. Every finding was in the harness or the tests (NL-780, NL-816, NL-817, NL-821..NL-825, NL-830, NL-840..NL-842) |

Product items still open from the cluster work: NL-796 (no deadline for the peer's `channel_reestablish`) and NL-806
(a dead connection kept after a cluster Eclair restart; a keep-alive or OrbStack question).

**What remains:**
1. **LNUnit removal (NL-820, owner decision).** (a) Re-implement `DockerLndBackend` on Docker.DotNet (about 1-1.5 days;
   the container names, the bridge network and `host.docker.internal` stay), or (b) retire the Docker LND backend now
   that every LND suite is proven on the cluster (the Tor fixture uses CLN only). Either way the `LNUnit` reference,
   `lnunit.lnd`, SharpCompress 0.41.0 and the NL-170 audit suppression go. With (b) the Docker fallback of the LND,
   on-chain, gossip and ABCD suites ends.
2. **Tor** stays Docker only (owner decision): `ClnTorInteropTests` skip on the cluster and run with
   `run-interop.sh tor`.
3. **SQL Server** tests are not ported (owner decision) and every matrix suite leaves them out
   (`-trait- Database=SqlServer`). The migrations are still generated for all three providers.
4. **CI on a cluster** is deferred (owner decision). CI keeps running the non-Docker suite. The live cluster tests stay
   `Category=Cluster` + `Explicit`, and NL-180 (interop not in CI) stays open.
5. Open harness items: NL-818 (log volume: 1.1 GB per matrix pass after gzip), NL-843 (ZMQ heal timing, diagnostics
   in place) and NL-844 (the 15 min target; owner decision on a 7th namespace).
6. Later phases, as planned: 7 (the `nltg` daemon image and scale) and 8 (facade convergence, NL-554, NL-556).

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
