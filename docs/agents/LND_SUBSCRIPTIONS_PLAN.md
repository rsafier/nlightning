# LND passive subscriptions

Owner request 2026-10-06: add the five missing passive LND event feeds, on
`wip/lnd-subscriptions` from `wip/fafo` 45f71673. The pinned protobuf surface is LND
0.21.4-beta. These are live subscriptions, with no durable event log or replay cursor.
Existing invoice/payment subscriptions and interactive ChannelAcceptor/HtlcInterceptor
remain available.

## Surface

| RPC | Source and behavior | Macaroon permission |
|---|---|---|
| routerrpc.SubscribeHtlcEvents | New committed commitment transitions: SEND/FORWARD offers, settles, downstream failures, clear local refusals, interceptor outcomes, and incoming final removals. Initial SubscribedEvent matches LND. | offchain:read |
| Lightning.SubscribePeerEvents | Accepted initialized sessions online; current session removed or replaced offline. Stale disconnects cannot remove a replacement session. | peers:read |
| Lightning.SubscribeChannelEvents | Persisted pending/open lifecycle, usable/inactive links, confirmed close and fully resolved channel, funding timeout, and committed channel updates. Reestablish readiness drives usability. | offchain:read |
| Lightning.SubscribeTransactions | Canonical wallet discovery, accepted wallet broadcasts, committed confirmation and rewind to unconfirmed. Net satoshi amount includes the fee; raw transaction, ownership, block and label metadata. | onchain:read |
| Lightning.SubscribeChannelGraph | Authoritative graph additions, node announcements, directed policies, funding points, spent edges, removals and reorg restoration. V2 routing-policy preference matches DescribeGraph. | info:read |

## Delivery and recovery

Each subscriber owns a bounded queue (default 1,024), with nonblocking publication.
A full queue cancels blocked writes and terminates only that stream with
RESOURCE_EXHAUSTED. Reconnect and reconcile current state through the existing read
RPCs; neither a restarted daemon nor a disconnected reader can replay missed events.
Observer callbacks cannot make primary peer/channel/chain/graph operations fail.
RPC cancellation detaches every event handler. Non-HTLC streams send empty response
headers after attaching their hooks, allowing a client to establish a readiness barrier
without introducing synthetic events.

HTLC observation is separate from the operational domain event queue. Operational
startup/link-up replay remains idempotent and does not publish fresh passive activity.
A bounded independent FIFO worker enriches captured HTLC observations using its own
fresh scope. Offers capture their origin and paired forward metadata before asynchronous
processing; the worker retains attribution until removal, so fast payments do not race
operational pruning. Neither metadata reads nor subscriber cancellation callbacks run
on the channel funds path. An observation metadata failure ends current HTLC readers
explicitly while allowing the already committed transition to reach the wire and
operational switch. Reader sets are captured at enqueue, preventing delayed delivery
to a later subscriber. The pending queue is capped at 1,024 and attribution caches at
8,192; overflow is explicit. Cancellation callbacks run asynchronously.
Pending/channel callbacks exclude startup loads and uncommitted dual-funding state.
Graph events describe the authoritative in-memory graph; its existing database
write-behind remains asynchronous.

## Limits

- BOLT 5 on-chain HTLC outcome replay has no durable first-publication marker. It is
  not published as new passive HTLC activity. On-chain confirmations/spends remain
  available through ChainNotifier. Adding on-chain final/timeout events without
  recovery duplicates needs a separate durable checkpoint.
- Trampoline fan-in has no single paired circuit; an outgoing observation can have
  event_type UNKNOWN. Incoming final events are UNKNOWN and incoming-only, matching
  LND. A receive does not generate a synthetic forwarding event.
- Failure detail reflects what the node actually knows: downstream failure onions
  remain opaque; local clear failures expose their BOLT code. No external decision
  audit log is added.
- SubscribeTransactions covers the canonical wallet. Imported-only tapscript
  transactions remain covered by GetTransactions and ChainNotifier, but are absent
  from this passive feed until imported history has a push/indexing source (NL-1197).
- Incoming unconfirmed discovery follows the configured monitor. ZMQ observes it;
  Poll mode emits accepted own broadcasts and confirmations, without promising
  discovery of all incoming mempool transactions.
- LND graph messages cannot express a standalone removed node or all taproot-specific
  extension fields; the existing LND graph representation is retained.

## Verification

Implementation and proof record: `ae737a97` on `wip/lnd-subscriptions`.

Final validation 2026-10-07:

- Release solution build on net10.0 and net11.0: zero warnings/errors. Standard tests
  ran on net10.0 only, with a five-minute hang timeout; SQL Server and Docker suites
  were excluded from the broad unit run. Solution configuration check: 40 projects.
- Full solution formatting verification passed, followed by verification of the final
  follow-up files; staged diff and shell syntax checks passed.
- Final focused suites: Application publication/startup/closing/peer/channel/graph/HTLC
  checks 190/190; Bitcoin normal and Poll monitor checks 78/78; LND gRPC 210/210,
  including seven real TLS/generated-client subscription tests.
- Initial broad run across 12 test projects: 17,248 passed, 16 failed, three skipped.
  Thirteen failures were startup tests still expecting AddChannel instead of the new
  silent LoadChannel path; two were zero-input synthetic wallet fixtures rejected by
  NBitcoin's default serializer. All were corrected and cleared by the focused reruns.
  The remaining failure was the existing NL-1198 graph timing assertion under load;
  its isolated rerun passed (770 ms test duration). A second complete broad run was
  not performed.
- Final real LND cluster proof `lnd-subs-proof2`: 1/1 green, 86 seconds, all owned
  namespaces cleaned. The generated LND client observed peer=4, channel=77, graph=21,
  wallet=9 and HTLC=15 through the production TLS/macaroons endpoint. An earlier
  proof also passed before the wallet fixture compatibility fixes.

The wallet publisher captures the transaction hash before publication and never
reparses raw bytes on the committed chain-processing path. Synthetic zero-input
objects use NBitcoin's non-witness serializer; real transactions preserve their
normal witness serialization.
The focused real cluster proof extends LndGrpcWave3FlowTests with actual generated
LND clients against the node's TLS/macaroons endpoint. It subscribes before funding
and peer connections, then exercises channel acceptance, forwarding FAIL/RESUME,
interceptor SETTLE, canceled downstream invoice failure, direct send/receive, wallet
transactions, public graph announcements and cooperative channel close.
Unit/source tests cover post-save publication, failed-save exclusion, recovery/revert
behavior, bounded fanout, slow-client overflow, observer isolation, initialized peer
sessions, reestablishment, staged-vs-committed channel updates, graph policy versions,
wallet discovery/confirmation/reorg, authorization and RPC cancellation cleanup.


### Reproduce the real proof

On a host with pod reachability, use the regular Wave3 class:

```bash
scripts/run-cluster.sh -n 1 --suite lnd \
  --class NLightning.Integration.Tests.Docker.LndGrpcWave3FlowTests --no-build
```

For a cluster that cannot route pods to the workspace host, build a fresh runner
image from the current net10 Release output and load it into that cluster:

```bash
NLTG_RUNNER_TAG=lnd-subscriptions-local \
  bash test/NLightning.Integration.Tests/Cluster/LndSubscriptions/image/build.sh
# Load nltg-spike-runner:lnd-subscriptions-local into your cluster's image store.
NLTG_RUNNER_IMAGE=nltg-spike-runner:lnd-subscriptions-local \
  scripts/run-cluster.sh -n 1 -p integration --no-build \
    --class NLightning.Integration.Tests.Cluster.Live.LndGrpcSubscriptionsClusterTests
```

The explicit wrapper allocates an owned namespace and scoped runner RBAC, runs the
same Wave3 class in a Job, and requires both a successful exit and the all-five-feed
proof marker. It preserves the regular host execution path. The image build refuses
an existing tag to prevent stale or replaced proof images. Pinned LND/Core images
must already be available to the cluster. No FAFO node is involved.
