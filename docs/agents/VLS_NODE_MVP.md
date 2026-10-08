# Fresh-node VLS prototype

Branch: `wip/remotesigner`. Canonical VLS revision:
`cb8a64c71d3b214951e752281f05b9090e77f074`.

The FAFO edition has one user and no prior-version compatibility requirement.
Use a fresh VLS identity, wallet and database. Existing native channels and keys
are not imported. Once channels exist, preserve both node and signer state and
inject the same seed on every signer restart.
Reinjecting the seed restores keys, but does not restore VLS policy history;
the durable signer database is also required.

## Start the signer and node

Build and provision the [Rust gateway](../../tools/vls-gateway/README.md).
Its seed is exactly 32 raw bytes delivered on stdin; credentials are separate,
owner-only files. The node receives only the node credential. Keep the approval
credential with the operator tool.

```json
{
  "Signing": {
    "Mode": "Vls",
    "SocketPath": "/private/vls-gateway/node.sock",
    "AuthTokenFile": "/private/vls-gateway/node-token",
    "TimeoutSeconds": 15,
    "ExpectedNodePublicKey": "REPLACE_WITH_COMPRESSED_VLS_PUBLIC_KEY"
  },
  "Node": { "Network": "regtest" }
}
```

Replace the public-key placeholder with the gateway identity before checking the
configuration. `nltg --check-config` validates offline; starting the node requires
a live authenticated gateway. VLS mode never creates or unlocks a local signing
key and never falls back to the native signer.

This gateway uses newline-delimited JSON over Unix sockets. The C# native remote
signer continues to use gRPC over Unix sockets. VLS's semantic adapter is a
separate backend; neither transport is a Nitro/vsock implementation.

## Supported prototype profile

The host suppresses unsupported features before negotiation and validates the
profile after options configuration. The initial profile is regtest, private,
single-funded static-remotekey ECDSA channels. Regtest is this adapter
prototype's acceptance boundary, not a restriction imposed by VLS upstream. It supports node ECDH, BOLT 11,
typed v1 gossip payloads, P2WPKH wallet funding, semantic commitments and peer
revocations, and channel close signing. Public channels, outbound push amounts,
anchors, taproot, dual funding, splicing, zero-conf, simple close, gossip v2,
BOLT 12, blinded/onion-message features, peer storage and native backups are
outside this profile. Dust HTLCs are unsupported by this pinned VLS phase-two
accounting path: the host advertises a 1,000-satoshi minimum HTLC and sets zero
allowed dust exposure. **Millisatoshi amounts are supported** (owner decision
2026-10-08): the gateway runs VLS's default policy, whose node-wide balance check
(`enforce_balance`) is off on every VLS network default, so balances, HTLC amounts
and forwarding fees (base and proportional) keep their exact millisatoshis.
VLS still checks every payment against its invoice or routed counterpart, the
routing-fee cap and the CLTV bounds; it sees commitments in whole satoshis, as the
outputs are. Making `enforce_balance` work with sub-satoshi values would need an
upstream VLS core change and is not planned.
Pre-offer, incoming-add and fee-update checks refuse
trimmed HTLCs before applying updates without weakening VLS balance enforcement.
Wallet withdrawals, named-account/watch-only imports and
`signmessage` are unsupported. Private-key export and arbitrary node-message signing
throw; financial accounting period signatures have no generic signing escape
hatch. Ordinary accounting event hash chains remain supported.

VLS Native wallet derivation uses the public account and a single child index
`2 * addressIndex + (change ? 1 : 0)`. The node defaults to a supported P2WPKH
address in VLS mode; explicit unsupported address requests fail. Wallet signing
validates the input path and script before calling VLS.

## Payment approval

Use the separate [approval tool](../../tools/vls-approve/README.md) to approve a
signed BOLT 11 invoice or a keysend hash/payee/amount before paying normally.
The node credential cannot grant approval. Separate OS identities and access
controls are needed to isolate approval credentials from a compromised node;
owner-only sockets under one UID do not provide that boundary. The gateway checks invoice signature, currency,
expiry and amount, and persists authorization with policy state and its exact
receipt. Reuse the same request ID when recovering an uncertain approval.

For keysend, use the same operator-controlled 32-byte preimage for approval and
the CLI's `--preimage-file`; never place a preimage in a shell argument. Forwarded
HTLCs are evaluated under VLS's balance policy. Fulfilled preimages are persisted
with the resulting policy state so restart preserves the admission history.

## Durable ordering and recovery

The node saves peer-bound channel allocation identity and the original request
envelope before dispatch. Allocation receipts and the BOLT channel mapping are
immutable. Startup checks persisted channels against the same VLS identity,
network, allocation and exact receipt.

Opening saves holder commitment zero before activation; VLS then permits wallet
funding signing. The signed funding transaction and watch state are saved before
publication. Normal signing, holder validation, activation and revocation use
durable workflow IDs. Receiving a peer revocation binds its secret, number and
next point. Revocation release occurs after the corresponding channel save.

Recovery reconciles the original envelope. An absent receipt is distinct from
an unknown or invalidated outcome; the latter fail closed. A completed operation
must return the identical saved bytes. Partial opening that lost its negotiation
context is refused rather than allocating new request IDs. Sticky data-loss and
broadcast guards apply before receipt replay and use canonical channel IDs.
An exact incoming HTLC-add retransmission during pending holder validation is
acknowledged without applying it twice. Changed payloads close the connection;
other duplicate adds retain normal protocol rejection.

## Remaining deployment gates

The local Redb transaction commits VLS policy state and immutable receipts before
replying. This does not establish external rollback protection or cloned-writer
fencing. The regtest policy does not independently track/validate the chain.
Force-close broadcast acceptance is distinct from complete output recovery:
HTLC claims, penalties, delayed sweeps and anchors need a separate adapter proof.
Wallet funding and close calls have gateway receipts, but their original request
IDs are not yet persisted as complete node application workflows. Unknown
funding/close outcomes therefore remain a deployment gate. Mutual-close and
force-close application recovery beyond the tested cases,
receipt compaction, deadline monitoring, transport performance, vsock and attested
seed provisioning remain deployment work. Keep `NL-1307` open for these gates.

## Millisatoshi accounting (resolved 2026-10-08)

VLS core counts HTLCs and routed payments in satoshis (`HTLCInfo2.value_sat`,
`RoutedPayment`). Its per-payment check (`validate_payment_balance`) compares
floored values, which a forward or an invoice payment never makes unbalanced; only
the opt-in node-wide `enforce_balance` sees the floored residual as a shortfall.
The gateway now keeps VLS's default (`enforce_balance` off) and the whole-satoshi
guards are removed (`wip/vls-msat`). Proofs: `VlsChannelHarnessTests`
fractional case (both directions, HTLCs in flight both ways, exact msat balances,
peer-verified commitments) and the live `VlsSignerLndClusterTests` (fractional
payments both ways across restarts with cooperative and force close, and an
LND -> VLS -> LND forward of 200,000,123 msat with a 1,001 msat + 1,234 ppm fee
settled with the exact fee), all green on OrbStack from the host. The gateway
also makes accepted connections blocking: on BSD/macOS they inherit the
listener's `O_NONBLOCK`, which dropped requests still in flight.

## Validation record

- Full Release solution build passes for .NET 10 and .NET 11 with zero warnings
  and errors; the full formatter check and solution configuration checks pass
  across all 45 projects.
- The exact pinned Rust gateway process proof passes (1 case).
- Four C#-to-Rust gateway cases, the real mixed-HTLC harness, and nine actual
  node/signer crash-recovery cases pass, with no skipped cases. The mixed harness
  checks both-direction equal-value HTLCs, signature ordering, converged balances
  and refusal before mutation. Recovery checks original request IDs and exact
  receipt bytes through prepared, completed, consumed and revocation boundaries.
- Final focused checks pass: daemon 1,802; channel factory 48; incoming and
  outbound operations 52; startup profile and receipt policy 13. Three SQLite
  mapping persistence cases also pass.
- Native Bitcoin/wallet regression: 2,467 pass, three skip, two explicit cases
  not run. Native/default remote regression: 70 executed cases pass; explicit
  VLS proofs run separately.
- The broad Application run executes 4,575 cases: 4,574 pass and one accounting
  classification case fails (`FinancialBooksProjectorTests.Given_ARuleAddedAfterAClose_When_Projected_Then_OnlyTheOpenPeriodIsReclassified`).
  That exact case passes alone. Classification's
  100-ms regex timeout is suspected load sensitivity (see NL-729); the cause was
  not established by the failure output, and accounting code was not changed.
- Final live LND acceptance passes all three actual cases with zero skips or
  cases not run. Cooperative and force closes follow payments in both directions,
  node/signer restart and further payments; Bitcoin Core confirms at least six
  blocks and the exact original funding input. LND → VLS → LND forwards a
  200,000,000-msat payment; the persisted fulfilled circuit binds distinct input
  and output channels and the actual forwarded amounts. Together with local
  proofs, 17 actual C# VLS cases pass. Force-close output sweeps remain unproved.

Final live runner: `nltg-spike-runner:vls-signer-20261008-d`, config SHA
`f3f621252ff3cf7af5d98e6700360ea10383fc845dde109d6221a90536adee70`.
Pinned gateway binary SHA256:
`3f93bd1c07ca9018b2271266273f71f171295233492597a6af8ef26bec3c564d`.
The successful final batch is `rc-20261008023830`. Cooperative transaction:
`c7a0e99534429916bfccdd51aa27530029ffca106158ab45dbf878b8d28faeb0`;
force-close transaction:
`c7d31e078d5b06b9782c4404dd4726dccca3a200ae274ce4475af76a57c58f24`.

Reproduce the local process proofs after building the solution and pinned gateway:

```bash
tools/vls-gateway/run.sh test
export NLTG_VLS_GATEWAY_BINARY="$HOME/.cache/nlightning/vls-gateway-cb8a64c71d3b214951e752281f05b9090e77f074/run/target/debug/nlightning-vls-gateway"
dotnet test/NLightning.RemoteSigning.Tests/bin/Release/net10.0/NLightning.RemoteSigning.Tests.dll -explicit only -class NLightning.RemoteSigning.Tests.VlsGatewayProcessTests
dotnet test/NLightning.RemoteSigning.Tests/bin/Release/net10.0/NLightning.RemoteSigning.Tests.dll -explicit only -class NLightning.RemoteSigning.Tests.VlsChannelHarnessTests
dotnet test/NLightning.RemoteSigning.Tests/bin/Release/net10.0/NLightning.RemoteSigning.Tests.dll -explicit only -method NLightning.RemoteSigning.Tests.VlsNodeWorkflowCrashTests.Given_DurableVlsWorkflow_When_NodeIsKilled_Then_OriginalRequestsRecoverAndPeersConverge
```

For live acceptance, stage the freshly built integration assembly and that same
Rust binary with `NLTG_RUNNER_PROJECT=integration` and a unique `NLTG_RUNNER_TAG`,
import the runner into the selected cluster, then use:

```bash
NLTG_RUNNER_IMAGE=nltg-spike-runner:YOUR_TAG scripts/run-cluster.sh --no-build -n 1 -p integration --class NLightning.Integration.Tests.Cluster.Live.VlsSignerInClusterRunnerTests --context YOUR_CONTEXT --timeout 1500
```
