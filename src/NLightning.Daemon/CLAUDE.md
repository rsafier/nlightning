# NLightning.Daemon (NLTG node host)

The executable Lightning node, and the DI **composition root** for the whole stack. `Program.cs` uses top-level statements and does the following in order: reads config, handles `--stop`, `--status` and `--help`, unlocks or creates the key (`SecureKeyManager`), optionally daemonizes, builds the Generic Host, applies optional EF migrations, then runs `NltgDaemonService`. That service starts FeeService, PeerManager (BOLT 8 TCP), BlockchainMonitor and the named-pipe IPC server. The CLI (`src/NLightning.Client`) talks to it over IPC. There is no protocol logic in this project; that lives in Application and Infrastructure.*.

## Layout
- `Program.cs`: entrypoint. Sets `MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options` (line ~132) before building the host.
- `Extensions/NodeServiceExtensions.cs`: `ConfigureNltgServices(secureKeyManager, configPath)`. This is the single DI root. It also calls `AddApplicationServices`, `AddBitcoinInfrastructure`, `AddInfrastructureServices`, `AddPersistenceInfrastructureServices`, `AddRepositoriesInfrastructureServices` and `AddSerializationInfrastructureServices`.
- `Extensions/NodeConfigurationExtensions.cs`: config path and network resolution, the default `appsettings.json` template (`CreateDefaultConfigJson`), and Serilog.
- `Extensions/DatabaseExtensions.cs`: migrates only when `Database:RunMigrations=true`.
- `Services/NltgDaemonService.cs`: the only hosted service. Start order is fee, peers, chain monitor, IPC. Stop runs in parallel.
- `Services/Ipc/`: `NamedPipeIpcService`, `LengthPrefixedIpcFraming` (IpcFraming.cs), `IpcRequestRouter` (IpcRouting.cs), `CookieFileAuthenticator`, `Factories/IpcErrorFactory`.
- `Ipc/Interfaces/` (internal): `IIpcCommandHandler`, `IIpcFraming`, `IIpcRequestRouter`, `IIpcAuthenticator`.
- `Ipc/Handlers/`: one singleton `IIpcCommandHandler` per `ClientCommand` (NodeInfo, ConnectPeer, ListPeers, GetAddress, WalletBalance, OpenChannel, OpenChannelSubscription).
- `Handlers/`: scoped `IClientCommandHandler<TReq,TResp>` classes for multi-step flows (`OpenChannelClientHandler`, `OpenChannelClientSubscriptionHandler`).
- `Interfaces/`: `IClientCommandHandler<TRequest,TResponse>` and `INodeInfoQueryService` (Daemon-local, not Domain).
- `Services/NodeInfoQueryService.cs`, `Utilities/DaemonUtils.cs` (arg normalization, re-exec daemonization, PID, stop), `Utilities/PasswordUtils.cs`, `Helpers/ClassNameEnricher.cs`.
- Dead or unwired code: `Services/PluginLoaderService.cs` (never registered, and nothing implements `IDaemonContext`) plus its `Models/PluginEntry.cs`, `Helpers/AesGcmHelper.cs` (no callers), `Models/FeeRateCacheData.cs` (only referenced from commented-out code in `Infrastructure.Bitcoin/Services/FeeService.cs` and from a Daemon test).

## Adding an IPC command
1. Add an enum value in `src/NLightning.Domain/Client/Enums/ClientCommand.cs`. Append only: the values go over the wire.
2. Add `[MessagePackObject]` `XIpcRequest`/`XIpcResponse` in `src/NLightning.Transport.Ipc/Requests|Responses`. Use append-only `[Key(n)]`. If a new Domain value object crosses the wire, add a formatter in `MessagePack/Formatters` and register it in `NLightningFormatterResolver`.
3. Add `internal sealed XIpcHandler : IIpcCommandHandler` in `Ipc/Handlers/`, using `NodeInfoIpcHandler.cs` as the template. Deserialize `envelope.Payload`. Return an envelope that copies Version, Command and CorrelationId with `Kind = IpcEnvelopeKind.Response`. On failure, return `IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.X, msg)` with a code from `Domain/Client/Constants/ErrorCodes.cs`. Use `ce.ErrorCode` for a `ClientException`, not `ce.Message`. Resolve client handlers by their `IClientCommandHandler<TReq, TResp>` interface, never by casting to the concrete type.
4. Register it with `services.AddSingleton<IIpcCommandHandler, XIpcHandler>()` in `NodeServiceExtensions`. The router does `ToDictionary(h => h.Command)`, so a duplicate command throws at resolve time.
5. Long-running or awaited flows go in a scoped `IClientCommandHandler` in `Handlers/`, registered with `AddScoped`. The IPC handler calls `CreateScope()` and resolves it per request. Follow the `TaskCompletionSource(RunContinuationsAsynchronously)` + event subscribe/`finally` unsubscribe pattern used in `OpenChannelClientHandler`.
6. On the client side, add a method to `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`, a printer, a case in `Program.cs` and help text in `ClientUtils.ShowUsage`.

## Conventions
- File-scoped namespace first. `NLightning.*` usings go after it and are written relative (`using Domain.Client.Enums;`). System, Microsoft and third-party usings go above it. (`Program.cs` has no namespace, so it uses fully qualified `using NLightning.*;` at the top.)
- IPC handlers are singletons, so resolve anything scoped (`IUnitOfWork`, `IBitcoinWalletService`, client handlers) through `CreateScope()` and never inject it directly.
- Options use `AddOptions<T>().BindConfiguration(section).ValidateOnStart()` for `Node` (`NodeOptions`, with a `PostConfigure` for `ListenAddresses`/`Network`/chain hash), `Bitcoin` (`BitcoinOptions`) and `FeeEstimation` (`FeeEstimationOptions`). `Database` is not an options class: `AddPersistenceInfrastructureServices(configuration)` reads it raw, and `DatabaseExtensions` reads `Database:RunMigrations`. `Serilog` is read via `ReadFrom.Configuration` in `NodeConfigurationExtensions.ConfigureNltg`. Env vars use the prefix `NLTG_` with `__` for nesting.
- Prefer registering new services in their own layer's `DependencyInjection.cs`. `NodeServiceExtensions` must own anything needing `SecureKeyManager` or `configPath` (e.g. `LocalLightningSigner`, `NamedPipeIpcService`, `CookieFileAuthenticator`). It also currently registers some layer-owned services that need no such input (`ChannelFactory`, `ChannelOpenValidator`, `CommitmentTransactionModelFactory`, `FundingTransactionModelFactory`, the `FeeService` HttpClient); do not treat those as a pattern to copy.

## Dependency rules
- This project is the outermost layer and may reference every other project (its csproj even references `NLightning.Client`). **Nothing** in `src/` may reference NLightning.Daemon; only `test/NLightning.Daemon.Tests` and `test/NLightning.Integration.Tests` do.
- Keep protocol, BOLT and crypto logic out of here. It belongs in Domain, Application or Infrastructure.*. Handlers here should only orchestrate.
- `NLightning.Daemon.Contracts` and `NLightning.Daemon.Plugins` target **net9.0** and must stay consumable from net9.0.

## Tests
- `test/NLightning.Daemon.Tests` (xUnit v3 + Moq, `Given_X_When_Y_Then_Z`). `dotnet test` runs them (CI included, since NL-167 added `xunit.runner.visualstudio`). The xunit v3 runner also works:
  - `dotnet run --project test/NLightning.Daemon.Tests` (all tests)
  - `dotnet run --project test/NLightning.Daemon.Tests -- -method '*FeeService*'`, or `-class <FQN>`
- InternalsVisibleTo (`AssemblyInfo.cs`) lists `NLightning.Daemon.Tests` (plus the stale `NLightning.Bolts.Tests` and `NLightning.Integration.Tests`). Moq cannot proxy `ILogger<InternalType>` (strong-named Logging.Abstractions), so use `NullLogger<T>.Instance` for internal handlers.
- Docker end-to-end tests (`test/NLightning.Integration.Tests/Docker/{AbcNetworkTests,ChannelOpeningFlowTests}.cs`) rebuild the DI graph **by hand** instead of calling `ConfigureNltgServices`. Mirror any new registration there. CI excludes them with `--filter 'FullyQualifiedName!~Docker'`.
- Build: `dotnet build NLightning.sln -p:MSBuildWarningsAsMessages=MSB4121`. Format gate: `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`.
- Run the node: `dotnet run --project src/NLightning.Daemon -- --network regtest`. The first run writes `~/.nltg/regtest/appsettings.json`; set the Bitcoin RPC/ZMQ settings and `Database:RunMigrations=true` there.

## Gotchas
- Always pass args through `DaemonUtils.NormalizeArgs` before `AddCommandLine` (done in `ReadInitialConfiguration` and for `Host.CreateDefaultBuilder`): it maps `-n`/`-c` to `--network`/`--config`, turns bare flags (`--daemon`, `--stop`, ... or any `--x` that is last or followed by `--y`/`-n`/`-c`/`-h`/`-?`) into `--x=true` so they don't swallow the next argument, keeps `--daemon true|false` as a pair, and drops the password options. `DaemonUtils.GetDaemonArgument` reads `--daemon` from the normalized form.
- With `--config`, the network comes from `Node:Network` in the file. Without it, the dir is `~/.nltg/<network>` (default `mainnet`), the template is written with that network, and `Node:Network` is set to it in memory when the file omits it. A file whose `Node:Network` differs from the dir (older templates wrote `regtest` into `~/.nltg/mainnet`) fails startup with an `InvalidOperationException` instead of being silently overridden.
- Creating a new key requires reachable bitcoind RPC (for birth height). The key password comes from `PasswordUtils.ResolvePassword`: `--password-file`, `--password-stdin`, `--password` (warns: visible in the process list), then `NLTG_PASSWORD`, else an interactive prompt. `Program.cs` clears `NLTG_PASSWORD` from its own environment after reading it. `ReadInitialConfiguration` uses `NltgEnvironmentVariablesSource`, which drops `NLTG_PASSWORD`, so the password never reaches `IConfiguration`.
- On Unix the IPC pipe is a Unix socket at `{configPath}/nltg.ipc` (`NodeConstants.NamedPipeFile`). Each connection carries one request and one response, with a native-endian 4-byte length prefix and a 10MB cap. `NamedPipeIpcService` writes a new random cookie to `{configPath}/nltg.cookie` (mode 0600 on Unix) on every start and deletes it on stop.
- Daemonizing re-executes the program with `--daemon-child` (`DaemonUtils.BuildDaemonChildArgs` drops `--daemon` and the password options): on Unix through `/bin/sh -c UnixDaemonLauncherScript` (`nohup ... &`, stdio to /dev/null, cwd kept), on Windows with `Process.Start`. The parent hands the password to the child in `NLTG_PASSWORD` on the child's environment only, and writes the PID file. There is no `fork()`.

## Onion routing (BOLT 4) hooks
There is no onion code here yet. When it lands:
- Register sphinx, forwarding and payment services in their layer's DI. Wire them here only if they need the node private key (prefer `ISecureKeyManager.ComputeNodeSharedSecret` over `GetNodeKeyPair()` for ECDH), following the `LocalLightningSigner` pattern, and mirror the wiring in the Docker test DI.
- Start a forwarding or interceptor loop from `NltgDaemonService.ExecuteAsync` and stop it in `StopAsync`, or register it as a separate `AddHostedService`.
- Add a payment IPC surface (`SendPayment`/`PayInvoice`/`DecodeInvoice` using `NLightning.Bolt11`, which the Daemon does not reference yet). Report payment progress with the existing long-poll subscription pattern, because the IPC transport has no server push. Map BOLT 4 failure codes to new `ErrorCodes`.
- Add forwarding policy (CLTV delta, fee base/ppm) to `NodeOptions` (`Node` section) and to `CreateDefaultConfigJson`.
