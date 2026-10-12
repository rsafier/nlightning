using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NBitcoin;
using NLightning.Tests.Utils;

namespace NLightning.LndGrpc.Tests.Loop;

using Domain.Crypto.Interfaces;
using Domain.Crypto.KeyRing;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.KeyRing;
using LndGrpc.Services;
using LndGrpc.Signrpc;
using TxOut = NBitcoin.TxOut;

public sealed class SwapSignerTests : IDisposable
{
    private readonly MemoryKeys _records = new();
    private readonly ServiceProvider _provider;
    private readonly ExtKey _master = ExtKey.CreateFromSeed(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private readonly KeyRingService _ring;
    private readonly SwapSigner _signer;
    private readonly IMusig2Service _musig;
    private readonly Mock<IUnitOfWork> _uow = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SwapSignerTests()
    {
        var keys = new Mock<ISecureKeyManager>();
        keys.Setup(k => k.GetKeyRingKeyAtIndex(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int family, int index) => _master.Derive(new KeyPath($"1017'/0'/{family}'/0/{index}")).ToBytes());
        keys.Setup(k => k.GetKeyRingPublicKey(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int family, int index) => new CompactPubKey(_master.Derive(new KeyPath($"1017'/0'/{family}'/0/{index}")).Neuter().PubKey.ToBytes()));
        _uow.SetupGet(u => u.KeyRingDbRepository).Returns(_records);
        _uow.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddBitcoinInfrastructure();
        services.AddSingleton(keys.Object);
        services.AddScoped(_ => _uow.Object);
        _provider = services.BuildServiceProvider();
        _musig = _provider.GetRequiredService<IMusig2Service>();
        _ring = new KeyRingService(keys.Object, _provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new KeyRingOptions()));
        _signer = new SwapSigner(_ring, _musig, _provider.GetRequiredService<ISecp256K1Math>(), Options.Create(new KeyRingOptions()));
    }

    public void Dispose() { _signer.Dispose(); _ring.Dispose(); _provider.Dispose(); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_GenericTweaks_When_CombiningRpcKeys_Then_TheTaprootInternalKeyIncludesThem(bool taproot)
    {
        // Arrange
        using var other = new Key();
        var own = await _ring.DeriveAsync(new KeyRingLocator(99, 8), Ct);
        var tweak = new byte[32];
        tweak[^1] = 7;
        var request = new MuSig2CombineKeysRequest { Version = (MuSig2Version)2 };
        request.AllSignerPubkeys.Add(ByteString.CopyFrom((byte[])own.PublicKey));
        request.AllSignerPubkeys.Add(ByteString.CopyFrom(other.PubKey.ToBytes()));
        request.Tweaks.Add(new TweakDesc { Tweak = ByteString.CopyFrom(tweak), IsXOnly = true });
        if (taproot) request.TaprootTweak = new TaprootTweakDesc { KeySpendOnly = true };
        using var services = new ServiceCollection().AddSingleton<ISwapSigner>(_signer).BuildServiceProvider();
        var rpc = new SignerService(services, Options.Create(new LndGrpc.LndGrpcOptions { EnableSigner = true }),
            NullLogger<SignerService>.Instance);
        // Act
        var result = await rpc.MuSig2CombineKeys(request, null!);
        // Assert
        var preTaproot = _musig.AggregatePubKeys(_musig.SortPubKeys([own.PublicKey, other.PubKey.ToBytes()]),
            [new Domain.Crypto.ValueObjects.MusigTweak(tweak, true)]);
        Assert.Equal(taproot ? preTaproot.XOnlyOutputKey : [], result.TaprootInternalKey.ToByteArray());
    }

    [Fact]
    public async Task Given_ConcurrentRequestsAndRestart_When_Allocating_Then_LocatorsNeverRepeat()
    {
        // Act
        var issued = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _ring.DeriveNextAsync(42060, Ct)));
        var repeated = await _ring.DeriveAsync(issued[7].Locator, Ct);
        using var restarted = new KeyRingService(_provider.GetRequiredService<ISecureKeyManager>(),
            _provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new KeyRingOptions()));
        var next = await restarted.DeriveNextAsync(42060, Ct);
        // Assert
        Assert.Equal(20, issued.Select(k => k.Locator).Distinct().Count());
        Assert.Equal(issued[7].PublicKey, repeated.PublicKey);
        Assert.Equal(20, next.Locator.Index);
        _uow.Verify(u => u.SaveChangesAsync(), Times.Exactly(21));
    }

    [Fact]
    public async Task Given_AFullOrExpiredSigner_When_CreatingSessions_Then_LimitsAndExpiryAreEnforced()
    {
        // Arrange
        var clock = new SteppedClockProvider();
        var options = new KeyRingOptions { MaxSessions = 1, SessionLifetime = TimeSpan.FromMinutes(1) };
        using var signer = new SwapSigner(_ring, _musig, _provider.GetRequiredService<ISecp256K1Math>(), Options.Create(options), clock);
        var locator = new KeyRingLocator(42060, 0);
        var own = await _ring.DeriveAsync(locator, Ct);
        using var remote = new Key();
        var aggregate = signer.CombineKeys([own.PublicKey, remote.PubKey.ToBytes()], [], null);
        var first = await signer.CreateAsync(locator, aggregate, [], Ct);
        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => signer.CreateAsync(locator, aggregate, [], Ct));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Throws<KeyNotFoundException>(() => signer.RegisterNonces(first.Id, []));
        var fresh = await signer.CreateAsync(locator, aggregate, [], Ct);
        Assert.NotEqual(first.PublicNonce, fresh.PublicNonce);
        Assert.Throws<ArgumentException>(() => signer.RegisterNonces(new byte[31], []));
        signer.Cleanup(fresh.Id);
    }

    [Theory]

    [InlineData(0)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Given_AForbiddenFamily_When_Derived_Then_NoKeyOrSignatureIsExposed(int family)
    {
        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _ring.DeriveAsync(new KeyRingLocator(family, 0), Ct));
        Assert.Empty(_records.Keys);
    }

    [Fact]
    public async Task Given_ASaveFailure_When_DeriveNext_Then_NoKeyIsReturned()
    {
        // Arrange
        _uow.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new IOException("disk failure"));
        // Act / Assert
        await Assert.ThrowsAsync<IOException>(() => _ring.DeriveNextAsync(99, Ct));
    }

    [Fact]
    public async Task Given_AnExplicitSharedKey_When_Derived_Then_MatchesCompressedEcdhAndIsStable()
    {
        // Arrange
        using var remote = new Key();
        var loc = new KeyRingLocator(21, 0);
        using var expectedKey = _master.Derive(new KeyPath("1017'/0'/21'/0/0")).PrivateKey;
        var expected = SHA256.HashData(remote.PubKey.GetSharedPubkey(expectedKey).ToBytes());
        // Act
        var first = await _signer.SharedKeyAsync(loc, [], remote.PubKey.ToBytes(), Ct);
        var again = await _signer.SharedKeyAsync(loc, [], remote.PubKey.ToBytes(), Ct);
        // Assert
        Assert.Equal(expected, first);
        Assert.Equal(first, again);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _signer.SharedKeyAsync(null, [], remote.PubKey.ToBytes(), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _signer.ResolveAsync(null, remote.PubKey.ToBytes(), Ct));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    public async Task Given_ARealOutput_When_SignedByLocatorOrPublicKey_Then_WitnessVerifies(int method, uint sighash)
    {
        // Arrange
        var loc = new KeyRingLocator(99, 4);
        var record = await _ring.DeriveAsync(loc, Ct);
        using var key = _master.Derive(new KeyPath("1017'/0'/99'/0/4")).PrivateKey;
        var leafScript = new Script(Op.GetPushOp(key.PubKey.ToBytes()[1..]), OpcodeType.OP_CHECKSIG);
        var leaf = new TapScript(leafScript, (TapLeafVersion)0xc0);
        var root = method == 2 ? leaf.LeafHash.ToBytes() : Array.Empty<byte>();
        var outputScript = method == 0 ? key.PubKey.WitHash.ScriptPubKey
            : key.PubKey.TaprootInternalKey.GetTaprootFullPubKey(method is 2 or 3 ? leaf.LeafHash : null).ScriptPubKey;
        var output = new TxOut(Money.Satoshis(10_000), outputScript);
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(9_000), key.PubKey.WitHash.ScriptPubKey);
        var descriptor = new SwapSignDescriptor(method == 0 ? loc : null, method == 0 ? [] : (byte[])record.PublicKey,
            0, method, sighash, new SwapPrevOutput(10_000, outputScript.ToBytes()), method == 3 ? leafScript.ToBytes() : [], [], [], root);
        // Act
        var signature = await _signer.SignOutputAsync(tx.ToBytes(), descriptor, [new SwapPrevOutput(10_000, outputScript.ToBytes())], Ct);
        // Assert
        if (method == 0)
        {
            tx.Inputs[0].WitScript = new WitScript(new[] { signature.Concat(new[] { (byte)sighash }).ToArray(), key.PubKey.ToBytes() });
            Assert.True(tx.CreateValidator([output]).ValidateInput(0).Error is null or ScriptError.OK);
        }
        else
        {
            Assert.Equal(64, signature.Length);
            var execution = method == 3 ? new TaprootExecutionData(0, leaf.LeafHash) : new TaprootExecutionData(0);
            execution.SigHash = (TaprootSigHash)sighash;
            var digest = tx.GetSignatureHashTaproot([output], execution).ToBytes();
            var publicKey = method == 3 ? key.PubKey.ToBytes()[1..] : outputScript.ToBytes()[2..];
            Assert.True(_musig.VerifySignature(signature, publicKey, digest));
        }
    }

    [Fact]
    public async Task Given_TwoSigners_When_MusigSignsAndCombines_Then_VerifiesAndNonceCannotBeReused()
    {
        // Arrange
        var key = await _ring.DeriveNextAsync(42060, Ct);
        using var remote = new Key();
        var aggregate = _signer.CombineKeys([key.PublicKey, remote.PubKey.ToBytes()], [], []);
        var nonce = _musig.GenerateNonce(remote.PubKey.ToBytes(), new PrivKey(remote.ToBytes()), aggregate.XOnlyOutputKey);
        using var secret = nonce.SecretNonce;
        var ours = await _signer.CreateAsync(key.Locator, aggregate, [(byte[])nonce.PublicNonce], Ct);
        var digest = SHA256.HashData("swap"u8);
        var session = _musig.CreateSession(aggregate, [new MusigPublicNonce(ours.PublicNonce), nonce.PublicNonce], digest);
        // Act
        var partial = _signer.Sign(ours.Id, digest, false);
        var theirPartial = _musig.Sign(secret, new PrivKey(remote.ToBytes()), session);
        Assert.Throws<InvalidOperationException>(() => _signer.Sign(ours.Id, SHA256.HashData("other"u8), false));
        var combined = _signer.Combine(ours.Id, [(byte[])theirPartial]);
        // Assert
        Assert.Equal(32, partial.Length);
        Assert.NotNull(combined);
        Assert.True(_musig.VerifySignature(combined, aggregate.XOnlyOutputKey, digest));
        Assert.Throws<KeyNotFoundException>(() => _signer.Sign(ours.Id, digest, false));
        _signer.Cleanup(ours.Id); // idempotent
    }

    [Fact]
    public async Task Given_DurableSession_When_RestartedBeforeAndAfterSigning_Then_NonceAndExactPartialRecover()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"nltg-swap-{Guid.NewGuid():N}.journal");
        var locator = new KeyRingLocator(42060, 17);
        var own = await _ring.DeriveAsync(locator, Ct);
        using var other = new Key();
        var aggregate = _signer.CombineKeys([own.PublicKey, other.PubKey.ToBytes()], [], []);
        var peerNonce = _musig.GenerateNonce(other.PubKey.ToBytes(), new PrivKey(other.ToBytes()), aggregate.XOnlyOutputKey);
        using var peerSecret = peerNonce.SecretNonce;
        var digest = SHA256.HashData("durable swap"u8);
        try
        {
            SwapMusigSession allocated;
            using (var first = DurableSigner(path))
                allocated = await first.CreateAsync(locator, aggregate, [(byte[])peerNonce.PublicNonce], Ct);
            byte[] partial;
            using (var second = DurableSigner(path))
            {
                // Act: unsigned secret nonce survives the process boundary inside its encrypted journal.
                partial = second.Sign(allocated.Id, digest, false);
                Assert.Equal(partial, second.Sign(allocated.Id, digest, false));
            }
            using var third = DurableSigner(path);
            Assert.Equal(partial, third.Sign(allocated.Id, digest, false));
            Assert.Throws<InvalidOperationException>(() => third.Sign(allocated.Id, SHA256.HashData("changed swap"u8), false));
            var signing = _musig.CreateSession(aggregate, [new MusigPublicNonce(allocated.PublicNonce), peerNonce.PublicNonce], digest);
            var peerPartial = _musig.Sign(peerSecret, new PrivKey(other.ToBytes()), signing);
            var final = third.Combine(allocated.Id, [(byte[])peerPartial]);
            Assert.NotNull(final);
            Assert.True(_musig.VerifySignature(final, aggregate.XOnlyOutputKey, digest));
            Assert.Throws<KeyNotFoundException>(() => third.Sign(allocated.Id, digest, false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Given_ConsumedNonceWithoutResult_When_Restarted_Then_SigningIsBlocked()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"nltg-swap-{Guid.NewGuid():N}.journal");
        var locator = new KeyRingLocator(42060, 18);
        var own = await _ring.DeriveAsync(locator, Ct);
        using var other = new Key();
        var aggregate = _signer.CombineKeys([own.PublicKey, other.PubKey.ToBytes()], [], null);
        var peerNonce = _musig.GenerateNonce(other.PubKey.ToBytes(), new PrivKey(other.ToBytes()), aggregate.XOnlyOutputKey);
        using var peerSecret = peerNonce.SecretNonce;
        var digest = SHA256.HashData("uncertain swap"u8);
        var fault = new Mock<IMusig2Service>();
        fault.Setup(m => m.AggregatePubKeys(It.IsAny<IReadOnlyList<CompactPubKey>>(), It.IsAny<IReadOnlyList<MusigTweak>?>()))
            .Returns((IReadOnlyList<CompactPubKey> publicKeys, IReadOnlyList<MusigTweak>? tweaks) => _musig.AggregatePubKeys(publicKeys, tweaks));
        fault.Setup(m => m.GenerateNonce(It.IsAny<byte[]>(), It.IsAny<CompactPubKey>(), It.IsAny<PrivKey?>(),
                It.IsAny<byte[]?>(), It.IsAny<byte[]?>(), It.IsAny<byte[]?>()))
            .Returns((byte[] randomness, CompactPubKey pubkey, PrivKey? privateKey, byte[]? outputKey, byte[]? message, byte[]? extra) =>
                _musig.GenerateNonce(randomness, pubkey, privateKey, outputKey, message, extra));
        fault.Setup(m => m.CreateSession(It.IsAny<Domain.Crypto.Models.MusigKeyAggregate>(), It.IsAny<IReadOnlyList<MusigPublicNonce>>(), It.IsAny<ReadOnlyMemory<byte>>()))
            .Returns((Domain.Crypto.Models.MusigKeyAggregate value, IReadOnlyList<MusigPublicNonce> nonces, ReadOnlyMemory<byte> message) =>
                _musig.CreateSession(value, nonces, message));
        fault.Setup(m => m.Sign(It.IsAny<MusigSecretNonce>(), It.IsAny<PrivKey>(), It.IsAny<Domain.Crypto.Models.MusigSigningSession>()))
            .Throws(new IOException("process stops after durable nonce consumption"));
        try
        {
            SwapMusigSession allocated;
            using (var first = new SwapSigner(_ring, fault.Object, _provider.GetRequiredService<ISecp256K1Math>(),
                Options.Create(new KeyRingOptions()), sessionStatePath: path))
            {
                allocated = await first.CreateAsync(locator, aggregate, [(byte[])peerNonce.PublicNonce], Ct);
                Assert.Throws<IOException>(() => first.Sign(allocated.Id, digest, false));
            }
            using var restarted = DurableSigner(path);
            Assert.Throws<InvalidOperationException>(() => restarted.Sign(allocated.Id, digest, false));
            Assert.Throws<InvalidOperationException>(() => restarted.Sign(allocated.Id, SHA256.HashData("different"u8), false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Given_EncryptedJournal_When_LockedOrCorrupted_Then_NoSecondSignerCanProceed()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"nltg-swap-{Guid.NewGuid():N}.journal");
        try
        {
            using (var first = DurableSigner(path))
            {
                Assert.Throws<IOException>(() => DurableSigner(path));
                var locator = new KeyRingLocator(42060, 19);
                var own = await _ring.DeriveAsync(locator, Ct);
                using var other = new Key();
                var aggregate = first.CombineKeys([own.PublicKey, other.PubKey.ToBytes()], [], null);
                await first.CreateAsync(locator, aggregate, [], Ct);
            }
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);
            Assert.ThrowsAny<CryptographicException>(() => DurableSigner(path));
        }
        finally { File.Delete(path); }
    }

    private SwapSigner DurableSigner(string path) => new(_ring, _musig, _provider.GetRequiredService<ISecp256K1Math>(),
        Options.Create(new KeyRingOptions()), sessionStatePath: path);

    private sealed class MemoryKeys : IKeyRingDbRepository
    {
        public List<KeyRingKey> Keys { get; } = [];
        public void Add(KeyRingKey key) => Keys.Add(key);
        public Task<int?> LastIndexAsync(int family) => Task.FromResult(Keys.Where(k => k.Locator.Family == family).Max(k => (int?)k.Locator.Index));
        public Task<KeyRingKey?> GetAsync(KeyRingLocator locator) => Task.FromResult(Keys.SingleOrDefault(k => k.Locator == locator));
        public Task<KeyRingKey?> FindAsync(CompactPubKey key) => Task.FromResult(Keys.SingleOrDefault(k => k.PublicKey == key));
    }
}