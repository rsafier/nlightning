# NLightning.Application — agent guide

Orchestration layer. It sits between the peer/transport layer (Infrastructure) and the domain plus crypto (Domain, Infrastructure.Bitcoin), and holds:
- PeerManager: the peer table, connect/accept, routing channel messages.
- ChannelManager: dispatches channel messages and reacts to blockchain events.
- The BOLT 2 v1 channel-open handlers.
- MessageFactory: builds outbound protocol messages (Infrastructure's `PeerCommunicationService` still constructs error/warning messages directly).

Only channel establishment is implemented. HTLC, commitment, shutdown and reestablish have no handlers yet.

## Layout
- `DependencyInjection.cs`: `AddApplicationServices()`. Registers IChannelManager (via a factory lambda), IMessageFactory and IPeerManager as singletons. A reflection scan registers every `IChannelMessageHandler<>` as Scoped. `FundingConfirmedMessageHandler` is registered as a concrete Scoped type. It is called from `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`.
- `Channels/Handlers/Interfaces/IChannelMessageHandler.cs`: `Task<IChannelMessage?> HandleAsync(msg, ChannelState currentState, FeatureOptions negotiatedFeatures, CompactPubKey peerPubKey)`. A non-null return value is sent back to the same peer.
- `Channels/Handlers/`:
  - `OpenChannel1MessageHandler`: we are the non-initiator. Receives open_channel, replies accept_channel.
  - `AcceptChannel1MessageHandler`: we are the initiator. Receives accept_channel, builds the funding tx, replies funding_created.
  - `FundingCreatedMessageHandler`: non-initiator. Replies funding_signed, persists the channel and watches the funding tx.
  - `FundingSignedMessageHandler`: initiator. Signs, publishes and watches the funding tx. Sends no reply.
  - `ChannelReadyMessageHandler`
  - `FundingConfirmedMessageHandler`: not an IChannelMessageHandler. It is triggered by a funding confirmation and emits channel_ready through its `OnMessageReady` event.
- `Channels/Managers/ChannelManager.cs`: the `switch (message.Type)` in `HandleChannelMessageAsync` (about line 88). Also handles blockchain events: `HandleNewBlockDetected` (ForgetStaleChannels, ConfirmUnconfirmedChannels) and `HandleFundingConfirmationAsync`.
- `Node/Managers/PeerManager.cs`: `Dictionary<CompactPubKey, PeerModel>`. Sends handler replies. Maps exceptions: ChannelErrorException disconnects the peer, ChannelWarningException sends a warning, any other exception disconnects.
- `Protocol/Factories/MessageFactory.cs`: implements Domain `IMessageFactory`. Thin payload+TLV builders with no validation.

## Adding an inbound channel message handler (the common task)
1. The message, payload and serializer must already exist. Check `src/NLightning.Domain/Protocol/Messages`, `src/NLightning.Infrastructure.Serialization/{Messages,Payloads,Tlv,Factories}` and `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`.
2. Create `Channels/Handlers/<Msg>MessageHandler.cs : IChannelMessageHandler<<Msg>Message>`. DI registers it automatically; don't add it by hand.
3. Add a `case MessageTypes.<X>:` to `ChannelManager.HandleChannelMessageAsync`. It should cast and call `GetChannelMessageHandler<T>(scope)`. **If you skip this, the message falls into `default`, which throws ChannelErrorException("Unknown message type") and disconnects the peer.**
4. At the top of the handler, check the state and throw ChannelErrorException or ChannelWarningException (`NLightning.Domain.Exceptions`) with `(message, channelId?, peerMessage?)`. The peer receives `PeerMessage` when it is non-empty; otherwise the internal `message` is sent (`src/NLightning.Infrastructure/Node/Services/PeerCommunicationService.cs`), so always pass a `peerMessage` if the internal text shouldn't leak.
5. Persist through a scoped `IUnitOfWork` (`ChannelDbRepository` then `SaveChangesAsync`) and update `IChannelMemoryRepository`.
6. Build outbound messages only through `IMessageFactory`. To send to a peer other than the requester (e.g. when forwarding), raise an event that ChannelManager converts into `OnResponseMessageReady` (see `FundingConfirmedMessageHandler`).
7. Add tests in `test/NLightning.Application.Tests/Channels/Handlers/` (xunit.v3 + Moq). Existing handler tests are named `HandleAsync_Condition_Outcome`; PeerManager tests use `Given_X_When_Y_Then_Z`.

## Conventions
- Use a file-scoped namespace. Put System and Microsoft usings above it, and relative `using Domain.X;` usings after it.
- Store injected dependencies in `_camelCase` readonly fields. Guard hot-path logs with `if (_logger.IsEnabled(...))`.
- Lifetimes: managers are singletons. They create an `IServiceScope` per message or event. Handlers and `IUnitOfWork` are Scoped. Never inject IUnitOfWork into a singleton. `IChannelMemoryRepository` and `IUtxoMemoryRepository` are singletons.
- `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` is a CI gate. `.editorconfig` treats IDE0005/0051/0060/0044 and the naming rules as errors.

## Dependency rules
- The intended direction is Domain only. In practice the csproj also references `NLightning.Infrastructure` and `NLightning.Infrastructure.Bitcoin`, because of IBlockchainMonitor and IBitcoinWalletService (`Infrastructure.Bitcoin/Wallet/Interfaces`), the funding/commitment tx builders (`Infrastructure.Bitcoin/Builders/Interfaces`), and ITcpService, NewPeerConnectedEventArgs and PeerAddress (`Infrastructure/Transport`, `Infrastructure/Protocol/Models`). Don't add more.
- MUST NOT reference: Infrastructure.Serialization, Infrastructure.Persistence(.*), Infrastructure.Repositories, Daemon, Client, Transport.Ipc, Bolt11. Depend on Domain interfaces instead, such as `IUnitOfWork` and `IMessageSerializer`.
- New abstractions (e.g. an onion or sphinx service) belong in Domain as interfaces. Their implementations go in Infrastructure(.Bitcoin).

## Tests
- `dotnet test` finds **0 tests** here ("No test is available") because the csproj lacks xunit.runner.visualstudio, which the other test projects reference; CI's `dotnet test` therefore silently skips them. Run the tests with:
  `dotnet run --project test/NLightning.Application.Tests` (24 tests), or
  `test/NLightning.Application.Tests/bin/Debug/net10.0/NLightning.Application.Tests -class NLightning.Application.Tests.Node.Managers.PeerManagerTests`
- Existing coverage: OpenChannel1, FundingCreated and PeerManager. Nothing covers AcceptChannel1, FundingSigned, FundingConfirmed, ChannelReady, ChannelManager or MessageFactory.
- Build: `dotnet build NLightning.sln -p:MSBuildWarningsAsMessages=MSB4121`.

## Gotchas
- Opening messages carry the **temporary** channel id. `currentState` only looks up real channels, so it is `None` for them, and the handlers must call `TryGetTemporaryChannelState(peer, id)` themselves. funding_created still carries the temp id. Only the initiator calls `UpgradeChannel`, which fires the `OnChannelUpgraded` event the Daemon's OpenChannelClientHandler waits on.
- Likely bug: `ForgetStaleChannels` selects `FundingCreatedAtBlockHeight <= height - ChannelConstants.MaxUnconfirmedChannelAge` (2016) without a state filter. That `uint` field is 0 until funding confirms, and is only set in `HandleFundingConfirmationAsync`. So on chains taller than 2016 blocks it can mark unconfirmed channels, and Open channels confirmed more than 2016 blocks ago, as Stale.
- `ConfirmUnconfirmedChannels` re-runs FundingConfirmed on every block for ReadyForUs/ReadyForThem channels. ReadyForThem moves to Open on the first run, but ReadyForUs stays put: the handler only logs the wrong state and doesn't return, so every block it increments CommitmentNumber again and re-sends channel_ready.
- The catch-block cleanup in `AcceptChannel1MessageHandler` looks inverted, and it doesn't release locked UTXOs. The initiator channel isn't persisted until funding_signed arrives.
- No per-channel lock: two messages for the same channel can race on the shared ChannelModel. `PeerManager._peers` is a plain Dictionary. Reply continuations are attached with `ContinueWith` and never awaited, so exceptions in them go unobserved.
- Handler discovery uses reflection (`Assembly.GetTypes()`), which trimming or AOT may break.

## Onion-routing (BOLT 4) hooks here
- Add ChannelManager cases for UpdateAddHtlc, UpdateFulfillHtlc, UpdateFailHtlc, UpdateFailMalformedHtlc, CommitmentSigned and RevokeAndAck. Each needs a handler. The HTLC commit/revoke state machine (BOLT 2) is a prerequisite.
- update_add_htlc handler, in order:
  1. Validate against ChannelConfig.
  2. Store the `Htlc` (Domain/Channels/ValueObjects/Htlc.cs).
  3. Pass `UpdateAddHtlcPayload.OnionRoutingPacket` (a raw 1366-byte `ReadOnlyMemory<byte>?`) and `payment_hash` (as associated data) to a new Domain sphinx-peel interface.
  4. Read the optional `BlindedPathTlv`.
- Forwarding: resolve the next hop by ShortChannelId or its aliases through `IChannelMemoryRepository.FindChannels`. Then call `MessageFactory.CreateUpdateAddHtlcMessage` (MessageFactory.cs:673). It can't attach a BlindedPathTlv yet. Send the result through the `OnResponseMessageReady` event path.
- Failures: `CreateUpdateFailHtlcMessage` (line 710) expects an already obfuscated `reason`. `CreateUpdateFailMalformedHtlcMessage` (line 782) takes sha256_of_onion plus a raw ushort failure code. No failure-code enum exists yet.
