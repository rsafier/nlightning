# NLightning.Infrastructure — agent notes

## Purpose
This project implements the Domain interfaces for symmetric crypto, BOLT 8 transport, per-peer comms and protocol helpers. Its only project reference is `NLightning.Domain` (NuGet: DnsClient, Logging/Options abstractions, plus a per-config crypto backend; see `NLightning.Infrastructure.csproj`).
secp256k1 / NBitcoin work does NOT belong here. `IEcdh` is declared in `Crypto/Interfaces/IEcdh.cs` but implemented in `NLightning.Infrastructure.Bitcoin/Crypto/Functions/Ecdh.cs`.
Message handlers live in `NLightning.Application` (e.g. `src/NLightning.Application/Channels/Handlers/`). Byte serializers live in `NLightning.Infrastructure.Serialization`.

## Layout
- `Crypto/` — `ICryptoProvider` (internal backend), `CryptoFactory` (picks the backend at compile time), `Hashes/Sha256`, `Hashes/Argon2Id`, `Functions/HmacSha256` (public, any key length; keys > 64 B are hashed first), `Functions/Hkdf` (Noise HKDF, delegates to `HmacSha256`), `Ciphers/ChaCha20Poly1305` and `XChaCha20Poly1305`, `Ciphers/ChaCha20Stream` (raw IETF ChaCha20 keystream, 12 zero-byte nonce, block counter 0, over `ICryptoProvider.StreamChaCha20IetfXor`; used by Sphinx), `Primitives/SecureMemory`, `Providers/{Libsodium,Native,JS}`. `CryptoFactory` selects via `CRYPTO_LIBSODIUM` / `CRYPTO_NATIVE` / `CRYPTO_JS` defines set in the csproj per configuration.
- `Transport/` — BOLT 8 Noise_XK: `Handshake/States/{HandshakeState,SymmetricState,CipherState}.cs`, `Encryption/Transport.cs` (framing plus rekey), `Services/{HandshakeService,TransportService,TcpService}.cs`, `Factories/TransportServiceFactory.cs`.
- `Node/` — `Factories/PeerServiceFactory.cs` builds the stack Transport -> MessageService -> PingPong -> PeerCommunicationService -> PeerService. `Services/PeerService.cs` handles init validation and dispatch. `Models/KeyFileData.cs` is the key-file JSON.
- `Protocol/` — `Services/{MessageService,PingPongService,SecretStorageService}.cs`, `Factories/{ChannelIdFactory,MessageServiceFactory,TlvConverterFactory}.cs`, `Tlv/Converters/*` (typed-TLV <-> BaseTlv converters; note the file is misspelled `UpfronfShutdownScriptTlvConverter.cs`; the 8 BOLT 4 hop-payload converters are in `Tlv/Converters/Onion/`), `Onion/OnionReplayCache.cs`, `Validators/Tx*Validator.cs` (interactive-tx), `Models/PeerAddress.cs`, `Constants/ProtocolConstants.cs`. `Services/DnsSeedClient.cs` is fully commented out.
- `Converters/EndianBitConverter.cs` — big- and little-endian byte helpers (its trim/pad is not truncated-int compliant). `Converters/TruncatedInt.cs` — strict BOLT 1 tu16/tu32/tu64 encode/decode (minimal encoding enforced). `Exceptions/` — Message/PayloadSerializationException, InvalidMessageException, ConnectionTimeoutException.
- `DependencyInjection.cs` — `AddInfrastructureServices()` is called from `src/NLightning.Daemon/Extensions/NodeServiceExtensions.cs`.

## Dependency rules
- Do NOT reference Application, Infrastructure.Bitcoin, Serialization, Persistence, Repositories, Daemon or NBitcoin. Dependencies point inward only (to Domain).
- Service interfaces belong in `src/NLightning.Domain/**/Interfaces`; only implementations go here. Existing exceptions declared in this project: internal `ICryptoProvider`, `IHandshakeService`, `IHandshakeState`; public `IEcdh`, `ITcpService`, `IInteractiveTransactionService`, `IInteractiveTransactionServiceFactory`.
- Internals are visible to Infrastructure.Bitcoin, Daemon, Infrastructure.Tests, Integration.Tests, Tests.Utils, BlazorTestApp and Moq (`DynamicProxyGenAssembly2`) via `AssemblyInfo.cs`.

## Adding a TLV (most common task here)
1. Add the type number to `src/NLightning.Domain/Protocol/Constants/TlvConstants.cs`. Numbers are per-message and collide (0 and 1 are reused), so always use the named constant.
2. Add a `BaseTlv` subclass in `src/NLightning.Domain/Protocol/Tlv/`.
3. Add `Protocol/Tlv/Converters/XTlvConverter.cs : ITlvConverter<XTlv>`. `ConvertFromBase` validates Type and Length and throws `InvalidCastException`. Implement the non-generic interface explicitly, marked `[ExcludeFromCodeCoverage]`.
4. Register the converter in `Protocol/Factories/TlvConverterFactory.RegisterConverters()`.
5. Add a sample instance to `CreateSampleTlvs` in `test/NLightning.Infrastructure.Serialization.Tests/Tlv/TlvStreamSerializerTests.cs` (`TlvStreamSerializer` finds the converter by runtime type; the test fails for registered types without a sample).
6. Add tests in `test/NLightning.Infrastructure.Tests/Protocol/Tlv/Converters/XTlvConverterTests.cs`.

## Adding a crypto primitive
Add the method to `ICryptoProvider` and implement it in ALL THREE providers: Libsodium (`[LibraryImport("libsodium")]` in `LibsodiumWrapper`), Native (`NativeCryptoProvider`: BCL, BouncyCastle, Konscious Argon2) and JS (`[JSImport]` in `LibsodiumJsWrapper`). Then wrap it in a sealed `IDisposable` class that gets its provider from `CryptoFactory.GetCryptoProvider()`.
Build `-c Release` and `-c Release.Native`. CI (`.github/workflows/dotnet.wasm.yml`) also builds `Release.Wasm`; `Crypto/Providers/JS/package.json` pins linux-x64 esbuild/rollup binaries, so that config likely only builds on linux-x64.

## Conventions
- Use a file-scoped namespace, then relative `using Domain.X;` directives AFTER the namespace line. System and Microsoft usings go above it.
- Naming (enforced by `.editorconfig`): private fields `_camelCase`, static fields `s_camelCase`. No `this.`. Unused usings (IDE0005) and unused parameters (IDE0060) are errors.
- Big-endian on the wire. Prefer `BinaryPrimitives` in new code, because EndianBitConverter's little-endian trim/pad behavior is suspect.
- Per-connection objects are built by factories, not DI (exception: `PeerServiceFactory` resolves the transient `IPingPongService` from `IServiceProvider`). Layers communicate through events (`MessageReceived`, `OnMessageReceived`, `DisconnectEvent`).
- `ErrorException` means send `error` and disconnect. `WarningException` means send `warning`. `ConnectionException` is local only.

## Tests & commands
- `dotnet build -c Release -p:MSBuildWarningsAsMessages=MSB4121`
- `dotnet format --verify-no-changes --exclude "**/BlazorTests/**"` (the CI gate; `dotnet build` does not catch these)
- `dotnet test test/NLightning.Infrastructure.Tests --filter "FullyQualifiedName!~Given_HttpAddress_When_ConstructingPeerAddress"` (that test resolves `dnstest.nlightn.ing` via live DNS)
- CI runs `dotnet test --filter 'FullyQualifiedName!~Docker'` (Docker tests live in `test/NLightning.Integration.Tests/Docker/`).
- BOLT 8 vectors: `dotnet test test/NLightning.Integration.Tests --filter "FullyQualifiedName~BOLT8"`
- Crypto provider tests are `#if`-gated: `SodiumCryptoProviderTests` is `#if CRYPTO_LIBSODIUM` (default configs), `NativeCryptoProviderTests` is `#if CRYPTO_NATIVE` (`*.Native`).

## Gotchas
- `TlvConverterFactory` is registered in `NLightning.Infrastructure.Bitcoin/DependencyInjection.cs`, not here.
- `Transport` is both a namespace (`NLightning.Infrastructure.Transport`) and the internal class `Transport/Encryption/Transport.cs`. Refer to the class as `Encryption.Transport`.
- `TransportService.WriteMessageAsync` encrypts BEFORE taking the write semaphore. Concurrent senders can reorder nonces.
- `TransportService.ReadResponseAsync` uses `ReadAsync`, not `ReadExactlyAsync`, so a partial TCP read kills the connection.
- `MessageService.ReceiveMessage` deserializes synchronously under a lock on the read loop.
- `PingPongService.StartPingAsync`: the pong-timeout disconnect path is dead. A timeout makes `Task.Delay` Canceled (not Faulted), so the loop `continue`s and re-pings instead of raising `DisconnectEvent`.
- `RemoteAddressTlvConverter`: Tor v3 decode reads 36 address bytes (`Value[1..37]`) instead of 35; type 5 (DNS) encode overwrites `customAddressBytes[1]` and decode expects length+3 instead of length+4. No tests exist. (`TlvStreamSerializer` now finds converters by runtime type, so it no longer throws for `RemoteAddressTlv`.)
- `Tx*Validator` serial_id parity check only runs when `isInitiator` is true and rejects odd ids; whether `isInitiator` means local or remote is undocumented, so verify against BOLT 2 before relying on it. `TxAddInputValidator.Validate` is `async void`.
- `SecretStorageService.GetBasepointPrivateKey` and `LoadFromIndex` throw `NotImplementedException`.
- `PeerService.HandleMessage` silently drops anything that isn't an IChannelMessage, error or warning (gossip, onion_message, stfu).
- `Argon2Id.DeriveKeyMemLimit` is `1 << 16` bytes = 64 KiB (the comment says MiB). Changing it breaks existing key files.
- `new Sha256()` allocates state via `ICryptoProvider.MemoryAlloc` (sodium_malloc on the libsodium backend) on every instance. Reuse instances in hot loops.

## Onion routing (BOLT 4)
- Crypto primitives (M1): `Crypto/Functions/HmacSha256.cs` (RFC 4231 tests) and `Crypto/Ciphers/ChaCha20Stream.cs` over `ICryptoProvider.StreamChaCha20IetfXor`, implemented in all three providers: libsodium `crypto_stream_chacha20_ietf_xor`, Native BouncyCastle `ChaCha7539Engine` (counter 0), JS `sodium.crypto_stream_chacha20_ietf_xor` (compiled only in CI's `Release.Wasm`). Tests: `test/NLightning.Infrastructure.Tests/Crypto/{Functions/HmacSha256Tests,Ciphers/ChaCha20StreamTests}.cs` (RFC 8439 vectors, counter 0); run them under both `-c Release` and `-c Release.Native`. Never derive a keystream from the AEAD (it starts at counter 1).
- `Protocol/Tlv/Converters/Onion/*TlvConverter.cs`: one converter per hop-payload TLV (2, 4, 6, 8, 10, 12, 16, 18), registered in `TlvConverterFactory`. Type numbers are in `src/NLightning.Domain/Protocol/Onion/Constants/OnionPayloadTlvTypes.cs`, not `TlvConstants`. Truncated ints go through `Converters/TruncatedInt`. `CurrentPathKeyTlvConverter` checks only length/prefix, not that the point is on the curve (M5 must validate it via `ISecp256K1Math`).
- `Protocol/Onion/OnionReplayCache.cs`: bounded (FIFO-evicting, default 100k), thread-safe, in-memory `IOnionReplayCache` (Domain interface); registered as a singleton in `AddInfrastructureServices`. Call `TryAdd` only after a successful peel (HMAC verified). Not persistent: replays older than the capacity or across restarts are not detected (M4: key/expire by cltv_expiry and persist).
- ECDH is `IEcdh.SecP256K1Dh` (Infrastructure.Bitcoin); EC point/scalar math is `ISecp256K1Math` (Domain interface, Infrastructure.Bitcoin impl). The Sphinx core itself is `ISphinxService` in `src/NLightning.Infrastructure.Bitcoin/Onion/`.
- Inbound `update_add_htlc` reaches the Application layer via `PeerService.OnChannelMessageReceived`. Fix the ReadExactly and send-lock issues above before forwarding HTLCs.
- `onion_message` (513) is not in `src/NLightning.Domain/Protocol/Constants/MessageTypes.cs`; it needs a type constant, a new branch in `PeerService.HandleMessage`, and a new event on `src/NLightning.Domain/Node/Interfaces/IPeerService.cs`.
