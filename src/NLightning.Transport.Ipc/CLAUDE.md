# NLightning.Transport.Ipc

## Purpose
This is the wire contract for local IPC between the CLI (`src/NLightning.Client`) and the daemon (`src/NLightning.Daemon`). It contains the envelope, the per-command request/response DTOs, and MessagePack formatters for Domain value objects. It holds no transport code: the pipe server lives in `src/NLightning.Daemon/Services/Ipc/` and the client in `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`. Every type here is serialized, so a change to one is a change to the wire format.

## Layout
- `IpcEnvelope.cs`: keys 0 Version, 1 Command (`Domain.Client.Enums.ClientCommand`), 2 CorrelationId, 3 AuthToken (cookie), 4 Payload (`byte[]`, the inner DTO serialized separately), 5 Kind.
- `IpcEnvelopeKind.cs` (Request=0, Response=1, Error=2) and `IpcError.cs` (Code, Message; the codes are in `src/NLightning.Domain/Client/Constants/ErrorCodes.cs`, except `unknown_command`, which `src/NLightning.Daemon/Services/Ipc/IpcRouting.cs` hard-codes).
- `Requests/*IpcRequest.cs` and `Responses/*IpcResponse.cs`: one pair per `ClientCommand`. Some have `ToClientRequest()` / `static FromClientResponse()` mappers to `Domain.Client.Requests|Responses`.
- `MessagePack/NLightningMessagePackOptions.cs`: Standard options + `NLightningFormatterResolver` + **Lz4BlockArray** compression.
- `MessagePack/NLightningFormatterResolver.cs`: a `Type -> formatter` dictionary that falls back to `StandardResolver`.
- `MessagePack/Formatters/`: Hash, TxId, ChannelId, CompactPubKey(+Nullable), PeerAddressInfo(+Nullable), FeatureSet, LightningMoney, BitcoinNetwork, SignedTransaction.

## Adding an IPC command (the common task)
1. Add a value to `src/NLightning.Domain/Client/Enums/ClientCommand.cs`. Append it at the end and never renumber, because the value goes on the wire.
2. Add `Requests/<Name>IpcRequest.cs`: a `[MessagePackObject]` sealed class with `[Key(n)]` members, plus `ToClientRequest()` if a Domain request exists.
3. Add `Responses/<Name>IpcResponse.cs` with the same attributes, plus `FromClientResponse()` if needed.
4. If a new Domain value object crosses the wire, add `MessagePack/Formatters/<T>Formatter.cs` and register it in the `NLightningFormatterResolver` ctor. For structs used as nullable, also register `typeof(T?)` with a `*NullableFormatter`, following the CompactPubKey pattern.
5. Daemon side: add `src/NLightning.Daemon/Ipc/Handlers/<Name>IpcHandler.cs : IIpcCommandHandler`. Copy Version, Command and CorrelationId into the response and set Kind=Response. Report errors through `Services/Ipc/Factories/IpcErrorFactory.CreateErrorEnvelope`. Register it with `AddSingleton<IIpcCommandHandler, ...>` in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`. A duplicate `Command` crashes the router (`ToDictionary` in `src/NLightning.Daemon/Services/Ipc/IpcRouting.cs`).
6. Client side: add a method to `NamedPipeIpcClient.cs`, a printer in `Client/Printers/`, a case in `Client/ClientApp.cs`, and update `Client/Utils/ClientUtils.ShowUsage`.

## Conventions
- File-scoped namespace `NLightning.Transport.Ipc[.Requests|.Responses|.MessagePack[.Formatters]]`. Put `using MessagePack;` above the namespace and relative `using Domain.X;` directives after it.
- The layout is integer-keyed arrays. Only append new keys; never reuse or reorder them. `OpenChannelIpcRequest` skips Key 1 on purpose, so keep it that way.
- Name DTOs `<Name>IpcRequest` / `<Name>IpcResponse`. Handlers are singletons, so they must resolve scoped services through `CreateScope()`.

## Dependency rules
- Allowed: `NLightning.Domain` and the MessagePack package (3.1.4). The csproj also references `NLightning.Daemon.Contracts` (net9.0), but no source file uses it.
- Must NOT reference Application, Infrastructure.*, Daemon, Client, NBitcoin or EF. This project is consumed by both Client and Daemon, so a reference to either one creates a cycle.

## Tests
Formatter round-trip tests live in `test/NLightning.Daemon.Tests/Ipc/Formatters/`. Run them with `dotnet test test/NLightning.Daemon.Tests` or `dotnet run --project test/NLightning.Daemon.Tests -- -class <FQN>`. Write round-trip tests with `MessagePackSerializer.Serialize/Deserialize(x, NLightningMessagePackOptions.Options)`.
- Build: `dotnet build src/NLightning.Transport.Ipc -p:MSBuildWarningsAsMessages=MSB4121`
- Format gate (CI): `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"`

## Gotchas
- Both ends must set `MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options` before any serialization. See `src/NLightning.Client/Program.cs` and `src/NLightning.Daemon/Program.cs`. Otherwise LZ4 and resolver mismatches occur.
- `HashFormatter` and `TxIdFormatter` write `bin 8` of exactly 32 bytes (`c4 20 ...`) and `nil` for a default value; reading rejects any other length. This replaced a raw 32-byte encoding (no header), so a client and daemon from before and after that change cannot talk to each other. `SignedTransactionFormatter` writes `[TxId, RawTxBytes]` (signatures are dropped) or `nil`.
- Framing is a 4-byte native-endian length prefix with a 10 MB cap, and each connection carries exactly one request and one response, with no push. For progress updates, use a long-poll "subscription" command, as `OpenChannelSubscription` does. The framing is implemented twice (`src/NLightning.Daemon/Services/Ipc/IpcFraming.cs` and `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`), so change both together.

## Onion-routing (BOLT 4) hooks
There is no onion code here. The following is guidance, not existing code: when payments land, add commands such as `SendPayment`, `PayInvoice`, `DecodeInvoice` and `PaymentStatus` (a long-poll subscription) by following the recipe above. Reuse the existing formatters: `Hash` for payment_hash/preimage, `LightningMoney` for amounts, `CompactPubKey` for node ids. Add a formatter for `Domain/Channels/ValueObjects/ShortChannelId.cs` if hops are exposed (none exists yet). For BOLT 4 onion failures, return a structured failure DTO (failure_code with its PERM/NODE/UPDATE bits, erring node, channel) rather than a bare `IpcError` string, and add the matching codes to `Domain/Client/Constants/ErrorCodes.cs`.
