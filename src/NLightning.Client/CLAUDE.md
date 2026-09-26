# NLightning.Client (CLI for the daemon)

## Purpose
A console exe that sends **one IPC request per call** to a running `NLightning.Daemon` and prints the result. The help text calls it `nltg`, but no AssemblyName is set, so the binary is `NLightning.Client`. It has no Lightning protocol logic of its own. All work is done by the daemon.

## Layout
- `Program.cs`: top-level statements. Sets `MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options` (this must happen first), wires Ctrl+C and calls `ClientApp.RunAsync`.
- `ClientApp.cs`: checks `--help` first, validates the command and its argument count (`ValidateArguments`, exit code 2 on usage errors), resolves pipe and cookie paths, then dispatches on a `switch (cmd)` that accepts aliases (`info|node-info`, `connect|connect-peer`, `listpeers|list-peers`, `listchannels|list-channels [peer_id]`, `getaddress|get-address`, `walletbalance|wallet-balance`, `openchannel|open-channel`, `createinvoice|create-invoice|addinvoice <msat|any> [description] [expiry_seconds]`, `payinvoice|pay-invoice|pay <bolt11> [msat] [timeout_seconds]` (exit code 1 when the payment failed), `listinvoices|list-invoices [count] [skip]`, `listpayments|list-payments [count] [skip]`, `forceclosechannel|force-close-channel <channel_id>` and `pendingsweeps|pending-sweeps [channel_id] [all]` (BOLT 5, ClientCommand 14/15; `all` includes the closed channels; `ParsePendingSweepsOptions`)). Invoice/payment amounts are in **msat** (`any` = no amount; `0` is a usage error); `openchannel` stays in sat. Number arguments are checked in `ValidateArguments` (usage error, exit 2) against the daemon's limits, so the user never gets an IPC error for them: list `count` 1-`MaxListCount` (1000, the daemon's `ClientRequestGuards.MaxPageSize`), payinvoice timeout 1-`MaxPayTimeoutSeconds` (300, `PayInvoiceClientHandler.MaxTimeoutSeconds`); keep them in sync. The default command is `node-info`.
- `Ipc/NamedPipeIpcClient.cs`: one typed method per `ClientCommand` (note `GetWalletBalance` lacks the `Async` suffix). The older ones are copies of the same template; `CreateInvoiceAsync`, `PayInvoiceAsync`, `ListInvoicesAsync` and `ListPaymentsAsync` use the private generic `SendRequestAsync<TRequest, TResponse>`, which new methods should use too. `SendAsync` opens a new `NamedPipeClientStream` for every call (2s connect timeout), writes a 4-byte `BitConverter` length prefix and a MessagePack `IpcEnvelope`, then reads a response frame (max 10,000,000 bytes). If the response is an `IpcEnvelopeKind.Error` envelope, it throws `InvalidOperationException("IPC error {code}: {msg}")`.
- `Handlers/OpenChannelMessageHandler.cs`: sends `OpenChannel`, then long-polls `OpenChannelSubscription` until the state is `ReadyForUs`/`ReadyForThem` or Ctrl+C.
- `Printers/`: `IPrinter<T>` plus one `<Name>Printer` per top-level `*IpcResponse` (nested `PeerInfoIpcResponse` is printed inside `ListPeersPrinter`). There is no JSON output mode. `ListChannelsPrinter` and the invoice/payment printers take an optional `TextWriter` (default `Console.Out`) and write culture-invariant text (layout shared in `PaymentsPrintFormat`, times as `yyyy-MM-dd HH:mm:ss'Z'` UTC); `test/NLightning.Daemon.Tests/Client/PrinterSnapshotTests.cs` pins their output, so update a snapshot only on purpose.
- `Utils/ClientUtils.cs`: `ShowUsage()`.

## Dependency rules
- Allowed references: `NLightning.Domain`, `NLightning.Transport.Ipc` (wire DTOs and formatters), `NLightning.Daemon.Contracts` (CLI parsing in `Helpers/CommandLineHelper.cs`, and `NodeUtils`/`NodeConstants` for `nltg.ipc`/`nltg.cookie`).
- Must NOT reference: Application, Infrastructure.*, Daemon, Persistence, Repositories, Serialization, or NBitcoin. The client is a thin shell over the IPC contract.
- `NLightning.Daemon` references this project, so never add a Client -> Daemon reference (that would be a cycle).

## Adding a new CLI command (the common task)
1. Add a `ClientCommand` value in `src/NLightning.Domain/Client/Enums/ClientCommand.cs`. Append only; values are on the wire.
2. Add `[MessagePackObject]` `<Name>IpcRequest` and `<Name>IpcResponse` in `src/NLightning.Transport.Ipc/Requests|Responses/` using `[Key(n)]`. Append keys; never reorder or reuse them.
3. If a new Domain value object crosses the wire, add a formatter in `src/NLightning.Transport.Ipc/MessagePack/Formatters/` and register it in `NLightningFormatterResolver`. If a DTO has a nullable struct property, also register a `T?` formatter (as done for `PeerAddressInfo?` and `CompactPubKey?`).
4. On the daemon side, add an `IIpcCommandHandler` in `src/NLightning.Daemon/Ipc/Handlers/` and register it with `AddSingleton<IIpcCommandHandler, X>()` in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`.
5. Here, add a `<Name>Async` method to `NamedPipeIpcClient`, following the existing template (Version=1, new CorrelationId, `GetAuthTokenAsync`, `Kind = IpcEnvelopeKind.Request`; some existing methods write the equivalent `Kind = 0`).
6. Add `Printers/<Name>Printer.cs : IPrinter<<Name>IpcResponse>`.
7. Add the `case` aliases in `ClientApp.RunAsync` and `ClientApp.ValidateArguments` (argument count), then update `ClientUtils.ShowUsage()`.

## Conventions
- File-scoped namespace (`NLightning.Client.<Folder>`). Preferred style: relative `using Domain.X;` / `using Transport.Ipc.X;` directives after the namespace line, System/third-party usings above it (some files, e.g. `Printers/NodeInfoPrinter.cs`, `Printers/WalletBalancePrinter.cs`, still use fully qualified `using NLightning.X;` above it).
- Root `.editorconfig`: naming rules are errors (`_camelCase` private fields, `s_camelCase` private statics), unused usings (IDE0005) is an error, `var` preference is a warning. CI (`.github/workflows/pr.yml`, `dotnet.yml`) runs `dotnet format --verify-no-changes`.
- Pass the `CancellationToken` through every call. Ctrl+C cancels `cts` in `Program.cs`.

## Build / run / test
- Build: `dotnet build src/NLightning.Client -p:MSBuildWarningsAsMessages=MSB4121`
- Run: `dotnet run --project src/NLightning.Client -- --network regtest listpeers`. This needs a running daemon and `~/.nltg/<network>/nltg.cookie`.
- Format: `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` (repo root).
- Tests: there is no Client test project. Client tests live in `test/NLightning.Daemon.Tests/Client/` (internals are visible via `AssemblyInfo.cs`). `dotnet test` discovers them; `dotnet run --project test/NLightning.Daemon.Tests -- -namespace NLightning.Daemon.Tests.Client` also works.

## Gotchas
- `CommandLineHelper` (in Daemon.Contracts) accepts `--network x`, `--network=x`, `-n x`, `--cookie x`, `--cookie=x` and `-c x`; only the separate-value forms consume the next argument. These options are skipped anywhere, including after the command, so they are never command arguments. Cookie dir precedence: `--cookie` arg, `--network` arg, `NLTG_COOKIE`, `NLTG_NETWORK`, then `~/.nltg/mainnet`.
- Frames are MessagePack with LZ4BlockArray compression (`Hash`/`TxId` are standard `bin 8` values).
- The protocol has no server push. Progress is reported by the client repeating the request (a long-poll).

## Onion routing (BOLT 4) hooks
This project has no onion or payment code. When BOLT 4 lands, expose it here with the 7-step recipe: e.g. `ClientCommand.PayInvoice`/`SendPayment`/`DecodeInvoice` (decode via `src/NLightning.Bolt11`, but on the daemon side, since Bolt11 depends on Infrastructure) and a `PaymentStatus` long-poll that follows the `OpenChannelMessageHandler` pattern. Map BOLT 4 failure codes (PERM/NODE/UPDATE/BADONION flags) to a structured error DTO or to `src/NLightning.Domain/Client/Constants/ErrorCodes.cs` rather than a plain string.
