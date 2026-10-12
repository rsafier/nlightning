using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Domain.Crypto.Interfaces;
using NLightning.Domain.Crypto.KeyRing;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Infrastructure.Bitcoin;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class RemoteSwapSignerTests(SignerDaemonFixture daemon) : IClassFixture<SignerDaemonFixture>
{
    [Fact]
    public async Task ExplicitAndPublicOnlySharedKeyRequestsUseTheRemotePrivateKey()
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var publicRing = new PublicRing(new RemoteSecureKeyManager(connection));
        var signer = new RemoteSwapSigner(connection, publicRing);
        var locator = new KeyRingLocator(21, 7);
        var resolved = await signer.ResolveAsync(locator, [], TestContext.Current.CancellationToken);
        using var peer = new Key();
        var secret = (byte[])daemon.LocalKeys.GetKeyRingKeyAtIndex(locator.Family, locator.Index);
        try
        {
            using var key = ExtKey.CreateFromBytes(secret).PrivateKey;
            var expected = SHA256.HashData(peer.PubKey.GetSharedPubkey(key).ToBytes());
            Assert.Equal(expected, await signer.SharedKeyAsync(locator, [], peer.PubKey.ToBytes(), TestContext.Current.CancellationToken));
            Assert.Equal(expected, await signer.SharedKeyAsync(null, resolved.PublicKey, peer.PubKey.ToBytes(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => signer.SharedKeyAsync(null, [], peer.PubKey.ToBytes(), TestContext.Current.CancellationToken));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    [Fact]
    public async Task MuSigAllocationAndConsumedPartialReceiptsRecoverAcrossActualSignerRestarts()
    {
        using var crypto = new ServiceCollection().AddBitcoinInfrastructure().BuildServiceProvider();
        var musig = crypto.GetRequiredService<IMusig2Service>();
        using var peer = new Key();
        var digest = SHA256.HashData("remote durable swap"u8);
        SwapSessionWire session;
        SigningRequest allocation;
        byte[] allocationPayload;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            var signer = new RemoteSwapSigner(connection, new PublicRing(new RemoteSecureKeyManager(connection)));
            var locator = new KeyRingLocator(42060, 9);
            var own = await signer.ResolveAsync(locator, [], TestContext.Current.CancellationToken);
            var aggregate = signer.CombineKeys([own.PublicKey, peer.PubKey.ToBytes()], [], []);
            allocation = RemoteSignerConnection.Prepare(SwapSignerOperations.Create, locator, SwapAggregateWire.From(aggregate), Array.Empty<byte[]>());
            allocationPayload = connection.ExecutePayload(allocation);
            session = SignerWire.Read<SwapSessionWire>(connection.Execute(allocation)[0]);
        }
        await daemon.RestartAsync();
        byte[] partial;
        SigningRequest signing;
        var peerNonce = musig.GenerateNonce(peer.PubKey.ToBytes(), new PrivKey(peer.ToBytes()), session.Aggregate.ToAggregate().XOnlyOutputKey);
        using var peerSecret = peerNonce.SecretNonce;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            var receipt = connection.Reconcile(allocation);
            Assert.Equal(RequestOutcome.Completed, receipt.Outcome);
            Assert.Equal(allocationPayload, receipt.Response.Payload.ToByteArray());
            var signer = new RemoteSwapSigner(connection, new PublicRing(new RemoteSecureKeyManager(connection)));
            Assert.True(signer.RegisterNonces(session.Id, [(byte[])peerNonce.PublicNonce]));
            signing = RemoteSignerConnection.Prepare(SwapSignerOperations.Sign, session.Id, digest, false);
            partial = SignerWire.Read<byte[]>(connection.Execute(signing)[0]);
        }
        await daemon.RestartAsync();
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            var receipt = connection.Reconcile(signing);
            Assert.Equal(RequestOutcome.Completed, receipt.Outcome);
            Assert.Equal(partial, SignerWire.Read<byte[]>(SignerWire.Decode(receipt.Response.Payload.ToByteArray())[0]));
            var signer = new RemoteSwapSigner(connection, new PublicRing(new RemoteSecureKeyManager(connection)));
            Assert.Equal(partial, signer.Sign(session.Id, digest, false));
            Assert.Throws<NLightning.Domain.Exceptions.SignerException>(() => signer.Sign(session.Id, SHA256.HashData("changed"u8), false));
            var aggregate = session.Aggregate.ToAggregate();
            var peerSession = musig.CreateSession(aggregate, [new MusigPublicNonce(session.PublicNonce), peerNonce.PublicNonce], digest);
            var peerPartial = musig.Sign(peerSecret, new PrivKey(peer.ToBytes()), peerSession);
            var final = signer.Combine(session.Id, [(byte[])peerPartial]);
            Assert.NotNull(final);
            Assert.True(musig.VerifySignature(final, aggregate.XOnlyOutputKey, digest));
        }
    }

    private sealed class PublicRing(RemoteSecureKeyManager keys) : IKeyRing
    {
        private readonly Dictionary<KeyRingLocator, KeyRingKey> _records = [];
        public Task<KeyRingKey> DeriveNextAsync(int family, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<KeyRingKey> DeriveAsync(KeyRingLocator locator, CancellationToken cancellationToken = default)
        {
            var record = new KeyRingKey(locator, keys.GetKeyRingPublicKey(locator.Family, locator.Index), DateTimeOffset.UtcNow);
            _records[locator] = record;
            return Task.FromResult(record);
        }
        public Task<KeyRingKey?> FindAsync(CompactPubKey publicKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_records.Values.SingleOrDefault(k => k.PublicKey == publicKey));
    }
}