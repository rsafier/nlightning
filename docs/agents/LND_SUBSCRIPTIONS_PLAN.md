# LND passive subscriptions

Owner request 2026-10-06: add the five missing passive LND event feeds, on
`wip/lnd-subscriptions` from `wip/fafo` 45f71673. The pinned protobuf surface is LND
0.21.4-beta. These are live subscriptions, with no durable event log or replay cursor.
Existing invoice/payment subscriptions and interactive ChannelAcceptor/HtlcInterceptor
remain available.

## Surface

| RPC | Source and behavior | Macaroon permission |
|---|---|---|
| routerrpc.SubscribeHtlcEvents | New committed off-chain transitions plus checkpointed known BOLT 5 outgoing outcomes and incoming finals: SEND/FORWARD offers, settles, downstream failures, clear local refusals, interceptor outcomes, and incoming final removals. Initial SubscribedEvent matches LND. | offchain:read |
| Lightning.SubscribePeerEvents | Accepted initialized sessions online; current session removed or replaced offline. Stale disconnects cannot remove a replacement session. | peers:read |
| Lightning.SubscribeChannelEvents | Persisted pending/open lifecycle, usable/inactive links, confirmed close and fully resolved channel, funding timeout, and committed channel updates. Reestablish readiness drives usability. | offchain:read |
| Lightning.SubscribeTransactions | Canonical wallet discovery and accepted broadcasts, plus committed canonical/imported tapscript confirmations and rewinds. Ownership is merged by input/output identity without double-counting. Net satoshi amount includes the fee; immutable raw transaction, ownership, block and label metadata. | onchain:read |
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

Off-chain HTLC observation is separate from the operational domain event queue. Operational
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

- BOLT 5 known outgoing settle/fail outcomes and resolved ordinary incoming HTLCs
  publish through the same passive source. Incoming final events set `Offchain=false`;
  a peer timeout or ignored incoming output is failed, and our confirmed claim is settled.
  Positively identified trimmed incoming HTLCs in the force-close metadata publish a
  failed final once the close reaches `Node:Onchain:ReasonableDepth`. Unknown/unmapped
  data-loss outputs and missing trimmed metadata (including when accounting recording
  was disabled) do not produce invented outcomes. Revocation penalties and second-level
  sweeps do not independently imply ordinary incoming settlement.
- `OnchainHtlcObservations` durably checkpoints channel/direction/HTLC/outcome in the
  resolution's own save. Fanout happens after that save to the captured reader set;
  operational switch callbacks continue replaying independently. Checkpoints survive
  restarts, reorgs and replaced closes. A later preimage after an earlier failure is a
  distinct known outcome and can notify once. This remains a live feed, without payload
  history, cursors or an outbox: a crash after commit and before fanout can lose a live
  notification, and a reorg does not retract an earlier HTLC event. ChainNotifier provides
  chain confirmation/spend updates.
- Trampoline fan-in has no single paired circuit; an outgoing observation can have
  event_type UNKNOWN. Incoming final events are UNKNOWN and incoming-only, matching
  LND. A receive does not generate a synthetic forwarding event.
- Failure detail reflects what the node actually knows: downstream failure onions
  remain opaque; local clear failures expose their BOLT code. No external decision
  audit log is added.
- SubscribeTransactions includes imported-only tapscript deposits and spends from the
  durable incremental index. Changes publish after its checkpoint save and the canonical
  wallet batch's exact completed height/hash marker. The processing marker invalidates
  prior readiness, including reorgs returning to the same tip. RPC index catch-up retains
  immutable changes until canonical observations can be joined; rewind markers handle
  a fresh tracker, and queued old confirmations are superseded by disconnects. Shared
  canonical/imported ownership is unioned before computing net amount, fees and details.
  First/reconnected readers initialize a checkpoint without replaying earlier history.
  Imported and canonical pending queues are bounded at 8,192; index failures or backlog
  overflow explicitly end affected subscriptions for reconciliation and reconnect.
  The source does not add imported-only mempool discovery, a replay cursor or a durable
  event outbox. A crash between index commit and fanout can lose live notifications.
- NL-1187's indexed wallet-history query limits sealed rows by wallet kind and height
  and suppresses reversed rows through derived references. Since NL-1187's completion
  the chain monitor also writes the wallet's durable history (`WalletTransactions`,
  migration `AddWalletTransactions`) in each block's save from the same description
  SubscribeTransactions publishes (raw transaction, block hash and time, wallet output
  indexes, wallet inputs with their values), independent of the accounting feed's gate;
  a rewind's save makes its rows unconfirmed. GetTransactions merges that history, the
  sealed feed (history from before the table), the outputs held since before the
  accounting cutover, pending broadcasts, unconfirmed deposits and the imported tapscript
  history by output index and spent outpoint, so a shared canonical/imported output or
  input counts once (NL-1253 fixed). A row a reorg unconfirmed is listed unconfirmed only
  while it is our pending broadcast, an unconfirmed deposit or in bitcoind's mempool.
  `total_fees` follows btcwallet: our broadcast row's fee, else inputs less outputs when
  every input is the wallet's, else 0. Remaining (NL-1289): transactions whose wallet
  outputs were all spent before the accounting cutover need a wallet rescan; a
  pre-cutover send whose change is still held is listed only when bitcoind returns its
  parents; history known only to the feed (before the table existed) still reads its raw
  transaction and block hash from bitcoind.
- Incoming unconfirmed discovery follows the configured monitor. ZMQ observes it;
  Poll mode emits accepted own broadcasts and confirmations, without promising
  discovery of all incoming mempool transactions.
- LND graph messages cannot express a standalone removed node or all taproot-specific
  extension fields; the existing LND graph representation is retained.

## Verification

The waves 1 and 2 follow-up on `wip/fixes-waves1and2` from `wip/fafo` 6d166c2e
implements NL-1231/NL-1232 and the indexed-query portion of NL-1187. Provider migrations
add passive HTLC checkpoints and derived wallet-history reference/index columns without
rewriting sealed accounting payloads or hashes. Added coverage includes actual SQLite
commit/reorg/restart and migration backfill tests, HTLC save failure and replay tests,
RPC catch-up/canonical join ordering, and generated-client imported/final-hop regtest
proofs. Final validation on 2026-10-07:

- Release solution build for net10.0 and net11.0: zero warnings/errors. Tests ran on
  net10.0 with a five-minute hang timeout. Affected Application areas: 1,697/1,697;
  full non-Docker/non-SqlServer/non-cluster Integration: 1,214/1,214; Bitcoin:
  2,164 passed and three platform skips; backend 114/114; LND gRPC 224/224.
- Initial broad Application failures were three nonce-fixture cases (responder setup
  produced no initial messages), cleared by the final affected-area run. Five initial
  Integration failures were the mutable snapshot, typed null upgrade parameter, and
  three provider comparer-root checks; all cleared in the full final Integration run.
- Real cluster proof `fixes-waves-proof1`: 1/1 wrapper, 35/35 inner tests, 157 seconds,
  all owned namespaces cleaned. The generated client saw all five streams and the
  imported deposit, mixed spend, rewind and reconfirmation. A real final-hop on-chain
  claim emitted `Offchain=false`, then a same-database restart and resolver replay
  emitted no duplicate. PostgreSQL passed the seeded historical-feed upgrade,
  preserving hashes and sequence while backfilling reversal references.
- Full solution formatting verification and staged diff checks passed.
- All three migrations and compiled models are included. SQL Server container tests
  were not run. Solution configuration check: 40 projects. No live-node activation.

Implementation and proof record: `ae7234e8` on `wip/fixes-waves1and2`.
The following validation belongs to the original five-feed implementation.

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
