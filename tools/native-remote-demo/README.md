# Native remote signer demo

This launcher runs two standalone node daemons and two native C# signer daemons against an existing **regtest** Bitcoin Core. Each node has its own SQLite database, peer port, administrative IPC cookie, signer token, signing context and signer history. It uses prototype authority: the node is trusted to request financial actions. It is not a non-custodial hosting guarantee or a mainnet deployment recipe.

Requirements: Linux, Python 3.9+, .NET 10, built Release assemblies, a funded/mining regtest Core with transaction indexing. No containers are started by the launcher. Run everything as the same unprivileged Unix user. Keep the demo root short enough for Unix socket paths.

## Initialize

Copy `core.example.json` to an external private file, replace its three values, and `chmod 600` it. Choose a **new, nonexistent absolute root**; initialization never overwrites existing state. It generates transport tokens and configuration, but no signing seeds.

```sh
python3 tools/native-remote-demo/demo.py init \
  --root /tmp/native-p1 \
  --core-config /secure/regtest-core.json \
  --dotnet /absolute/path/to/dotnet \
  --signer-dll /absolute/repo/src/NLightning.Signer/bin/Release/net10.0/NLightning.Signer.dll \
  --node-dll /absolute/repo/src/NLightning.Daemon/bin/Release/net10.0/NLightning.Daemon.dll \
  --client-dll /absolute/repo/src/NLightning.Client/bin/Release/net10.0/NLightning.Client.dll \
  --base-peer-port 19735
```

For the bounded legacy force-close acceptance, append `--legacy-channels` to initialization. This writes `Node:Features:OptionAnchors=No` for both nodes; ordinary initialization retains product feature defaults.

Supply two distinct owner-held seeds, each exactly 32 random bytes encoded as one 64-character hexadecimal line in an external mode-600 file. Retain those original secrets securely for every subsequent activation. Neither the node configuration nor the demo manifest contains a seed. Seed file paths are passed to the supervisor; seed bytes go only through the signer child's stdin pipe. The prototype launcher reads them into its own memory briefly; it is not an enclave provisioning boundary or threshold-share implementation. It does not encrypt signer history or supply backups.

## Run and operate

For independent failure domains, use two terminals (or two process handles in the integration harness):

```sh
python3 tools/native-remote-demo/demo.py run --root /tmp/native-p1 --node a --seed-a-file /secure/a.seed
python3 tools/native-remote-demo/demo.py run --root /tmp/native-p1 --node b --seed-b-file /secure/b.seed
```

Alternatively, omit `--node` and supply both seed file arguments to supervise all four child processes together. Startup waits for signer history replay, pins the reported public identity into the node configuration under a shared private identity lock, rejects the same identity on both nodes, then waits for an authenticated product `info` request to succeed. `DEMO_NODE_READY` contains only public identity and endpoint information. Logs are private files in each node's directory.

```sh
python3 tools/native-remote-demo/demo.py status --root /tmp/native-p1
python3 tools/native-remote-demo/demo.py client --root /tmp/native-p1 --node a -- info
python3 tools/native-remote-demo/demo.py client --root /tmp/native-p1 --node a -- getaddress p2wpkh
python3 tools/native-remote-demo/demo.py client --root /tmp/native-p1 --node a -- connect PUBKEY@127.0.0.1:19736
python3 tools/native-remote-demo/demo.py client --root /tmp/native-p1 --node a -- openchannel PUBKEY 1000000 --v1 --no-wait
python3 tools/native-remote-demo/demo.py client --root /tmp/native-p1 --node a -- listchannels
```

Fund the returned wallet address with Core and mine confirmations before opening. Mine funding confirmations, create an invoice using the product client's `createinvoice` help, pay with `payinvoice`, and close with `closechannel`. Core/LND provisioning, mining and invoice parsing belong to the final integration harness; the launcher deliberately does not pretend that process readiness proves a channel/payment flow.

Ctrl-C or SIGTERM to the foreground supervisor stops only child processes it actually owns, nodes before signers, with bounded graceful shutdown followed by bounded termination if necessary. No PID-file-based signalling is used. Durable state is retained. Restart by issuing the same `run` command with the same external seed.

For the isolated outage proof:

```sh
python3 tools/native-remote-demo/demo.py stop-signer --root /tmp/native-p1 --node a
```

This writes a private control request to the matching live supervisor. That supervisor terminates its actual signer child and stops its node, then acknowledges completion. With separate `run --node` supervisors, B remains running. If a single supervisor owns both nodes, it stops both. It does not signal arbitrary recorded PIDs. Start A again with the same command and original seed. A stopped signer cannot cause a local-key fallback.

## Recovery safeguards and limits

The complete injected signer bundle is `history`, `history.key-index`, `history.enrollment`, `history.nonces` and `history.swap-sessions`, plus the retained node database/configuration and transport credentials. Once any history file exists, all must exist; an identity-pinned configuration also requires the full bundle. A missing or partial bundle blocks startup. The signer's exclusive lock and enrollment checks remain authoritative. Authority-profile markers are refused by this prototype launcher.

The launcher never removes sockets, databases or signer histories. If a crash leaves a socket, startup stops. An operator must establish that its owning process is stopped before performing explicit stale-endpoint recovery. Restoring an older complete backup is not automatically detected by this prototype; independent freshness authority and encrypted backup are separate unfinished work. Do not treat a seed-only restore as safe recovery for active channels.

`status` reports observed process identity using Linux PID start times; it is diagnostic, not a signing readiness assertion. There is no automatic restart, seed storage, crash reset or deletion command. Keep logs/state for the final acceptance pass, and retain the external seeds separately.

## Manifest contract for acceptance

`demo.json` version 1 contains `root`, `network` (`regtest`), absolute `dotnet`, `signerDll`, `nodeDll`, `clientDll`, and a `nodes` array. Each node contains `name` (`a`/`b`), `nodeId`, `ownerId`, `signerId`, `peerPort`, and absolute `signerState`, `signerSocket`, `signerToken`, `nodeConfig`, `nodeIpc`, `nodeCookie`, `nodeDatabase`. The public key is pinned in `Signing:ExpectedNodePublicKey` after startup, not stored in the immutable path manifest.

`<root>/<name>/runtime.json` records informational `supervisorPid`, `signerPid`, `nodePid`, their corresponding `*Start` values and `nodePublicKey`. The private control request/acknowledgement files bind a request to a specific supervisor PID and start time. For a C#-managed acceptance fixture, `pin --root ROOT --node a --public-key HEX` uses the same identity-pinning and complete-history checks, after the fixture has authenticated the signer itself.

Final acceptance on the larger machine must prove real channels, payments, isolated signer interruption, same-history restart/reestablishment, cooperative close, and on-chain confirmation. Python syntax/help or mocked processes are only launcher checks.
