# NLightning.Testing.Lnd

The in-tree LND gRPC client of the test harness. It replaces the `lnunit.lnd` NuGet package (LNUnit.LND, nbd-wtf/LNUnit, MIT), which can no longer be published. It is a thin layer over gRPC, with no Docker or Kubernetes code and no product references:

- **Generated clients.** The C# clients are generated with Grpc.Tools (client stubs only) from LND's own `.proto` files. The files are committed under `Protos/` and pinned to one LND tag.
- **Connection layer.** `LndNodeConnection`, `LndSettings` and `LndNodePool` are ported from LNUnit.LND. Each file names its LNUnit source and lists what changed.

## Licenses

Both third-party parts are MIT, and their notices ship with them:

| Part | Copyright | Notice |
|---|---|---|
| `Protos/**.proto` and the client code generated from them | Lightning Labs and The Lightning Network Developers (lightningnetwork/lnd) | `Protos/LICENSE-LND.txt`, LND's root `LICENSE` at the manifest's commit (the protos have no header of their own) |
| The connection layer (`Lnd*.cs`), ported from LNUnit.LND | nbd (nbd-wtf/LNUnit) | `LICENSE-LNUnit.txt` |

Tests are in `test/NLightning.Testing.Lnd.Tests`.

## Layout

| File | What it is |
|---|---|
| `Protos/**.proto`, `Protos/manifest.txt`, `Protos/LICENSE-LND.txt` | LND's protos at the tag in the manifest, and LND's MIT notice. The manifest also records the tag's commit, two sha256 per proto (the upstream file and the committed one) and the notice's sha256. |
| `LndSettings` | The endpoint, `tls.cert` (PEM or DER), the macaroon (optional), a custom certificate check, and the message size limits (128 MB by default, as in LNUnit). |
| `LndGrpcChannelFactory` | Opens the `GrpcChannel`: `SocketsHttpHandler` with HTTP/2 keep-alive, TLS with the certificate check, and the macaroon call credentials. |
| `LndCertificatePinning` | Accepts exactly the pinned certificate, whatever its chain or host name errors, or delegates to `LndSettings.ServerCertificateValidation`. |
| `LndMacaroonCredentials` | Adds the `macaroon` metadata header, in lower-case hex, to every call. |
| `LndNodeConnection` | One channel plus a typed client per LND service, and the node's identity from `GetInfo`. |
| `LndNodePool`, `LndNodePoolConfig` | A set of connections with a readiness loop. Also LNUnit's 50/50 rebalance between pool members. |
| `LndExtensions` | LNUnit's small helpers: `GetClient`, `PackUnsignedToInt64`/`UnpackSignedToUInt64`, `ToLightningNode(s)`. |

## Regenerating for another LND tag

```bash
scripts/lnd-protos/update.sh v0.21.4-beta     # the default tag
dotnet build test/NLightning.Testing.Lnd.Tests -c Release -p:NltgTargetNet11=false
dotnet test test/NLightning.Testing.Lnd.Tests -c Release --no-build -f net10.0
```

The script does the following:

1. It resolves the tag to the commit it points to, and downloads every service proto under `lnrpc/` at that commit (never by the tag name, so the recorded commit is what was downloaded) into a staging folder. That is LNUnit.LND's LND set plus `chainrpc/chainkit.proto`. It skips `lnclipb/lncli.proto`, which holds lncli's output types and no service.
2. It inserts one line after each file's `package` line and changes no other byte: `option csharp_namespace = "NLightning.Testing.Lnd.<Package>";`.
3. It checks that every import resolves within the set.
4. It downloads LND's root `LICENSE` at the same commit as `Protos/LICENSE-LND.txt`.
5. It replaces `Protos/` and writes the manifest.

It fails if a file already sets `csharp_namespace`, has more or fewer than one `package` line, or imports a file outside the set.

After a tag change, these tests show what moved:

- **`ProtoManifestTests`** checks each committed file against its manifest hash. It also removes the namespace line from each file and checks the result against the upstream hash, and checks that LND's notice is there.

Loop's protos (LNUnit's `LoopConnection`) are not fetched. They come from another repository, and nothing here uses Loop.

## Namespace choice

The generated types live in `NLightning.Testing.Lnd.Lnrpc`, `.Routerrpc`, `.Walletrpc`, `.Invoicesrpc`, `.Signrpc`, `.Chainrpc`, `.Peersrpc`, `.Devrpc`, `.Verrpc`, `.Autopilotrpc`, `.Watchtowerrpc`, `.Wtclientrpc` and `.Neutrinorpc`:

- **Coexistence with lnunit.lnd.** lnunit.lnd uses the global `Lnrpc`, `Routerrpc` and so on, so this assembly can sit next to it in one test assembly with no `extern alias`. That still matters in `NLightning.Integration.Tests`, which gets lnunit.lnd transitively through `LNUnit` (the Docker LND backend's container builder, NL-819). The `LnUnitCoexistenceTests` that compiled against both left with the `LNUnit.LND` reference of `NLightning.Testing.Lnd.Tests` (NL-819).
- **Wire names are unchanged.** Only the C# namespace moves. The proto packages stay the same, so the services are still `lnrpc.Lightning`, `routerrpc.Router` and so on, and LND sees the same method paths.
- **Member names are unchanged.** Message, field and enum names are what protoc generates from LND's protos, as in lnunit.lnd. For example, `verrpc.Version.version` is `Version_`.

## Feeding it

```csharp
// Docker fixture: files copied out of the container.
var settings = LndSettings.FromFiles("172.18.0.3", 10009, tlsCertPath, adminMacaroonPath);

// Kubernetes harness: bytes read with exec.
var settings = LndSettings.FromBytes($"https://{podIp}:10009", tlsCertBytes, macaroonBytes);

// From lnunit.lnd settings, during the migration.
var settings = LndSettings.FromBase64(old.GrpcEndpoint!, old.TlsCertBase64!, old.MacaroonBase64);

using var lnd = await LndNodeConnection.ConnectAsync(settings, cancellationToken: ct);
```

The node's `tls.cert` is **pinned**, while LNUnit.LND accepted any certificate:

- **Re-read the certificate when LND writes a new one.** A restarted container keeps its certificate unless it was deleted, expired, or regenerated by `tlsautorefresh`.
- **Or set a custom check.** Set `ServerCertificateValidation` to decide yourself.

`GrpcEndpoint` takes `https://host:port` or `host:port` and is normalized to `https://host:port`. `http` is refused, because LND's gRPC always uses TLS.

A pool's readiness check defaults to `State.GetState` answering `SERVER_ACTIVE` within 2 s. Replace `LndNodePoolConfig.ReadinessCheck` to require more, such as `synced_to_chain`. Replace `ConnectionFactory` to build connections another way. To drive passes yourself, set `StartBackgroundUpdates = false` and call `UpdateReadyStatesAsync` or `WaitUntilAllReadyAsync`.

## Swap table for phase 3 (LNUnit.LND to this project)

**Done (test harness phase 3 lane B).** Every test in `NLightning.Integration.Tests` uses this client. `Fixtures/LightningRegtestNetworkFixture` keeps LNUnit's `LNUnitBuilder` private, as the container orchestrator only, and builds one `LndNodeConnection` per LND node with `LndSettings.FromBase64` from what the builder read out of each container (endpoint, `tls.cert`, `admin.macaroon`). Tests reach the nodes through `LndNodes`, `GetLndNode(alias)` and `RestartLndAsync(alias)`, and the miner through `Bitcoin` and `BitcoinZmqPorts`. The LNUnit package moved from NLightning.Tests.Utils to NLightning.Integration.Tests; lnunit.lnd still comes with it transitively until the cluster backend replaces the builder, so `LnUnitCoexistenceTests` stays.

### Namespaces

In a test file, swap the `using` directives:

| LNUnit.LND | Here |
|---|---|
| `using LNUnit.LND;` | `using NLightning.Testing.Lnd;` |
| `using Lnrpc;` | `using NLightning.Testing.Lnd.Lnrpc;` |
| `using Routerrpc;` | `using NLightning.Testing.Lnd.Routerrpc;` |
| `using Walletrpc;`, `Invoicesrpc`, `Signrpc`, `Chainrpc`, `Peersrpc`, `Devrpc`, `Verrpc`, … | `using NLightning.Testing.Lnd.<same>;` |

Keep them **above** the file-scoped namespace, with the full name, where the old `using Lnrpc;` lines were. A using above the namespace loses to the relative `NLightning.*` usings inside it, so names that exist in both, such as `AddressType` (Domain and lnrpc), `Invoice` (Bolt11 and lnrpc) or `OutPoint`/`Transaction` next to NBitcoin aliases, resolve as before. Relative `using Testing.Lnd.Lnrpc;` lines inside the namespace make those names ambiguous (`CS0104`).

Some code qualifies a type with the namespace, such as `Routerrpc.SendToRouteRequest`, `Lnrpc.Payment` or `Walletrpc.EstimateFeeRequest`. That happened in 11 Docker test files; lane B wrote the `Testing.Lnd.` prefix. Inside an `NLightning.*` namespace, such a name no longer resolves on its own. Either:

- write `Testing.Lnd.Routerrpc.SendToRouteRequest` (works at any time), or
- use project-wide aliases in the test csproj, such as `<Using Include="NLightning.Testing.Lnd.Routerrpc" Alias="LndRouterrpc"/>`, and write `LndRouterrpc.SendToRouteRequest`.

An alias with the old name (`Alias="Routerrpc"`, so the qualified code compiles unchanged) works only once neither LNUnit nor LNUnit.LND is referenced by the test assembly, directly or transitively (NLightning.Integration.Tests references LNUnit 3.0.4 for its container builder). While lnunit.lnd is there, its global `Routerrpc`/`Lnrpc`/... namespaces conflict with the alias at every use: `error CS0576: Namespace '<global namespace>' contains a definition conflicting with alias 'Routerrpc'`. So either drop the LNUnit package (the Docker fixture builder) in the same step as the swap, or use a non-clashing alias or the `Testing.Lnd.` prefix.

### Types and members

| LNUnit.LND | Here | Difference |
|---|---|---|
| `LNDNodeConnection` | `LndNodeConnection` | The constructor `(settings, ILogger?)` still makes a blocking `GetInfo`; prefer `ConnectAsync`. The client properties are get-only. `StartWithBase64` and `CreateGrpcConnection` are gone; use `LndGrpcChannelFactory.Create(settings)`. `Dispose` is idempotent. `KeysendPayment` takes an optional `CancellationToken`. |
| `.LightningClient`, `.RouterClient`, `.SignClient`, `.StateClient`, `.ChainNotifierClient`, `.DevClient`, `.InvoiceClient`, `.PeersClient`, `.WalletKitClient` | Same names | The types are the `NLightning.Testing.Lnd.*` ones. |
| (none) | `.WalletUnlockerClient`, `.ChainKitClient`, `.VersionerClient`, `.AutopilotClient`, `.WatchtowerClient`, `.WtClientClient`, `.NeutrinoKitClient`, `.Channel` | New. |
| `.LocalNodePubKey`, `.LocalNodePubKeyBytes`, `.LocalAlias`, `.ClearnetConnectString`, `.OnionConnectString`, `.Host`, `.Settings`, `.IsRpcReady`, `.IsServerReady`, `.GetStateSafe(seconds)`, `.KeysendPayment(...)`, `.Clone()`, `.Stop()` | Same names | `Settings` is a copy. `Host` is the normalized endpoint, without the trailing `/` LNUnit's builder put there. New: `RefreshNodeInfoAsync` and `GetStateSafeAsync(TimeSpan)`. |
| `LNDSettings { GrpcEndpoint, TlsCertBase64, MacaroonBase64 }` | `LndSettings`, same properties | Backed by `TlsCert`/`Macaroon` bytes. The certificate is pinned. The macaroon may be null. New: `FromFiles`, `FromBytes`, `FromBase64`, `ToGrpcEndpoint`, `ServerCertificateValidation`, `MaxReceiveMessageSize`, `MaxSendMessageSize`, `Validate`, `Clone`. |
| `LNDNodePool` | `LndNodePool` | Differences listed below. |
| `.ReadyNodes` (a public `List<>` field) | `.ReadyNodes` (`IReadOnlyList<>` snapshot) | LINQ, `.ToList()`, `.ToImmutableList()` and indexing work as before; `Add`/`Remove` on it do not. |
| `.TotalNodes`, `.AllReady`, `.SaveRebalanceAction`, `.AddNode(settings)`, `.RemoveNode(node)`, `.Dispose()` | Same names | `TotalNodes` follows `AddNode`/`RemoveNode`. |
| `.GetLNDNodeConnection()`, `.GetLNDNodeConnection(pubkey)` | `.GetLndNodeConnection()`, `.GetLndNodeConnection(pubkey)` | Throws `InvalidOperationException` with no ready node, as LNUnit's `First()` did. |
| `.RebalanceNodePool(delta)` | `.RebalanceNodePoolAsync(delta, ct)` | Counts only payments LND reports `Succeeded`. |
| `LNDNodePool.InvoicePayRebalance(...)` | `LndNodePool.InvoicePayRebalanceAsync(...)` | Returns the hash only for `Succeeded`. LNUnit returned the hash of any first update. |
| `LNDNodePool.GetInteralNodeEvenBalaceTasks(pool, delta)` | `LndNodePool.GetInternalNodeEvenBalanceTasksAsync(nodes, delta, ct)` and the pure `ComputeEvenBalanceTasks` | A channel without constraints no longer throws. |
| `LNDNodePool.BalanceTask`, `PoolRebalanceStats` | Same, nested in `LndNodePool` | |
| `LNDNodePoolConfig`, `LNDNodePoolConfigBuilder` (`AddNode`, `AddConnectionSettings`, `UpdateReadyStatesPeriod`) | `LndNodePoolConfig`, `LndNodePoolConfigBuilder`, same methods | `QuickStartupMode` is settable. New: `ReadinessCheck`, `ConnectionFactory`, `StartBackgroundUpdates`. |
| `LNDExtensions` | `LndExtensions` | The AES helpers are not ported. |

### LndNodePool differences

- **Constructors.** The only constructor is `(LndNodePoolConfig, ILogger<LndNodePool>?)`. The `IOptionsSnapshot`/`IServiceProvider` constructors and the obsolete `List<LNDSettings>` constructor are gone.
- **Readiness.** A node joins `ReadyNodes` only when its readiness check passes. LNUnit also added a node that was not `SERVER_ACTIVE`.
- **Ordering.** `ReadyNodes` keeps the order in which nodes joined the pool.
- **Thread safety.** The pool's state is thread-safe.

### Not ported

`LNDChannelAcceptor`, `LNDChannelEventsHandler`, `LNDChannelInterceptorHandler`, `LNDCustomMessageHandler`, `LNDGraphEventsHandler`, `LndHtlcMonitor`, `LNDRPCMiddlewareHandler`, `LNDSimpleHtlcInterceptorHandler`, `LNDStateMonitor` and `LoopConnection`. Nothing in the tests uses them. They are thin wrappers over streaming calls that the generated clients expose directly.

### LND 0.21 removed APIs

Compared with lnunit.lnd 3.0.4 (LND 0.20 protos), LND 0.21 removed these deprecated items, and none is used by the Docker tests:

- `Lightning.SendPayment`, `SendPaymentSync`, `SendToRoute` and `SendToRouteSync`, with `lnrpc.SendRequest`, `SendResponse` and `SendToRouteRequest`.
- `Router.SendPayment`, `SendToRoute` and `TrackPayment`, with `routerrpc.SendToRouteResponse`, `PaymentStatus` and `PaymentState`.
- The `outgoing_chan_id` field of `QueryRoutesRequest` and `routerrpc.SendPaymentRequest`; use `outgoing_chan_ids`. `BuildRouteRequest.OutgoingChanId` is unchanged.

`LnUnitCoexistenceTests` holds the exact list.

## Live check

`LiveLndTests` is `Explicit` and in category `Live`. It runs against a real LND named by three environment variables:

- `NLTG_LND_GRPC`
- `NLTG_LND_TLS_CERT` (a path)
- `NLTG_LND_MACAROON` (a path)

The test waits for readiness through the pool, then calls `GetInfo`, `GetState`, `GetVersion`, `NewAddress`, `WalletKit.EstimateFee`, `AddInvoice` and `Invoices.LookupInvoiceV2`. Run it with:

```bash
dotnet run --project test/NLightning.Testing.Lnd.Tests -f net10.0 -- -explicit only -trait Category=Live
```

It passed against `custom_lnd:0.21.4-beta` with a regtest bitcoind.

`Docker/LndGrpcLiveTests` is `Explicit` and in category `LndGrpc`. It needs no environment: it starts its own regtest network through the `docker` command line, with unique names and its own Docker network (`nltg-lndgrpc-<id>-bitcoind`, `-a`, `-b`, `-net`, all labelled `nltg-lndgrpc=<id>`), from the local images `polarlightning/bitcoind:29.0` and `custom_lnd:0.21.4-beta` (`--pull=never`). It reads `tls.cert` and `admin.macaroon` out of the containers and reaches gRPC through ports published on 127.0.0.1. Then it drives both nodes through this client:

1. `GetInfo` (version 0.21.4) and `NewAddress`, then funding by mining to that address.
2. `ConnectPeer`, `OpenChannelSync` and a wait until the channel is active.
3. `AddInvoice` on b, followed through `Invoices.SubscribeSingleInvoice`, and paid from a with `Router.SendPaymentV2`, streamed to `SUCCEEDED`.
4. `Invoices.LookupInvoiceV2`, the `ListChannels` balances and `WalletKit.ListUnspent`.
5. An `LndNodePool` round over both nodes: readiness, lookup by key, and `RebalanceNodePoolAsync`, which moves the channel to 50/50.

It removes only its own containers and network, also on failure. It sits in a `Docker` namespace, so CI's `!~Docker` filter leaves it out too. Run it alone, under the machine's Docker lock where there is one:

```bash
dotnet build test/NLightning.Testing.Lnd.Tests -c Release -p:NltgTargetNet11=false
dotnet run --project test/NLightning.Testing.Lnd.Tests -c Release -f net10.0 --no-build -- -explicit only -trait Category=LndGrpc
```
