# Native remote signer P1: standalone channel demo

Working branch: `wip/native-remote-p1`, based on `wip/remotesigner` at `834a96a2`.
The owner requested implementation, compilation and limited focused testing on
the smaller machine, with heavy final testing and integration on a larger host.

## Outcome and trust boundary

Provide a repeatable regtest demo using actual node and native signer executables:
two independent daemon/signer pairs open channels against LND, pay both ways,
retain separate contexts/state/credentials, recover after signer interruption,
and close with peer and Bitcoin confirmation checks.

The node is trusted to request financial signing in this first pass. Independent
owner authorization is not required for this demo. Root, wallet and channel
private keys remain outside the node; authenticated signer access, durable
safety history, exact-request recovery and no local-key fallback remain required.
The existing restricted authority profile remains intact and cannot be silently
downgraded to prototype mode. This phase does not complete the
[four-milestone delivery goal](NATIVE_SIGNER_HOSTED_NODES_GOAL.md).

## Work product

- [Demo launcher](../../tools/native-remote-demo/README.md): Python standard-library
  commands initialize isolated configuration, run both pairs or one pair per
  foreground supervisor, query status and invoke the actual client. External
  private seed files feed only signer stdin. Seeds are never placed in node
  configuration or command arguments. Expected public identities are pinned.
- Outbound v1 recovery freezes the negotiated channel, exact `accept_channel`,
  opening nonce, unsigned funding transaction, ordered wallet inputs, fee and
  original reservation before registration/signing. Retained openings resume
  before startup repair; reconnect replays exact `funding_created` while waiting
  for acknowledgment. Funding replay validates the retained wallet/transaction
  and keeps the original reservation through atomic publication persistence.
- Focused executable lifecycle checks cover unchanged identity/history across
  graceful restart, monotonic allocation, missing histories, wrong seeds,
  authority downgrade and live/stale socket admission. The launcher never
  automatically deletes signer history or stale endpoints.
- `NativeRemoteP1DaemonChannelClusterTests` runs the shipped launcher and product
  IPC, with two explicit cases: cooperative closes and force-close recovery.
  Both include fractional-msat settlement, A interruption with B continuing,
  identity/funding retention and channel reestablishment after restart.

The launcher supports ordinary product feature defaults. The first live acceptance
uses explicit `--legacy-channels` configuration for static-remotekey v1 channels;
it does not establish anchors/taproot live coverage. Focused outbound recovery
tests separately exercise legacy and simple-taproot formats.

## Checkpoint verification

- Full Release/net10 solution build: zero warnings and errors. The final
  test-only corruption-fixture adjustment was then rebuilt against those current
  references, also with zero warnings and errors.
- Actual signer lifecycle and outbound recovery: 14 cases passed, zero failures,
  errors or skips (53.249 seconds). The four outbound cases cover both legacy and
  simple-taproot formats, original-request startup/restart replay, missing/blocked
  opening history and changed wallet evidence. The crash fixture asserts that
  opening intent and inputs committed before the first prepared-request save fails.
- Launcher: 12 lightweight cases passed (5.503 seconds), including simultaneous
  duplicate-identity admission and finite timeout validation.
- Integration assembly compiled and the explicit live proof theory was discovered.
  Neither live case was executed here; runner image creation remains pending.
- Solution configuration mappings: all 45 projects passed. Changed-file whitespace,
  C# encoding/end-of-file checks and runner shell syntax passed.

Full formatting, net11 compile checks and broad/live regressions remain gates for
the larger host. Local evidence does not establish full native lifecycle coverage.

## Larger-machine acceptance

Fetch this branch and read the repository/scoped instructions. Compile the final
source first; do not use old runner images or an old test assembly. Run net10
tests, net11 compile checks, formatting and solution configuration checks.

The bounded local checks can be repeated without Core or containers:

```bash
python3 tools/native-remote-demo/test_demo.py
dotnet test/NLightning.RemoteSigning.Tests/bin/Release/net10.0/NLightning.RemoteSigning.Tests.dll \
  -class NLightning.RemoteSigning.Tests.NativeP1SignerLifecycleTests \
  -class NLightning.RemoteSigning.Tests.NativeFundedOutboundOpeningRecoveryTests \
  -parallel none -noColor
```

```bash
dotnet build NLightning.sln -c Release -p:NltgTargetNet11=false -p:MSBuildWarningsAsMessages=MSB4121
dotnet format NLightning.sln --no-restore --verify-no-changes --exclude test/BlazorTests
python3 scripts/check-sln-configs.py

NLTG_RUNNER_PROJECT=integration NLTG_RUNNER_TAG=native-p1 \
NLTG_RUNNER_DOCKERFILE="$PWD/test/NLightning.Testing.Cluster/Runner/image/Dockerfile.native-p1" \
test/NLightning.Testing.Cluster/Runner/image/build.sh Release

# Import into the selected Kubernetes node's image store or configure a reachable image registry.
NLTG_RUNNER_IMAGE=nltg-spike-runner:native-p1 \
dotnet test/NLightning.Integration.Tests/bin/Release/net10.0/NLightning.Integration.Tests.dll \
  -explicit only \
  -class NLightning.Integration.Tests.Cluster.Live.NativeRemoteP1DaemonChannelInClusterRunnerTests \
  -parallel none -showLiveOutput -noColor
```

The optional runner Dockerfile installs Python 3 for the actual launcher. Ordinary
runner builds retain their existing Dockerfile. Provision the pinned Core/LND
images and Kubernetes prerequisites described by the harness before acceptance.
The wrapper must report one passing wrapper and exactly two inner cases with no
errors, failures or skips. Each inner case has a 25-minute deadline; the wrapper
job allows 55 minutes. `NLTG_KEEP_NATIVE_DEMO=1` retains private failure artifacts
including test seeds; leave it unset unless those artifacts are needed.

Final acceptance also requires:

- Broad native RPC and affected application/wallet regressions on the merged base.
- Actual node-process kills at outbound opening and acknowledgment boundaries,
  including prepared requests, signer commit before reply, locally saved receipt,
  consumed transition and pre-publication recovery.
- Altered peer inputs, missing/uncertain receipts, changed wallet evidence and
  concurrent admission must block without releasing inputs or replacing requests.
- The existing hosted forwarding proof, including 10,000,123-msat settlement and
  persisted accounting, plus the existing withdrawal and PSBT kill matrices.
- Final review and clean formatting before reporting P1 acceptance complete.

The implementation branch and focused results are a handoff, not a claim that
the live demo or complete native lifecycle inventory has passed. Standalone
PSBT signing returns, remaining on-chain/channel lifecycle capture, broader
independent authorization and writer/publication fencing remain in the coverage
inventory. This sprint adds no threshold provisioning or encrypted-backup feature.
