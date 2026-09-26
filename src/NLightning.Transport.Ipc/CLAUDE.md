# NLightning.Transport.Ipc

## Purpose
This is the wire contract for local IPC between the CLI (`src/NLightning.Client`) and the daemon (`src/NLightning.Daemon`). It contains the envelope, the per-command request/response DTOs, and MessagePack formatters for Domain value objects. It holds no transport code: the pipe server lives in `src/NLightning.Daemon/Services/Ipc/` and the client in `src/NLightning.Client/Ipc/NamedPipeIpcClient.cs`. Every type here is serialized, so a change to one is a change to the wire format.

## Layout
- `IpcEnvelope.cs`: keys 0 Version, 1 Command (`Domain.Client.Enums.ClientCommand`), 2 CorrelationId, 3 AuthToken (cookie), 4 Payload (`byte[]`, the inner DTO serialized separately), 5 Kind.
- `IpcEnvelopeKind.cs` (Request=0, Response=1, Error=2) and `IpcError.cs` (Code, Message; the codes are in `src/NLightning.Domain/Client/Constants/ErrorCodes.cs`, except `unknown_command`, which `src/NLightning.Daemon/Services/Ipc/IpcRouting.cs` hard-codes).
- `Requests/*IpcRequest.cs` and `Responses/*IpcResponse.cs`: one pair per `ClientCommand`. Some have `ToClientRequest()` / `static FromClientResponse()` mappers to `Domain.Client.Requests|Responses`. `ListChannelsIpcResponse` carries nested `ChannelInfoIpcResponse` rows (ClientCommand 8; keys 16-18 `IsReestablished`, `FeeBaseMsat`, `FeePpm`). CloseChannel (ClientCommand 13): `CloseChannelIpcRequest` (ChannelId, FeeRatePerKw?, NoFeeRange, WaitSeconds?) and `CloseChannelIpcResponse` (ChannelId, State, ClosingTxId as display-order hex). ForceCloseChannel (14): `ForceCloseChannelIpcRequest` (ChannelId) and `ForceCloseChannelIpcResponse` (ChannelId, State, Status name, CommitmentTxId display hex). PendingSweeps (15): `PendingSweepsIpcRequest` (ChannelId?, IncludeClosed) and `PendingSweepsIpcResponse` with `PendingSweepChannelIpcInfo` (close kind, commitment txid, number, height) and `PendingSweepOutputIpcInfo` rows (outpoint, descriptor, state, amount, HTLC, resolving txid, heights); txids as display-order hex (`PendingSweepsIpcResponse.ToDisplay`). Invoices and payments (ClientCommand 9-12): `CreateInvoice`/`PayInvoice`/`ListInvoices`/`ListPayments` requests, and responses that nest `InvoiceInfoIpcResponse` (never the preimage) or `PaymentInfoIpcResponse` (preimage once succeeded, `FailureCode?` as its u16 so unnamed codes survive, failing hop index).
- `MessagePack/NLightningMessagePackOptions.cs`: Standard options + `NLightningFormatterResolver` + **Lz4BlockArray** compression.
- `MessagePack/NLightningFormatterResolver.cs`: a `Type -> formatter` dictionary that falls back to `StandardResolver`.
- `MessagePack/Formatters/`: Hash, TxId, ChannelId(+Nullable), CompactPubKey(+Nullable), PeerAddressInfo(+Nullable), FeatureSet, LightningMoney, BitcoinNetwork, SignedTransaction, and `SecretNullableFormatter` (`Secret?`, a preimage: `bin 8` of 32 bytes or `nil`; other lengths are rejected).

## Adding an IPC command (the common task)
1. Add a value to `src/NLightning.Domain/Client/Enums/ClientCommand.cs`. Append it at the end and never renumber, because the value goes on the wire.
2. Add `Requests/<Name>IpcRequest.cs`: a `[MessagePackObject]` sealed class with `[Key(n)]` members, plus `ToClientRequest()` if a Domain request exists.
3. Add `Responses/<Name>IpcResponse.cs` with the same attributes, plus `FromClientResponse()` if needed.
4. If a new Domain value object crosses the wire, add `MessagePack/Formatters/<T>Formatter.cs` and register it in the `NLightningFormatterResolver` ctor. For structs used as nullable, also register `typeof(T?)` with a `*NullableFormatter`, following the CompactPubKey pattern.
5. Daemon side: add `src/NLightning.Daemon/Ipc/Handlers/<Name>IpcHandler.cs : IIpcCommandHandler` (derive from `ClientCommandIpcHandler<...>` when a scoped client handler does the work). Copy Version, Command and CorrelationId into the response and set Kind=Response. Report errors through `Services/Ipc/Factories/IpcErrorFactory.CreateErrorEnvelope`. Register it with `AddSingleton<IIpcCommandHandler, ...>` in `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`. A duplicate `Command` crashes the router (`ToDictionary` in `src/NLightning.Daemon/Services/Ipc/IpcRouting.cs`).
6. Client side: add a method to `NamedPipeIpcClient.cs`, a printer in `Client/Printers/`, a case in `Client/ClientApp.cs`, and update `Client/Utils/ClientUtils.ShowUsage`.

## Conventions
- File-scoped namespace `NLightning.Transport.Ipc[.Requests|.Responses|.MessagePack[.Formatters]]`. Put `using MessagePack;` above the namespace and relative `using Domain.X;` directives after it.
- The layout is integer-keyed arrays. Only append new keys; never reuse or reorder them. `OpenChannelIpcRequest` skips Key 1 on purpose, so keep it that way; its Key 3 `PushAmount` (optional, null = no push) was appended in NL-301 without a new `ClientCommand`, because an absent key reads as null in both directions.
- Name DTOs `<Name>IpcRequest` / `<Name>IpcResponse`. Handlers are singletons, so they must resolve scoped services through `CreateScope()`.
- A property with a default value must use `set`, not `init`: the MessagePack analyzer (MsgPack017) warns that an `init` initializer is reset on deserialization (see `ListInvoicesIpcRequest.Take`).

## Dependency rules
- Allowed: `NLightning.Domain` and the MessagePack package (3.1.4). The csproj also references `NLightning.Daemon.Contracts` (same targets as every src project since W4-C), but no source file uses it.
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
There is no onion code here. `PayInvoice` waits for the outcome (bounded by `TimeoutSeconds`) and returns the payment `InFlight` when the wait ends; `ListPayments` is the way to check it later. Guidance, not existing code: further commands such as `SendPayment`, `DecodeInvoice` or a `PaymentStatus` long-poll follow the recipe above. Reuse the existing formatters: `Hash` for payment_hash, `Secret?` for a preimage, `LightningMoney` for amounts, `CompactPubKey` for node ids. There is no formatter for `Domain/Channels/ValueObjects/ShortChannelId.cs`; `ChannelInfoIpcResponse` (ListChannels) sends it as the BOLT 7 uint64 (LND's `chan_id`), so do the same or add a formatter. For BOLT 4 onion failures, return a structured failure DTO (failure_code with its PERM/NODE/UPDATE bits, erring node, channel) rather than a bare `IpcError` string, and add the matching codes to `Domain/Client/Constants/ErrorCodes.cs`.
