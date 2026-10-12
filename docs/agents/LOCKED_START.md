# Locked start and key provisioning (NL-1349)

A locked start runs the daemon from an image or directory that holds no key material. The node waits, with nothing but a
provisioning endpoint open, until a key provisioner delivers its key; then it checks the key against the node's identity
and starts exactly as a normal start does. Normal starts (key file on disk, `--password-file` and the other password
sources) are unchanged.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `--locked` or `Node:Startup:Locked` | false | Start locked. |
| `Node:Startup:Provisioner` | `Socket` | `Socket` (the Unix socket below) or `Stdin` (frames on stdin, answers on stdout; the log goes to stderr until the node starts; not with `--daemon`). |
| `Node:Startup:SocketPath` | `<configPath>/provisioning/key.sock` | Absolute path, at most 103 bytes (`sun_path`). |
| `Node:Startup:FailureDelayMilliseconds` | 1000 | Wait after a refused key before the next attempt is read (0-60000). |
| `Node:Startup:RequireProvisionedSecrets` | false | Never write a secret the node would generate (NL-1352, below). |

`Node:Startup` is read by hand (`Configuration/StartupOptions`), not bound. `--check-config` validates it when the start
is locked. A locked start refuses `Signing:Mode` other than `Local`, any password option and `NLTG_PASSWORD`. A key file
left in the configuration directory is ignored (warned). `--status`, `--stop` and `--check-config` work as before; a
SIGINT/SIGTERM while locked ends the wait (exit 0) and removes the socket.

While locked nothing else runs: no peer listener, chain monitor, signer, IPC pipe (`nltg.ipc` and its cookie do not
exist, so every IPC command fails to connect), LND gRPC, LN backend or Cashu processor. With `--daemon` the process
daemonizes first and the child opens the endpoint.

## Endpoint

The socket's directory is created 0700 (an existing one must already be owner-only and not a symbolic link), the socket
is 0600. A socket file left by a daemon that died is replaced only when nothing answers on it; any other file there is
refused. The socket is removed when the node is unlocked or the wait ends. Up to 4 connections are served at once; a
socket connection must send each request within 60 s.

Transports implement `Provisioning/IProvisioningEndpoint` (`UnixSocketProvisioningEndpoint`,
`StdioProvisioningEndpoint`); a vsock endpoint goes behind the same interface (NL-1350).

## Protocol

One frame each way per exchange (`Daemon.Contracts/Provisioning/KeyProvisioningProtocol`): the 4 bytes `NLKP`, version
byte `1` or `2`, a big-endian u32 body length (at most 1 MiB) and a UTF-8 JSON body (camelCase, source-generated, AOT
safe). Version 2 (NL-1352) adds the file secrets and `returnLndGrpcMacaroons` below; everything else is version 1. A
request is written as version 1 unless it uses a version 2 field, so a newer client still unlocks an older node; an older
node refuses a version 2 frame ("Unsupported key provisioning version 2") before it unlocks, instead of ignoring secrets
it does not know and writing their files. The node answers in the request's version and refuses version 2 fields in a
version 1 frame. (Optional fields in a version 1 frame were rejected for that reason: System.Text.Json skips unknown
properties, so an older node would have started and generated the files the secrets were meant to replace.)

Request:

```json
{ "kind": "unlock", "material": "encrypted-key-file", "keyFile": "<base64 of the key file>", "password": "...",
  "secrets": { "databaseConnectionString": "...", "bitcoinRpcUser": "...", "bitcoinRpcPassword": "...",
               "torControlPassword": "...", "torOnionServiceKey": "ED25519-V3:<base64>",
               "lndGrpcMacaroonRootKey": "<base64 of 32 bytes>",
               "lndGrpcTls": { "certificate": "<PEM>", "key": "<PEM>", "clientCa": "<PEM, optional>" },
               "cashuProcessorTls": { "certificate": "...", "key": "...", "clientCa": "..." },
               "lnBackendTls": { "certificate": "...", "key": "...", "clientCa": "..." } },
  "returnLndGrpcMacaroons": true }
```

`{ "kind": "status" }` asks for the state only. Response:
`{ "ok": true|false, "state": "locked"|"unlocked", "network": "regtest", "nodeId": "<hex, once unlocked>", "error": "...",
"lndGrpcMacaroons": { "admin": "<base64>", "readonly": "...", "invoice": "..." } }` (the macaroons only when asked).
A malformed frame or body is answered with an error that never echoes the body.

## Key provisioners

`Provisioning/IKeyProvisioner` yields `KeyProvisioningAttempt`s, each carrying a `ProvisionedKeyMaterial` and optional
secrets and answered through `RespondAsync`. The development provider, `EndpointKeyProvisioner`, reads the protocol
above from an endpoint and takes an **encrypted NLightning key file plus its password**, not a raw seed: the key file's
version decides the derivation and so the node id (v1/v2 legacy, v3 BIP32; NL-158, NL-159, NL-211). Later providers (an
attested KMS unwrap, an operator import encrypted to an attested ephemeral key) yield their own material types through
the same interface (NL-1350).

## Unlock sequence

For each attempt, `Provisioning/LockedStartup`:

1. Opens the material in memory: `SecureKeyManager.FromKeyFileContent` decrypts exactly as a start from disk would, but
   writes nothing (a v1 file is not upgraded and no backup is made). A wrong password, another network or a malformed
   file is refused with that reason.
2. Checks the public key index file `<configPath>/nltg.key-index` (`KeyIndexFile`: `nltg-key-index 1 <network> <node
   public key> <last used channel key index>`), read-only: a file of another node key refuses the key.
3. Builds the node's service graph (`AddNltgNodeServices`, never started) over the configuration with the delivered
   secrets laid over it, and checks read-only that the database is not enrolled to another node key
   (`NodeSigningEnrollmentStore.IsEnrolledToAnotherAsync`, only once the schema has the enrollment table).
4. Runs the configured migrations and the signing enrollment validation of a normal start (`NodeSigningEnrollmentExtensions`):
   a new database enrolls, an enrolled one must match, one from before enrollment is adopted when its channels are this
   key's (NL-1340). Such a database is migrated before its adoption check (NL-1351).
5. Answers `ok` with the node id, closes the endpoint, and hands the key to `Program`, which builds the host and runs the
   same `MigrateDatabaseIfConfiguredAsync`/`ValidateNodeSigningEnrollmentAsync`/`RunAsync` sequence as a normal start.

A refused key keeps the node locked; the database and the configuration directory are unchanged (tests compare every
file's hash), and the next attempt is read after `FailureDelayMilliseconds`.

## Key material at rest

The key stays in memory (the key manager's protected allocation); no key file, plaintext or password is written. The
only file a locked node adds is `nltg.key-index`, public data written atomically 0600 on the first channel key
reservation; the larger of its index and the delivered file's is used, so a key file that never changes (it is never
rewritten in a locked start) cannot hand out a used index (SECURITY_REVIEW SR-19). Passwords and key bytes are never
logged; the delivered key file's buffer is wiped after use (the password is a managed string, as in a normal start).

## Secrets bundle (NL-1352)

The unlock request may carry every secret the node otherwise reads from disk or its configuration, so a locked node can
run with no secret at rest (the audit and the security notes are `SECURITY_REVIEW.md` SR-39):

| Field | Replaces | Use |
|---|---|---|
| `databaseConnectionString` | `Database:ConnectionString` | configuration, in memory |
| `bitcoinRpcUser`, `bitcoinRpcPassword` | `Bitcoin:RpcUser`, `Bitcoin:RpcPassword` | configuration, in memory |
| `torControlPassword` (v2) | `Node:Tor:ControlPassword` | configuration, in memory |
| `torOnionServiceKey` (v2) | the file `Node:Tor:OnionServiceKeyFile` | handed to Tor's `ADD_ONION`; the file is neither read nor written |
| `lndGrpcMacaroonRootKey` (v2) | `<lnd-grpc>/macaroons.key` and the baked `*.macaroon` | root key id 0 in memory; no macaroon file is written; other root key ids (`BakeMacaroon` with `root_key_id`) are refused |
| `lndGrpcTls` (v2) | `<lnd-grpc>/tls.cert`, `tls.key`, `LndGrpc:ClientCaPath` | the server certificate (and client CA) from memory |
| `cashuProcessorTls`, `lnBackendTls` (v2) | `TlsDirectory/server.pem`, `server.key`, `ca.pem` | the server certificate and client CA from memory (`TlsDirectory` may be unset) |

Each value is checked before the key is opened (`Provisioning/ProvisionedSecretsReader`): the root key is base64 of 32
bytes, a certificate or CA is PEM or base64 DER, a key is PEM (any label `X509Certificate2.CreateFromPem` reads) or
base64 PKCS#8 DER and must match its certificate, the onion key is `ED25519-V3:` and base64 of 64 bytes. A malformed value
keeps the node locked and is answered with a reason that names the field, never its value. The accepted values become
`Domain/Node/Provisioning/ProvisionedSecrets`, one DI singleton (`AddProvisionedSecrets` in `ConfigureNltgServices`)
that `LndGrpcHost`/`LndRootKeyStore`, `TorOnionService` and the Cashu/LN backend options (`UseProvisionedTls`, a method
the configuration binder never binds) read. A secret not delivered keeps today's file behavior, so a standard node
upgrades in place.

`returnLndGrpcMacaroons: true` (needs `lndGrpcMacaroonRootKey`) bakes the default `admin`, `readonly` and `invoice`
macaroons in memory and returns them in the answer: the provisioner already holds the root key, so nothing new leaves
the node, and the node writes none. The client writes them with `--macaroon-dir`.

`Node:Startup:RequireProvisionedSecrets=true` (locked starts only; default false) forbids writing any secret the node
would generate on its first start. The unlock is refused (node still locked, nothing written) while an enabled consumer
has neither a delivered value nor an existing file: `LndGrpc:Enabled` without `lndGrpcTls` and `tls.cert`/`tls.key`, or
without `lndGrpcMacaroonRootKey` and `macaroons.key` (unless `AllowNoMacaroons`), and an onion service
(`Node:Tor` `IsOnionServiceEnabled`) without `torOnionServiceKey` and its key file. Should a consumer still reach
generation, it refuses instead: `LndTlsFiles`/`LndMacaroonFiles` throw at the gRPC host's start, missing default
macaroons of a file root key are not baked, new root key ids are refused, and the Tor onion service is not started. Files
that exist are still used.

With every secret delivered the configuration directory needs only `appsettings.json` (`ProvisionedLockedStartTests`:
LND gRPC, Cashu processor, LN backend and Tor onion service run and the directory still holds that one file; the
database, data rather than a secret, lives where the delivered connection string points). A running node still writes
its non-secret state there: the public `nltg.key-index`, the per-process IPC cookie (SR-15), logs, the fee cache and,
with backups on, `channel.backup` (encrypted to the node key).

## Client

The client (`NLightning.Client`, `nltg` in its usage text):

```bash
NLightning.Client --network regtest unlock --status
NLightning.Client --network regtest unlock --key-file node.key.json --password-file pw [--secrets-file secrets.json] [--macaroon-dir <dir>] [--socket <path>]
NLightning.Client --network regtest unlock --key-file node.key.json --password-stdin < pw
# Stdin provisioner: the request frame goes to the daemon's stdin
NLightning.Client unlock --key-file node.key.json --password-file pw --frame | NLightning.Daemon --network regtest --locked
```

`unlock` does not use the IPC pipe. It prints the state, network and node id; exit 0 when the node is (now) unlocked or
answered a status, 1 otherwise. The secrets file is the `secrets` JSON object above. `--macaroon-dir <dir>` (with a
secrets file holding `lndGrpcMacaroonRootKey`, not with `--frame`) asks for the default macaroons and writes
`admin.macaroon`, `readonly.macaroon` and `invoice.macaroon` there, owner-only and never over an existing file.

## Tests

`test/NLightning.Daemon.Tests/Provisioning/` (`LockedStartupTests`: fresh database enrolls, v2 and v3 key files unlock
to their own node ids, wrong password and a database or key index of another key refused with every file unchanged,
channel key index persisted and restored, secrets applied in memory, no secret in any file or log line;
`KeyProvisioningSocketTests`: the client over the socket, socket and directory modes, malformed frames, stale and live
sockets, the stdin endpoint, `--macaroon-dir`; `LockedStartOptionsTests`; `ProvisionedSecretsTests`: frame versions,
every secret read and refused by name, `RequireProvisionedSecrets`, macaroons returned; `ProvisionedLockedStartTests`:
the end-to-end start with every secret and only `appsettings.json` on disk), the consumers' own tests
(`LndGrpc.Tests/LndGrpcHostTests.Provisioned`, `Infrastructure.Tests/Transport/Tor/TorOnionServiceTests`,
`Daemon.Tests/Cashu/CashuPaymentProcessorHostTests`, `LnBackend.Tests/LnBackendOptionsTests`) and
`test/NLightning.Infrastructure.Bitcoin.Tests/Managers/SecureKeyManagerKeyFileContentTests`.