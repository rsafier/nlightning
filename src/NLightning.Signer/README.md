# Standalone signer prototype

The signer daemon owns the encrypted `SecureKeyManager` file and exposes the existing
C# signer over authenticated gRPC/HTTP2 on a Unix domain socket. It does not run the
node, connect to Bitcoin RPC, or load the node database. Linux and macOS are supported.

Create a private working directory and a random authentication token:

```sh
install -d -m 700 "$HOME/.nltg-signer/regtest"
umask 077
openssl rand -hex 32 > "$HOME/.nltg-signer/regtest/auth.token"
dotnet run --project src/NLightning.Signer -c Release -f net10.0 -- \
  --network regtest \
  --socket "$HOME/.nltg-signer/regtest/signer.sock" \
  --key-file "$HOME/.nltg-signer/regtest/node.key" \
  --auth-token-file "$HOME/.nltg-signer/regtest/auth.token" \
  --password-stdin --create
```

Supply the new key file's password as the first line of stdin from your secret
manager. Alternatively, use `--password-file /absolute/path` with a file readable
only by its owner. Passwords and tokens are never command-line values.
Remove `--create` on subsequent starts; it refuses to overwrite an existing identity.
The key format and derivation are the existing C# signer's format, including persisted
channel-key allocation. To migrate an existing key file, stop its original node before
copying it into the private signer directory. All processes must use the same key
file path for the daemon's exclusive sidecar lock to fence concurrent ownership.
Keep the adjacent `node.key.signer-state` journal with the key file: it preserves
channel signing guards and one-use signature responses across process restarts.
The daemon refuses a truncated or invalid journal; restoring only the key file can
roll back safety state and is not a safe channel recovery procedure.

For prototype provisioning into an instance that has no private key file, use:

```sh
your-secret-manager-read-seed | dotnet run --project src/NLightning.Signer -c Release -f net10.0 -- \
  --network regtest \
  --socket "$HOME/.nltg-signer/regtest/signer.sock" \
  --auth-token-file "$HOME/.nltg-signer/regtest/auth.token" \
  --seed-stdin --state-file "$HOME/.nltg-signer/regtest/injected.signer-state"
```

The producer writes exactly one line of 64 hexadecimal characters representing a
32-byte **BIP32 seed**. This is seed material, not the node's private key or an xprv:
the signer derives its standard BIP32 master and node key using the current C# scheme.
Inject the same seed on every restart. This mode never reads, creates or saves a private
key file and rejects `--key-file`, password options and `--create`. The bounded input
buffers are wiped after provisioning into `SecureKeyManager`'s protected memory.

The required state path still lives outside the instance's secret memory. Its adjacent
`.key-index` journal contains only a public-identity/network fingerprint and durable
channel allocation counters. These are local filesystem journals for the prototype;
Nitro will require an external storage transport through vsock. Keep both files:
lost/truncated state or a different
seed fails startup. Their integrity against a malicious host rolling both files back
still needs an external monotonic-state solution; stdin injection alone does not
provide enclave attestation or rollback protection. Future enclave provisioning can
replace this stdin producer with an attested KMS path while retaining the signer RPC.

The daemon prints `SIGNER_READY` only once its socket is listening. It accepts no TCP
listener or ambient Kestrel configuration. The socket and key directories must be
owner-only; the key, authentication token and password file must also be owner-only.
Ctrl-C/SIGTERM stops the daemon and removes its socket. After a crash, it refuses an
existing socket path: verify the previous process has stopped before manually removing
the stale socket. It never unlinks a preexisting socket or other file at startup.

Configure the node's remote signer with the same socket, network, and authentication
token, and pin its expected node public key to the value printed by `SIGNER_READY`.
See [the MVP runbook](../../docs/agents/REMOTE_SIGNER_MVP.md) for node configuration
and the complete local startup flow.
The current owner-only socket assumes both daemons run as the same operating-system
user; those processes can read each other's files and memory. A deployment with
separate users needs explicit socket access grants and separately provisioned private
token files. This MVP isolates keys and preserves existing C# signing checks; it is not
VLS policy validation or a production enclave deployment. Future vsock hosting can
replace the stream connector and listener without changing the signing operations.

## Recover an unknown RPC outcome

Calls have deadlines and no automatic retry. A transport failure does not prove an
operation failed: the signer may already have persisted it. Use
`RemoteSignerConnection.Prepare(operation, arguments)` to create an envelope,
store it securely before dispatch, and call `Execute(envelope)`. A
`RemoteSignerTransportException.Request` also preserves the exact failed envelope
for recovery while the caller remains alive. `Reconcile(envelope)` authenticates
and looks up the result without executing it. Changing its ID, operation or
payload defeats exact-outcome recovery; conflicting reuse of an ID is refused.

| Outcome | Action |
| --- | --- |
| `Completed` | Consume the stored response, or explicitly execute the identical envelope to retrieve it. The supported operation is not executed again. |
| `Unknown` | An allocation may have persisted in the separate key-index journal. Do not execute this ID again; it remains refused. Treat the allocated index as potentially used and reconcile the channel workflow before continuing. |
| `Invalidated` | Channel data-loss or retirement invalidated the old result. Recover the channel through its safety procedure; do not replay its old signature. |
| `NotFound` | No durable receipt exists for this supported operation. An explicit dispatch of the original envelope is allowed; this lookup did not execute it. |
| `Unsupported` | No durable outcome guarantee exists for this operation. Use its protocol recovery procedure; do not infer non-execution from a missing receipt. |

Durable receipts cover channel/funding safety mutations, holder broadcast
signatures, nonce-consuming close/splice signatures and channel-key allocations.
Normal commitment/HTLC signatures, taproot remote partial signatures, revealed
per-commitment secrets, channel commitment points and verification nonces also have
receipts for node workflow recovery. Restoring receipt-only operations reads their
saved responses without repeating cryptographic signing.
Ephemeral nonce creation is unsupported across restart because the secret nonce is
not restored. Nonce sessions bind decoded channel/public-nonce identities: equivalent
accepted base64 encodings cannot create another signing context. Restart refuses
conflicting historical nonce-session records. Safety changes and their completed responses share one fsynced journal
record. Allocations write a pending marker before touching the separate allocation
journal. Data-loss and retirement invalidate completed receipts for that channel;
unrelated allocation outcomes remain available.

The receipt store refuses new supported requests at 65,536 identities or its 64 MiB
response budget (reserving room for the largest response before execution). It never
evicts old identities to permit re-execution. There is no safe online compaction or
maintenance procedure yet: deleting the journal is unsafe. The node now captures
normal commitment, taproot reconnect and revocation-release workflows in its
database and reconciles before startup rollback. Locally completed results require
a matching signer receipt; `NotFound` in that case blocks rather than executes.
See [node recovery](../../docs/agents/REMOTE_SIGNING_RECOVERY.md) for proof scope.
Other application workflows, external state freshness and cloned writer fencing
remain separate work.


FAFO integration through `dd598216` adds remote wallet message signing, public named
deposit-account metadata and silent-payment output derivation with the complete frozen
reservation context. Silent-payment scanning/receiving and isolated key-ring/swap
operations remain unsupported in remote mode. `SilentPayments:Enabled=true` is
rejected at configuration checking and startup; private key-ring and silent-payment
spend-key export methods throw. This does not restrict the local backend's upstream
features.
