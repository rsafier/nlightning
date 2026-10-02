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
- `ClusterChainEndpoint`: the topology's bitcoind as a `RegtestBitcoinEndpoint` (RPC with the `miner` wallet, ZMQ raw
  block 28332 / raw tx 28333): by **pod IP** from the host, by headless Service name in the cluster. Read once: a test
  that restarts bitcoind must rebuild the node.

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
- `Live/InProcessNodeClusterTests` (`Category=Cluster`, `Explicit`, not under `Docker`, no Docker lock): CLN (dual-funded
  open, pay both ways, cooperative close) and LND (v1 open by the topology with a push, pay both ways, restart, pay,
  cooperative close). Run: `scripts/run-cluster.sh -n 3 -p integration --class
  NLightning.Integration.Tests.Cluster.Live.InProcessNodeClusterTests`.
- The CLN proof advertises `option_shutdown_anysegwit`: CLN v26.06.8 sends a P2TR `shutdown` script on a dual-funded
  channel without the option negotiated, which our default features refuse ("shutdown scriptpubkey is not a valid
  form"), and the close stalls.