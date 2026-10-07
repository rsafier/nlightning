# Lightning Node Connect bridge

## Goal and architecture

Let an LNC-capable remote client use NLightning's LND-compatible gRPC API through
an encrypted mailbox connection. The node can remain behind NAT: both the bridge
and client initiate outbound connections to the mailbox relay.

```text
LNC client <== PAKE/Noise encrypted gRPC ==> mailbox relay <==> Go bridge
                                                               |
                                                   TLS + backend macaroon
                                                               |
                                                     NLightning LND gRPC
```

The Go bridge lives in `tools/lnc`. It reuses Lightning Labs' upstream mailbox and
Noise implementation rather than introducing a second protocol implementation.
The relay transports encrypted traffic; it does not terminate the node RPCs.
The bridge terminates the LNC transport and forwards RPCs to the configured
NLightning backend. NLightning remains responsible for implementing RPC behavior
and validating the backend macaroon.

This is a transport adapter, not an embedded Lightning Terminal. It does not
provide Terminal's accounting, Loop, Pool, or application-specific APIs. Client
compatibility depends on which backend RPCs that client calls. The existing
`signrpc.Signer` endpoint is not needed for the LNC handshake; the bridge owns
separate transport identity keys.

## Implementation and operating contract

- Persist one transport identity and authorization per pairing session.
- Print a pairing phrase only when creating a session, through an explicit local
  operator action; do not log it during normal serving.
- Accept the upstream client's initial version 0 offer and negotiate handshake
  version 2, which carries the persistent remote identity. Bind pairing to the
  first authenticated remote identity and persist it before
  accepting subsequent calls. Restart must retain that binding.
- Issue dedicated URI-scoped backend macaroons with an expiry caveat. The bridge
  requires the exact session credential and closes the transport when local
  expiry or revocation is observed. The backend enforces its RPC permissions.
- Mark revocation locally before contacting the backend, so an offline backend
  cannot prevent local cutoff. Retry revocation to finish deleting the session
  root key. Expired roots remain until manually revoked.
- Use verified TLS to the backend and the mailbox. Backend authentication must
  come from the bridge's session credential, never untrusted client metadata.
- Forward unary, server-streaming, client-streaming and bidirectional RPC traffic
  without requiring generated service wrappers for every LND method. Preserve
  cancellation and backend status codes. Bound each message to 32 MiB in either
  direction (`RESOURCE_EXHAUSTED` for larger frames).
- Keep session files and transport keys private and avoid credentials in process
  arguments or logs.

Backend dials time out after 20 seconds. A mailbox listener owns its relay gRPC
channel and closes it when the session transport stops, including expiry and
revocation. The dedicated `.github/workflows/lnc.yml` gate checks formatting and
module integrity, runs vet and race-detector tests, and builds the executable.

The actual command interface and runnable setup instructions are maintained in
[`tools/lnc/README.md`](../../tools/lnc/README.md).

## Verification matrix

| Area | Required evidence |
| --- | --- |
| Transport | Upstream LNC client pairs through a mailbox and calls the bridge |
| Backend | A real NLightning regtest backend answers requests over TLS/macaroons |
| Money operations | Invoice creation and payment against a real regtest peer |
| Server streams | Real-node subscriptions traverse the encrypted LNC transport |
| Bidirectional forwarding | Generic opaque proxy round-trip unit proof; actual ChannelAcceptor/HtlcInterceptor client trial remains separate |
| Authentication | Client metadata cannot substitute another backend credential |
| Permissions | Restricted session denies a write while allowing its reads |
| Lifecycle | Expiry and revocation terminate active calls and deny new calls |
| Persistence | Restart retains session identity and first-client binding |
| Errors | Backend status, cancellation and stream completion reach the client |

## Verification record

Implementation commit: `478976a1` (NL-1237). Final checks passed on `wip/lnc`, based on `wip/fafo` `dff5931f`:

- Go: 19 top-level tests passed with the race detector (7.243 s), plus
  `go vet ./...`, `go mod verify`, and clean gofmt output.
- .NET: full solution Release net11.0 build passed with zero warnings or errors
  (4m57s). The final Integration.Tests fixture compiled for both net10.0 and
  net11.0 with zero warnings or errors (6m3.62s). C# formatting verification passed.
- Real-node regtest: `lnc-proof3` passed 1/1, 130 s outer run and 117.487 s inner
  proof. The test namespaces were cleaned afterward.

The regtest fixture is
`test/NLightning.Integration.Tests/Cluster/Live/LncClusterTests.cs`. It used a
local Aperture v0.4.0 mailbox relay and the upstream LNC client against an actual
NLightning backend over TLS/macaroons and LND 0.21.4-beta peers. It proved pairing
over gRPC and WebSockets; read-only write denial; invoice subscription and an
incoming LND payment; an outgoing SendPaymentV2 payment stream; bridge restart
with the same paired identity; active HTLC subscription cutoff on revocation;
revoked and expired session reconnection denial; and continued access through
another session.

The Go proxy tests separately cover generic bidirectional forwarding, headers,
trailers, status, authentication, cancellation and message size bounds. The
real-node proof exercised encrypted server streams. A live encrypted
ChannelAcceptor or HtlcInterceptor trial remains separate. Active-stream expiry
cutoff is implemented but the real-node expiry check exercised reconnection denial.

The verified image was `nltg-lnc-runner:lnc-proof2`, digest
`sha256:4309b5f9ebeed3d6f75468547eaa28a24646b85e449f9dd4c64e0d21cd2beb53`.
The tag names the image build; `lnc-proof3` names the final test run using it.

### Reproduce regtest

Build the Integration.Tests project in Release for net10.0 first, sequentially
with other .NET builds. Use a cluster with the harness bitcoind and LND images
already available and working Go, Docker, and kubectl tools:

```bash
dotnet build test/NLightning.Integration.Tests -c Release -f net10.0
NLTG_LNC_RUNNER_TAG=lnc-proof-local bash test/NLightning.Integration.Tests/Cluster/Lnc/image/build.sh
# Import nltg-lnc-runner:lnc-proof-local into your cluster image store if needed.
NLTG_RUNNER_IMAGE=nltg-lnc-runner:lnc-proof-local scripts/run-cluster.sh \
  -n 1 -p integration \
  --class NLightning.Integration.Tests.Cluster.Live.LncClusterTests \
  --no-build --id lnc-proof-local --timeout 20m --keep-logs
```

The image build script refuses to overwrite an existing tag. Choose a fresh tag
for each changed binary. On the managed kind environment, source
`/workspace/nlightning-setup/activate-loop.sh` and import the image with
`/workspace/nlightning-setup/load-image.sh nltg-lnc-runner:lnc-proof-local`; these
paths are environment setup helpers, not portable repository commands.

A Signet or Mutinynet trial is a separate operator task on the user's machine;
no live node activation is part of this change.

## Packaging

Build and run the bridge as a separate Go executable under the same service
supervisor as the node. Prefer a loopback backend listener when they run on the
same host. Keep session state in an operator-selected private directory and
back it up with its permissions intact: it contains credentials and transport
keys. The backend TLS certificate is a trust anchor, while the TLS private key
and macaroon root key remain in the node's data directory.

Container deployment can mount the node certificate and session state into a
bridge container with a private backend network. A cluster proof runner image is
test infrastructure and is not a production deployment recipe.
