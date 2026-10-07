# Remote C# signer MVP

Run the existing C# signing implementation in a separate daemon over authenticated
gRPC/HTTP2 on a Unix domain socket. The node keeps its database and wallet metadata;
the signer owns the private keys, channel allocation counter and signing-state journal.
Linux and macOS are supported. Start with regtest.

## Start the signer

Follow [the signer daemon README](../../src/NLightning.Signer/README.md) to create its
private directory and authentication token, then either unlock/create its encrypted
key file or inject a BIP32 seed through stdin. Wait for `SIGNER_READY`, and record its
node public key. For an existing node, stop it before moving its key to the signer;
keep the signer's state journal alongside its key or injected seed's allocation journal.

## Configure and start the node

Generate the node's default configuration without unlocking or creating a node key:

```sh
dotnet run --project src/NLightning.Daemon -c Release -f net10.0 -- \
  --network regtest --check-config
```

Edit `~/.nltg/regtest/appsettings.json`: set its `Bitcoin` RPC/ZMQ settings, enable
`Database:RunMigrations` for the initial database, and replace its `Signing` section
with the following. Replace `/home/alice` with your absolute home path and the public
key placeholder with the 66-character compressed public key from `SIGNER_READY`.
JSON does not expand `$HOME` or `~`.

```json
"Signing": {
  "Mode": "RemoteNative",
  "SocketPath": "/home/alice/.nltg-signer/regtest/signer.sock",
  "AuthTokenFile": "/home/alice/.nltg-signer/regtest/auth.token",
  "TimeoutSeconds": 15,
  "ExpectedNodePublicKey": "<compressed public key from SIGNER_READY>"
}
```

The node reads the same token file as the signer. It must contain at least 32 printable
ASCII characters and be owner-only (`chmod 600`). Socket and token paths must be
absolute. `ExpectedNodePublicKey` is optional, but pin it when migrating or retaining
channels to reject an unintended signer identity.

Check the configuration, then start the node while the signer is running:

```sh
dotnet run --project src/NLightning.Daemon -c Release -f net10.0 -- \
  --network regtest --check-config
dotnet run --project src/NLightning.Daemon -c Release -f net10.0 -- \
  --network regtest
```

`--check-config` validates the settings offline without reading the token or contacting
the signer. Normal startup authenticates and checks the network and pinned public key
before starting peers. Remote mode never unlocks/creates a local node key or prompts
for its password. A missing/unreachable signer fails startup; there is no local fallback.
The CLI and node configuration outside `Signing` retain their existing behavior.

## Verify the prototype

```sh
dotnet test test/NLightning.RemoteSigning.Tests -c Release -f net10.0
```

The tests launch real signer child processes and exercise identity, invoices, wallet
signatures, commitment/revocation checks, injected-seed restarts, journal corruption,
authentication and nonce reuse. Two payment proofs route Alice → Bob → Carol with
three separate injected-seed signer processes, covering ECDSA and taproot channels.
They use production handlers, onions and SQLite persistence; peer messages use the
existing harness FIFO links. The separate live LND proof below covers real Bitcoin/LND interoperability.

Initial MVP validation on 2026-10-07 with SDK 11 preview, using net10.0 for runtime tests:

| Check | Result |
| --- | --- |
| Full solution Release build, net10.0 + net11.0 | Passed, zero warnings |
| Signer Release.Native build, net10.0 | Passed, zero warnings |
| RemoteSigning.Tests | 29 passed |
| Infrastructure.Bitcoin.Tests | 2,076 passed |
| Bolt11.Tests | 343 passed |
| Infrastructure.Tests | 738 passed |
| Daemon.Tests | 1,689 passed |
| Integration.Tests, excluding Docker/SqlServer | 1,196 passed |
| Application.Tests | 4,364 passed; one timing-sensitive failure |

The application failure was
`Given_ALiveHtlcWhoseIdWasNotRecorded_When_ReconcilingAtStartup_Then_ItIsAttached`:
its stepped 50 ms deadline expired before the HTLC was offered during heavy build
load. An isolated rerun passed without code changes. The payment service and that
test's timing logic are unchanged.

Solution configuration and changed-file whitespace checks pass. Full formatting
identified the two existing `IDE0031` findings in `HtlcInterceptorHub.cs`; new
whitespace/import findings were corrected and the affected style checks passed.

## Follow-up proofs and research

The recovery tests withhold a reply on a real gRPC stream, independently confirm its
fsynced receipt, kill the signer child process, and retrieve the exact result after
restart. A separate test simulates interruption in the allocation callback to prove
that a pending allocation remains `Unknown` and its request ID cannot execute again.
Data-loss invalidation, exact request-ID conflicts, authenticated reconciliation and
exclusive same-state daemon ownership are also covered. The nonce identity regression
rejects an altered transaction after restart when the same channel is represented by
an equivalent base64 encoding, and refuses conflicting legacy journal history (NL-1195). This does not yet prove
reconciliation of a node database transition interrupted mid-payment.

The live proof opens a single-funded channel with LND 0.21.4, pays both ways before
and after sequential node/signer restarts, and confirms a cooperative close on both
ends. It uses real bitcoind and LND pods plus the production node service graph with
a separate injected signer process. Peer traffic uses BOLT 8 TCP. Run it through the
repository cluster runner on a host with pod routing:

```bash
scripts/run-cluster.sh -n 1 -p integration --class \
  NLightning.Integration.Tests.Cluster.Live.RemoteSignerLndClusterTests
```

For a host without pod routing, build and load a unique integration runner image,
then run its namespaced wrapper:

```bash
remote_signer_tag="remote-signer-$(date -u +%Y%m%d%H%M%S)"
NLTG_RUNNER_PROJECT=integration NLTG_RUNNER_TAG="$remote_signer_tag" \
  test/NLightning.Testing.Cluster/Runner/image/build.sh
# Load nltg-spike-runner:$remote_signer_tag into the selected cluster's image store.
NLTG_RUNNER_IMAGE="nltg-spike-runner:$remote_signer_tag" scripts/run-cluster.sh -n 1 -p integration --class \
  NLightning.Integration.Tests.Cluster.Live.RemoteSignerInClusterRunnerTests
```

Set the runner's `--context` for your cluster. Both proofs are explicit opt-ins;
unit-test skips do not count as live validation. See the
[cluster guide](../../test/NLightning.Integration.Tests/Cluster/CLAUDE.md) for setup.
This is a lifecycle interoperability proof; force close, pending HTLC recovery and
failures between node/signer persistence boundaries remain acceptance gates.

The [pinned VLS spike](VLS_COMPATIBILITY_SPIKE.md) exercises real VLS policy APIs
for static-remotekey and zero-fee anchors, including signature validation, revocations,
unauthorized HTLC refusal and explicit keysend authorization. It proves core API
compatibility with artificial funding fixtures and in-memory persistence. It does
not add a VLS backend to the node. Its derivation vectors confirm that mainnet node
identity equality under VLS LND style does not imply channel-key compatibility.

## Follow-up validation record

Verified on 2026-10-07 on `wip/remotesigner`, implementation commit `9d4e7b944ad16d9f6d2ae888b24200305a7ddf37`:

| Check | Result |
| --- | --- |
| Remote signing tests, including process failure, exclusive ownership and nonce identity regression | 36 passed, zero skips |
| Release solution build (.NET 10 and .NET 11) | Passed with zero warnings and zero errors |
| Formatting | Changed files verified; full-solution verification retains two existing IDE0031 findings in HtlcInterceptorHub.cs |
| Existing in-process node harness regressions | 23 passed |
| Live LND inner proof | 1 passed, zero skips; 67.324 seconds |
| Namespaced in-cluster wrapper | 1 passed, zero skips; 89.902 seconds |
| Pinned VLS core spike | Two locked runs passed with identical fixture output |

The live proof used Bitcoin Core 29.0 and LND 0.21.4-beta. It opened a 1,000,000 sat
channel with a 300,000 sat push, sent 200,000 sat to LND and received 50,000 sat back
before and after restarting signer and node, then verified gross balances of 400,000
and 600,000 sat. Both peers agreed on cooperative closing transaction
`2b59dd08edc52f73230cef01a02bf1a437e48f304e3e4e2e8b5b2a5ee9b10d8a`,
confirmed six blocks deep. The cluster runner reported one green run in 93 seconds;
all owned namespaces were removed. Local logs are under
`TestResults/cluster/rs-interop-20261007-c2/` (not committed).

Production follow-ups are tracked as NL-1190..NL-1194 in the
[issue ledger](ISSUES.md). The VLS fixture output and its pinned reproduction tooling
are committed under `tools/vls-compat-spike/`.

## Prototype limits

The proxy forwards the complete `ILightningSigner` surface and safe key-manager
operations: node ECDH, invoices, wallet public derivation, backups, peer storage and
offer path identifiers. Private-key export methods throw. The node supplies channel
and wallet metadata; the signer retains the existing C# checks and durable signing
guards, but does not independently validate chain state or enforce VLS policies.
Transport calls have deadlines and no automatic retries: a timed-out call may already
have executed. Supported operations now have durable receipts and explicit
`Prepare` / `Execute` / `Reconcile` recovery; see the signer README's
[recovery table](../../src/NLightning.Signer/README.md#recover-an-unknown-rpc-outcome).
The node does not yet persist pending envelopes or automatically reconcile its
database transitions. Follow the signer README's journal/restart constraints.

The owner-only socket currently assumes the same operating-system user for both
daemons. A split-user deployment needs explicit socket access grants and private token
provisioning for each user. This is a same-machine prototype, with a replaceable stream
connector for eventual vsock transport. Nitro
attestation, external rollback protection, provisioning and VLS are subsequent work;
see [the deployment research plan](REMOTE_SIGNING_PLAN.md).
