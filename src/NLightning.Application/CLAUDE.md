# NLightning.Application — agent guide

Orchestration layer. It sits between the peer/transport layer (Infrastructure) and the domain plus crypto (Domain, Infrastructure.Bitcoin), and holds:
- PeerManager: the peer table, connect/accept, routing channel messages.
- ChannelManager: dispatches channel messages and reacts to blockchain events.
- The BOLT 2 v1 channel-open handlers.
- MessageFactory: builds outbound protocol messages (Infrastructure's `PeerCommunicationService` still constructs error/warning messages directly).

Only channel establishment is implemented. HTLC, commitment, shutdown and reestablish have no handlers yet.

## Layout
- `DependencyInjection.cs`: `AddApplicationServices()`. Registers the Domain services that have no DI of their own (IChannelOpenValidator, IChannelFactory, ICommitmentTransactionModelFactory, IFundingTransactionModelFactory; they need the host's IFeeService and AddBitcoinInfrastructure's ILightningSigner) and IChannelLockProvider, IChannelManager (via a factory lambda), IMessageFactory and IPeerManager as singletons. A reflection scan registers every `IChannelMessageHandler<>` as Scoped. `FundingConfirmedMessageHandler` is registered as a concrete Scoped type. It is called from `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`.
- `Channels/Handlers/Interfaces/IChannelMessageHandler.cs`: `Task<IReadOnlyList<IChannelMessage>> HandleAsync(msg, ChannelState currentState, FeatureOptions negotiatedFeatures, CompactPubKey peerPubKey)`. The returned messages are sent back to the same peer in list order (e.g. revoke_and_ack before commitment_signed); return `[]` for none.
- `Channels/Services/ChannelLockProvider.cs`: `IChannelLockProvider` (Domain) singleton, one non-reentrant `SemaphoreSlim` per channel id, dropped when unused. `AcquireAsync` / blocking `Acquire` return an `IDisposable`.
- `Channels/Handlers/`:
  - `OpenChannel1MessageHandler`: we are the non-initiator. Receives open_channel, replies accept_channel with **our** values (`ChannelParams.Local`, never the opener's; NL-194) and the opener's `channel_type` echoed byte for byte (BOLT 2, NL-218).
  - `AcceptChannel1MessageHandler`: we are the initiator. Receives accept_channel, fails it if `channel_type` differs from the one we sent (`ChannelParams.ToChannelType()`) or a reserve is below the other side's dust limit, stores the peer's values with `ChannelModel.UpdateRemoteParams` (our own, incl. `to_self_delay`, never change), builds the funding tx, replies funding_created. The channel is not persisted until funding_signed (BOLT 2: a funder that has not broadcast SHOULD NOT remember the channel).
  - `FundingCreatedMessageHandler`: non-initiator. Replies funding_signed, persists the channel and watches the funding tx.
  - `FundingSignedMessageHandler`: initiator. Signs, publishes and watches the funding tx. Sends no reply.
  - `ChannelReadyMessageHandler`
  - `FundingConfirmedMessageHandler`: not an IChannelMessageHandler. It is triggered by a funding confirmation and emits channel_ready through its `OnMessageReady` event.
- `Channels/Services/CommitmentSigningService.cs` (BOLT2 plan N3-T5): implements the Domain ports `ICommitmentSigner.SignRemoteCommitment(channel, spec, remoteNumber, remotePoint)` and `ICommitmentVerifier.VerifyLocalCommitment(channel, spec, localNumber, sig, htlcSigs)`, both returning `CommitmentSignatures(txid, sig, htlcSigs)`. It runs factory (spec-driven overload) -> `BuildWithOutputMap` -> `HtlcTransactionModelFactory` -> `IHtlcTransactionBuilder.Build` -> `ILightningSigner` (`SignChannelTransaction` + `SignRemoteHtlcTransactions`, or `ValidateSignature` + `ValidateLocalHtlcSignatures`), so HTLC signatures are always in commitment output order. Singleton in `AddApplicationServices` (one instance behind both interfaces); nothing calls it yet (the N4 engine / N6 handlers will). Vectors: `test/NLightning.Integration.Tests/BOLT3/CommitmentSigningServiceVectorTests` (Appendix C/F: as node B the signatures equal `remote_signature`/`remote_htlc_signature`; as node A the vector signatures verify).
- `Channels/Managers/ChannelManager.cs`: the `switch (message.Type)` in `HandleChannelMessageAsync` (about line 88). Also handles blockchain events: `HandleNewBlockDetected` (ForgetStaleChannels, ConfirmUnconfirmedChannels) and `HandleFundingConfirmationAsync`.
- `Node/Managers/PeerManager.cs`: `ConcurrentDictionary<CompactPubKey, PeerSession>`; a session is one connection: the `PeerModel`, a bounded inbound queue (1024, the transport read loop waits when full) drained by one loop, a close token, and the connection's `PeerOutbox`. Sessions subscribe to the peer service as soon as it is created (before any DB work; `PeerService` replays channel messages and a disconnection that happened before), and only then are installed. Closing a session cancels its loop (queued messages are dropped, the one being handled finishes); the next session of the same peer starts its loop only after the previous loop ended, and `StopAsync` waits for all loops. Replies raised while a loop handles a message go to that loop's session (an `AsyncLocal`), so they are dropped rather than sent on a newer connection. A new connection replaces the current one, except within `SimultaneousConnectWindow` (5 s) of an opposite-direction connection, where the connection initiated by the lower pubkey is kept (LND's `shouldDropLocalConnection`). A runtime drop of a peer with active channels in `IChannelMemoryRepository` starts the backoff reconnect loop unless the disconnect was ours (`DisconnectPeer`, `StopAsync`, replacement). It subscribes to `IChannelManager.OnResponseMessageReady` in its constructor and only enqueues there (never throws). Maps exceptions (through the outbox, so they follow the replies queued before them), always scoped to the message's channel id (never an all-zero `error`, BOLT 1): ChannelErrorException sends `error` for the channel and disconnects, ChannelWarningException sends a `warning` and stays connected (or disconnects after it when `CloseConnection` is set), any other exception (our bug) sends a channel `warning` and disconnects.
- `PeerManager.StartAsync` (NL-201): awaits `IChannelManager.RegisterExistingChannelAsync` (takes the channel lock; memory + signer) for every non-Closed/Stale channel of every stored peer before connecting to any of them; a failing channel is logged and skipped. A peer with such channels that can't be reached gets a background reconnect loop (internal `ReconnectInitialDelay` 5 s, doubling up to `ReconnectMaxDelay` 10 min) that ends when the peer is connected (either direction) or `StopAsync` cancels it (StopAsync waits for the loops before disconnecting peers).
- `Node/Services/PeerOutbox.cs`: the only send path for a peer's channel messages: a FIFO (`System.Threading.Channels`) drained by one loop that awaits each `IPeerService` send. `TryEnqueue`, `TryEnqueueWarning`, and `TryEnqueueDisconnect` (terminal: sent after everything queued before it, later items are refused). Nothing else calls `IPeerService.SendMessageAsync` for channel messages: the Daemon's `OpenChannelClientHandler` calls `IChannelManager.StartOpeningChannelAsync`, which stores the temporary channel and raises open_channel through `OnResponseMessageReady` under the temporary channel's lock.
- `Protocol/Factories/MessageFactory.cs`: implements Domain `IMessageFactory`. Thin payload+TLV builders with no validation.

## Adding an inbound channel message handler (the common task)
1. The message, payload and serializer must already exist. Check `src/NLightning.Domain/Protocol/Messages`, `src/NLightning.Infrastructure.Serialization/{Messages,Payloads,Tlv,Factories}` and `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`.
2. Create `Channels/Handlers/<Msg>MessageHandler.cs : IChannelMessageHandler<<Msg>Message>`. DI registers it automatically; don't add it by hand.
3. Add a `case MessageTypes.<X>:` to `ChannelManager.HandleChannelMessageAsync`. It should cast and call `GetChannelMessageHandler<T>(scope)`. **If you skip this, the message falls into `default`, which throws a channel-scoped ChannelWarningException ("not supported yet"): the message is ignored, the peer gets a `warning` and stays connected.** That is the interim behavior for channel_reestablish (BOLT2 plan N7), update_*, commitment_signed, revoke_and_ack, update_fee (N6) and shutdown/closing_signed (N10); we never fail the channel for a missing handler, since LND would force-close it. Two exceptions: a message for a channel we don't know at all (memory, temporary channels of the peer, DB) gets an `error` for that unknown channel_id (BOLT 1 SHOULD; `ThrowIfUnknownChannelAsync`), and update_fail_malformed_htlc without the BADONION bit gets a channel `warning` plus a disconnect (`ChannelWarningException.CloseConnection`; BOLT 2 allows it instead of failing the channel, which we can't do yet, BOLT2 plan G21).
4. At the top of the handler, check the state and throw ChannelErrorException or ChannelWarningException (`NLightning.Domain.Exceptions`) with `(message, channelId?, peerMessage?)`. The peer receives `PeerMessage` when it is non-empty; otherwise the internal `message` is sent (`src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs`), so always pass a `peerMessage` if the internal text shouldn't leak.
5. Persist through a scoped `IUnitOfWork` (`ChannelDbRepository` then `SaveChangesAsync`) and update `IChannelMemoryRepository`.
6. Build outbound messages only through `IMessageFactory`. Return the replies to the requester as a list, in wire order. To send to a peer other than the requester (e.g. when forwarding), raise an event that ChannelManager converts into `OnResponseMessageReady` (see `FundingConfirmedMessageHandler`), synchronously while the channel's lock is held, so wire order equals persist order. `ChannelManager` already holds the lock of the message's channel id around your handler: don't acquire it again (it is not reentrant), and never take a second channel's lock from inside a handler.
7. Add tests in `test/NLightning.Application.Tests/Channels/Handlers/` (xunit.v3 + Moq). Existing handler tests are named `HandleAsync_Condition_Outcome`; PeerManager tests use `Given_X_When_Y_Then_Z`.

## Conventions
- Use a file-scoped namespace. Put System and Microsoft usings above it, and relative `using Domain.X;` usings after it.
- Store injected dependencies in `_camelCase` readonly fields. Guard hot-path logs with `if (_logger.IsEnabled(...))`.
- Lifetimes: managers are singletons. They create an `IServiceScope` per message or event. Handlers and `IUnitOfWork` are Scoped. Never inject IUnitOfWork into a singleton. `IChannelMemoryRepository` and `IUtxoMemoryRepository` are singletons.
- `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` is a CI gate. `.editorconfig` treats IDE0005/0051/0060/0044 and the naming rules as errors.

## Dependency rules
- The intended direction is Domain only. In practice the csproj also references `NLightning.Infrastructure` and `NLightning.Infrastructure.Bitcoin`, because of IBlockchainMonitor and IBitcoinWalletService (`Infrastructure.Bitcoin/Wallet/Interfaces`), the funding/commitment tx builders (`Infrastructure.Bitcoin/Builders/Interfaces`), and ITcpService, NewPeerConnectedEventArgs and PeerAddress (`Infrastructure/Transport`, `Infrastructure/Protocol/Models`). Don't add more.
- MUST NOT reference: Infrastructure.Serialization, Infrastructure.Persistence(.*), Infrastructure.Repositories, Daemon, Client, Transport.Ipc, Bolt11. Depend on Domain interfaces instead, such as `IUnitOfWork` and `IMessageSerializer`.
- New abstractions belong in Domain as interfaces (as `ISphinxService`/`IOnionReplayCache` already do). Their implementations go in Infrastructure(.Bitcoin).

## Tests
- `dotnet test test/NLightning.Application.Tests` runs all the tests (CI included, since NL-167 added xunit.runner.visualstudio). The xunit v3 runner also works:
  `dotnet run --project test/NLightning.Application.Tests`, or
  `test/NLightning.Application.Tests/bin/Debug/net10.0/NLightning.Application.Tests -class NLightning.Application.Tests.Node.Managers.PeerManagerTests`
- Existing coverage: OpenChannel1, AcceptChannel1 (upfront_shutdown_script, error cleanup, funding outpoint flow), FundingCreated, FundingSigned (rebuilt funding tx guard), FundingConfirmed, ChannelReady, ChannelManager (block events), ChannelManagerConcurrency (lock serialization, reply order, confirmation vs. message), ChannelLockProvider, PeerOutbox and PeerManager (inbound order, outbox FIFO, error after queued replies). Nothing covers MessageFactory.
- Build: `dotnet build NLightning.sln -p:MSBuildWarningsAsMessages=MSB4121`.

## Gotchas
- Opening messages carry the **temporary** channel id. `currentState` only looks up real channels, so it is `None` for them, and the handlers must call `TryGetTemporaryChannelState(peer, id)` themselves. funding_created still carries the temp id. Only the initiator calls `UpgradeChannel`, which fires the `OnChannelUpgraded` event the Daemon's OpenChannelClientHandler waits on.
- `ForgetStaleChannels` only forgets fundee (non-initiator) channels still awaiting funding (`V1FundingSigned`/`ReadyForThem`) whose `FundingCreatedAtBlockHeight` is non-zero and at least 2016 blocks old. `FundingCreatedMessageHandler` sets that field to `IBlockchainMonitor.LastProcessedBlockHeight`; `HandleFundingConfirmationAsync` later overwrites it with the funding tx's first-seen height. Legacy rows with a zero height get it backfilled to the current block height on the next block, so their timeout starts then.
- `ConfirmUnconfirmedChannels` only re-drives `V1FundingSigned`/`ReadyForThem` channels whose watched funding tx is already `IsCompleted` (e.g. confirmed while offline). `HandleFundingConfirmationAsync` and `FundingConfirmedMessageHandler` ignore any other state, so channel_ready is sent once per confirmation. Re-sending channel_ready on reconnect belongs to channel_reestablish (not implemented).
- `AcceptChannel1MessageHandler` deliberately does not persist the initiator channel; `FundingSignedMessageHandler` saves it before publishing the funding tx. On failure it removes the temporary channel by its temporary id (or the upgraded channel) and releases the locked UTXOs; failures in the validation checks before its `try` still rely on the Daemon's OpenChannelClientHandler to release them.
- Per-channel lock (NL-033): `ChannelManager` holds the lock for the message's channel id (the temporary id during the open flow, the real id after) around dispatch and the raising of the replies; block handlers (`ForgetStaleChannels`, the backfill, `HandleFundingConfirmationAsync` → `ConfirmFundingAsync`) take it per channel and re-check the channel's state under it. A channel is keyed by its temporary id until funding_created/funding_signed and by its real id afterwards; the per-peer inbound loop already serializes the open flow. The IPC open-channel operation takes it through `StartOpeningChannelAsync` (store the temporary channel + raise open_channel).
- scid aliases (NL-103): `FundingConfirmedMessageHandler` generates `LocalAliases` that avoid every real scid and alias in `IChannelMemoryRepository`, and reuses them if the channel already has some. `LocalAliases` is never persisted, so after a restart old aliases are forgotten: they are no longer recognized for incoming HTLCs and new ones are generated.
- Handler discovery uses reflection (`Assembly.GetTypes()`), which trimming or AOT may break.

## Onion-routing (BOLT 4) hooks here
The onion library (M1+M2) exists but nothing in Application uses it yet. All of these are DI singletons:
- `ISphinxService` (`src/NLightning.Domain/Protocol/Onion/Interfaces/`, registered by `AddBitcoinInfrastructure`).
- `IHopPayloadSerializer` (`src/NLightning.Domain/Serialization/Interfaces/`, registered by `AddSerializationInfrastructureServices`).
- `IOnionReplayCache` (registered by `AddInfrastructureServices`).
- `HopPayloadValidator` is a static Domain class.

Application must not reference Infrastructure.Serialization, but `IHopPayloadSerializer` is declared there. Before M4 uses it, move the interface to `src/NLightning.Domain/Serialization/Interfaces/`, where `IMessageSerializer` already lives.

Receive path (M4-T2, after the HTLC is irrevocably committed):
1. `var packet = new OnionPacket(add.Payload.OnionRoutingPacket.Span);`
2. `var peeled = sphinx.PeelAsLocalNode(packet, paymentHash, blindedPathTlv?.PathKey);`. Pass the update_add_htlc path_key only, never the payload's current_path_key. It throws `OnionException`:
   - A BADONION code (`ex.FailureCode.IsBadOnion()`) → `update_fail_malformed_htlc` with `ex.FailureData` (sha256_of_onion).
   - `InvalidOnionPayload` → `update_fail_htlc` encrypted with `ex.SharedSecret` (M3).
   - With a path_key, every failure comes back as `InvalidOnionBlinding`.
3. `if (!replayCache.TryAdd(packet.Hmac.Span))`: this is a replay, so fail it. Record the HMAC only after the peel succeeded.
4. `var payload = await hopPayloadSerializer.DeserializeAsync(peeled.Payload);`. Its offsets already count the stripped length prefix.
5. `HopPayloadValidator.Validate(payload, peeled.IsFinal, hasUpdateAddPathKey)`. When update_add_htlc carried a path_key, remap any `OnionException` from here on to `invalid_onion_blinding`.
6. If `peeled.IsFinal`, go to final-hop processing. Otherwise forward `peeled.NextPacket` (`.ToBytes()`) on the channel for `payload.ShortChannelId`.

Keep `peeled.SharedSecret` with the HTLC: failures need it later.

Send path (M4-T6):
- Serialize each `HopPayload` with `IHopPayloadSerializer.SerializeAsync`. That writes no length prefix, and `OnionHop` expects none.
- Call `sphinx.ConstructWithSharedSecrets(hops, sessionKey, paymentHash)` with a fresh CSPRNG session key.
- Keep the per-hop secrets for error decryption (M3).

Other hooks:
- Add ChannelManager cases for UpdateAddHtlc, UpdateFulfillHtlc, UpdateFailHtlc, UpdateFailMalformedHtlc, CommitmentSigned and RevokeAndAck. Each needs a handler. The HTLC commit/revoke state machine (BOLT 2) is a prerequisite.
- Forwarding: resolve the next hop by ShortChannelId or its aliases through `IChannelMemoryRepository.FindChannels`. Then call `MessageFactory.CreateUpdateAddHtlcMessage`, which can't attach a BlindedPathTlv yet. Send the result through the `OnResponseMessageReady` event path.
- Failures: `CreateUpdateFailHtlcMessage` expects an already obfuscated `reason`, and no error-onion code exists yet (M3). `CreateUpdateFailMalformedHtlcMessage` takes sha256_of_onion plus a raw ushort failure code; cast from `FailureCode`.
