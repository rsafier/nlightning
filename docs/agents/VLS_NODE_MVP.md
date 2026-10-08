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
revocations, channel close signing, and (NL-1335) public channels, withdrawals to
operator-allowlisted addresses and LND-style `signmessage`. Outbound push amounts,
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
Named-account/watch-only imports, PSBT wallet signing and single-SHA256
`signmessage` are unsupported. Private-key
export and arbitrary node-message signing throw; financial accounting period signatures have no generic signing escape
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

## Public channels, withdrawals and signmessage (NL-1335)

Each path uses VLS's purpose-specific API at the pinned revision; nothing falls
back to a local key.

- **Public channels.** `openchannel --public` and a peer's announced open pass the
  VLS gate, and the profile no longer forces `Gossip:AcceptPublicChannels` off
  (`AllowPublicChannelsOnMainnet` stays off; the prototype is regtest only).
  `channel_announcement` goes to the gateway's `sign_channel_announcement`: VLS's
  `Channel::sign_channel_announcement_with_funding_key` for the bitcoin signature
  and its node-key gossip signer for the node signature, as VLS's own
  SignChannelAnnouncement handler does. Both the node adapter (announce flag, data
  loss, confirmed short channel id, our node id, the peer and both funding keys)
  and the gateway (chain, node order, our node and the VLS channel's peer, funding
  keys and output index, data loss, closed channel) bind the announcement to the
  channel; the adapter verifies both returned signatures. `channel_update` and
  `node_announcement` were already typed VLS operations. Gossip v2 (taproot)
  stays off.
- **Withdrawals.** VLS has no withdrawal-specific API beyond its on-chain check:
  vlsd's `SignWithdrawal` is a PSBT wrapper over the same `check_onchain_tx` +
  `unchecked_sign_onchain_tx`. `withdraw` is therefore signed by the reserved-input
  wallet signing of the anchors lane (`VlsLightningSigner.Anchors.cs`,
  `wallet_sign_fee_inputs`, NL-1325), which passes the destination as a non-wallet
  output path. VLS accepts only outputs to its wallet (the change, by wallet path)
  or to an allowlisted destination, so the operator first allowlists the address
  on the approval socket (`approve.py allowlist --address ...`,
  `VlsWalletApprovalClient`); the node credential cannot. VLS keeps the allowlist
  in its node state across restarts. Its fee policy (`max_feerate_per_kw`, fee
  velocity) applies.
- **signmessage.** `ILightningSigner.SignLightningMessage` (LND's `SignMessage`)
  calls VLS `sign_message` (SHA256d of `"Lightning Signed Message:" || message`)
  and returns LND's header form; the adapter checks the signature recovers the
  node key. `single_hash` is refused: VLS has no single-SHA256 variant.

Withdrawal signing has a gateway receipt but, like funding, no node-owned
request-ID workflow yet (NL-1336).

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
Wallet funding, legacy mutual-close and force-close signatures are node workflows
since NL-1330 (below). Receipt compaction, deadline monitoring, transport
performance, vsock and attested seed provisioning remain deployment work. Keep
`NL-1307` open for these gates.

## Durable funding and close workflows (NL-1330, 2026-10-08)

`wallet_sign` (channel funding), `mutual_close` and `force_close` follow the same
ordering as the commitment workflows: the intent row and the original request
envelope are saved together before dispatch (no intent ever exists without its
request ID; one that lost its request is blocked), the exact receipt is saved before
it is used, the workflow is consumed in the save of the node transition, and only
then is anything published or sent.

- Funding: consumed in the `V1FundingSigned` save with the funding broadcast row and
  watches; the inputs stay reserved meanwhile. At registration an interrupted funding
  replays the saved envelope (reconcile, else the same envelope under the same ID)
  and rebuilds the signed transaction from the receipt's witnesses.
- Legacy mutual close (simple close is disabled in VLS mode): each `closing_signed`
  signature is consumed before the message can go out; the agreed one in the
  `Closing` save with the closing transaction and its watch. A restarted negotiation
  of a `Closing` channel answers with our signature read from the stored transaction,
  never a new VLS request. An interrupted signature is replayed under its original ID
  at registration and retired: it was never sent, and the negotiation restarts on the
  next connection.
- Force close: the intent is bound to the commitment number and unsigned txid and
  consumed in the `Failed` save with the `LocalCommitment` row. Retries and resumed
  failures republish that row's exact bytes; registration completes an interrupted
  force close with the original request, then publishes.

Proof: `VlsFundingCloseCrashTests` kills the node process (real SQLite, actual Rust
gateway restarted too) at intent+envelope, gateway committed before the reply,
receipt saved, consumed with the transition and after publication, for funding,
both mutual-close signatures and force close (16 cases), plus 5 refusals (altered
receipt, altered envelope, lost request) that block without signing. Recovery keeps
every original request ID and receipt; funding converges to a confirmed channel with
payments both ways, the close to both peers `Closing` on one transaction that is
script-valid against the funding output, the force close to a script-valid commitment.
Run it with the other explicit process proofs:
`dotnet test/NLightning.RemoteSigning.Tests/bin/Release/net10.0/NLightning.RemoteSigning.Tests.dll -explicit only -class NLightning.RemoteSigning.Tests.VlsFundingCloseCrashTests -parallel none`
(`ChildEntrypoint` is the worker and fails when started by itself). The live
`VlsSignerLndClusterTests` ran 3/3 green on these paths from the host (batch
`rc-20261008175852`).

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

- NL-1335 (2026-10-08, `wip/vls-public`; the withdrawal results below were taken
  with this lane's own `wallet_sign`-based signer, since replaced by the anchors
  lane's identical VLS path, so re-run both proofs after merging NL-1325):
  `VlsPublicSigningTests` 3/3 against the pinned gateway (message signature recovered to the node key, single hash refused;
  withdrawal refused for an unknown destination, another reservation, a non-wallet
  input and a node-credential allowlist, then signed for the allowlisted address,
  accepted by NBitcoin's script interpreter and again after a gateway restart; a
  public harness channel's announcement refused while private, then both
  signatures verified by the gossip verifier, a wrong short channel id refused, and
  swapped funding keys refused by the gateway itself). Live:
  `VlsSignerPublicLndClusterTests` 1/1 (batch `rc-20261008173423`): LND learned our
  public channel with both policies and our `node_announcement` by gossip, LND's
  `VerifyMessage` accepted our VLS signature (valid, our pubkey), a withdrawal to a
  non-allowlisted address was refused, and after the operator's allowlist Bitcoin
  Core accepted and confirmed the VLS-signed withdrawal 6 deep (250,000 sat out,
  change back to a VLS change address).

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
