using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Daemon.Extensions;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Enums;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class RemoteSignerProcessTests(SignerDaemonFixture daemon) : IClassFixture<SignerDaemonFixture>
{
    [Fact]
    public void IdentityEcdhInvoiceAndNodeSignaturesMatchExistingLocalSigner()
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var remoteKeys = new RemoteSecureKeyManager(connection);
        var signer = new RemoteLightningSigner(connection);
        Assert.Equal(daemon.LocalKeys.GetNodePubKey(), remoteKeys.GetNodePubKey());
        Assert.Equal(daemon.LocalSigner.GetNodePublicKey(), signer.GetNodePublicKey());
        var peer = new Key().PubKey.ToBytes();
        var localSecret = new byte[32];
        var remoteSecret = new byte[32];
        daemon.LocalKeys.ComputeNodeSharedSecret(peer, localSecret);
        remoteKeys.ComputeNodeSharedSecret(peer, remoteSecret);
        Assert.Equal(localSecret, remoteSecret);
        var hash = new NLightning.Domain.Crypto.ValueObjects.Hash(SHA256.HashData("signer-test"u8));
        var signature = signer.SignNodeMessage(hash);
        Assert.Equal(daemon.LocalSigner.SignNodeMessage(hash), signature);
        Assert.True(daemon.LocalSigner.VerifyNodeMessage(hash, signature, signer.GetNodePublicKey()));
        var words = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(daemon.LocalKeys.SignBolt11Invoice("lnbcrt10u", words),
                     remoteKeys.SignBolt11Invoice("lnbcrt10u", words));
        Assert.Throws<NotSupportedException>(() => remoteKeys.GetNodeKeyPair());
        Assert.Throws<NotSupportedException>(() => remoteKeys.GetChannelKeyAtIndex(1));
        Assert.Throws<NotSupportedException>(() => remoteKeys.GetDepositP2WpkhKeyAtIndex(0, false));
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh, false)]
    [InlineData(AddressType.P2Wpkh, true)]
    [InlineData(AddressType.P2Tr, false)]
    [InlineData(AddressType.P2Tr, true)]
    public void WalletPublicDerivationMatchesLocalKeys(AddressType type, bool change)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        Assert.Equal(daemon.LocalKeys.GetWalletPublicKey(7, change, type), keys.GetWalletPublicKey(7, change, type));
    }

    [Fact]
    public void RegisteredChannelSignsCommitmentAndEnforcesRevocationGuards()
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var signer = new RemoteLightningSigner(connection);
        var index = signer.CreateNewChannel(out var basepoints, out var firstPoint);
        Assert.Equal(daemon.LocalSigner.GetChannelBasepoints(index), basepoints);
        Assert.Equal(daemon.LocalSigner.GetPerCommitmentPoint(index, 0), firstPoint);
        var channelId = new ChannelId(RandomUtils.GetBytes(32));
        var funding = new TxId(RandomUtils.GetBytes(32));
        var info = new ChannelSigningInfo(funding, 0, 100_000, basepoints.FundingPubKey,
                                         new Key().PubKey.ToBytes(), index);
        signer.RegisterChannel(channelId, info);
        daemon.LocalSigner.RegisterChannel(channelId, info);
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])funding), 0)));
        tx.Outputs.Add(Money.Satoshis(99_000), new Key().PubKey.WitHash.ScriptPubKey);
        var unsigned = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        Assert.Equal(daemon.LocalSigner.SignChannelTransaction(channelId, unsigned),
                     signer.SignChannelTransaction(channelId, unsigned));
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(channelId, 0));
        signer.AdvanceLocalCommitment(channelId, 1);
        daemon.LocalSigner.AdvanceLocalCommitment(channelId, 1);
        Assert.Equal(daemon.LocalSigner.RevealPerCommitmentSecret(channelId, 0),
                     signer.RevealPerCommitmentSecret(channelId, 0));
        signer.AdvanceLocalCommitment(channelId, 2);
        signer.MarkBroadcastSigned(channelId, 1);
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(channelId, 1));
        signer.MarkDataLoss(channelId);
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(channelId, unsigned));
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh)]
    [InlineData(AddressType.P2Tr)]
    public void ReservedWalletSpendIsSignedAndIndependentlyVerified(AddressType type)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        var wallet = new UtxoMemoryRepository();
        var pubKey = new PubKey(keys.GetWalletPublicKey(2, false, type));
        var address = pubKey.GetAddress(type == AddressType.P2Wpkh ? ScriptPubKeyType.Segwit : ScriptPubKeyType.TaprootBIP86,
                                       Network.RegTest);
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var model = new UtxoModel(new TxId(outpoint.Hash.ToBytes()), 0, LightningMoney.Satoshis(50_000), 100,
                                 new WalletAddressModel(type, 2, false, address.ToString()));
        wallet.Add(model);
        var reservation = Guid.NewGuid();
        Assert.True(wallet.TryReserveForFee([(model.TxId, model.Index)], reservation));
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(outpoint));
        tx.Outputs.Add(Money.Satoshis(49_000), new Key().PubKey.WitHash.ScriptPubKey);
        var unsigned = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        var signer = new RemoteLightningSigner(connection, wallet: wallet);
        Assert.True(signer.SignWalletTransaction(unsigned, reservation, []));
        var result = Transaction.Load(unsigned.RawTxBytes, Network.RegTest);
        var validator = result.CreateValidator([new TxOut(Money.Satoshis(50_000), address.ScriptPubKey)]);
        Assert.True(validator.ValidateInput(0).Error is null or ScriptError.OK);
        var wrongReservation = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        Assert.Throws<SignerException>(() => signer.SignWalletTransaction(wrongReservation, Guid.NewGuid(), []));
        Assert.Equal(tx.ToBytes(), wrongReservation.RawTxBytes);
        wallet.ReleaseFeeReservation(reservation);
        var released = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        Assert.Throws<SignerException>(() => signer.SignWalletTransaction(released, reservation, []));
        Assert.Equal(tx.ToBytes(), released.RawTxBytes);
    }

    [Fact]
    public void TaprootVerificationNonceSupportsNullFundingContextAndRoundTrips()
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var signer = new RemoteLightningSigner(connection);
        var index = signer.CreateNewChannel(out _, out _);
        Assert.Equal(daemon.LocalSigner.GetLocalVerificationNonce(index, null, 0),
                     signer.GetLocalVerificationNonce(index, null, 0));
        var funding = new TxId(RandomUtils.GetBytes(32));
        Assert.Equal(daemon.LocalSigner.GetLocalVerificationNonce(index, funding, 5),
                     signer.GetLocalVerificationNonce(index, funding, 5));
    }

    [Fact]
    public void TaprootClosingNonceIsConsumedOnceAndPartialSignaturesRoundTrip()
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var signer = new RemoteLightningSigner(connection);
        var remoteIndex = signer.CreateNewChannel(out var remotePoints, out _);
        var peerIndex = signer.CreateNewChannel(out var peerPoints, out _);
        var id = new ChannelId(RandomUtils.GetBytes(32));
        var funding = new TxId(RandomUtils.GetBytes(32));
        signer.RegisterChannel(id, new ChannelSigningInfo(funding, 0, 100_000, remotePoints.FundingPubKey,
                                                         peerPoints.FundingPubKey, remoteIndex)
        { IsSimpleTaproot = true });
        daemon.LocalSigner.RegisterChannel(id, new ChannelSigningInfo(funding, 0, 100_000, peerPoints.FundingPubKey,
                                                                    remotePoints.FundingPubKey, peerIndex)
        { IsSimpleTaproot = true });
        var nonce = signer.CreateClosingNonce(id);
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])funding), 0)));
        tx.Outputs.Add(Money.Satoshis(99_000), new Key().PubKey.WitHash.ScriptPubKey);
        var unsigned = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        var closer = daemon.LocalSigner.SignClosingAsCloser(id, unsigned, nonce);
        var closee = signer.SignClosingAsClosee(id, unsigned, nonce, closer);
        daemon.LocalSigner.ValidateClosingPartialSignature(id, unsigned, closee, nonce, closer.PublicNonce);
        var signed = signer.AggregateClosingSignature(id, unsigned, closee, nonce,
                                                     closer.PartialSignature, closer.PublicNonce);
        Assert.Equal(64, Transaction.Load(signed.RawTxBytes, Network.RegTest).Inputs[0].WitScript[0].Length);
        // Exact retries are returned from the signer journal; another transaction must never reuse the nonce.
        Assert.Equal(closee, signer.SignClosingAsClosee(id, unsigned, nonce, closer));
        tx.Outputs[0].Value = Money.Satoshis(98_000);
        var changed = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        var changedCloser = daemon.LocalSigner.SignClosingAsCloser(id, changed, nonce);
        Assert.Throws<SignerException>(() => signer.SignClosingAsClosee(id, changed, nonce, changedCloser));
        signer.MarkDataLoss(id);
        Assert.Throws<SignerException>(() => signer.SignClosingAsClosee(id, unsigned, nonce, closer));
    }

    [Theory]
    [InlineData("mainnet", null)]
    [InlineData("regtest", "wrong-auth-token-00000000000000000000000000000")]
    public void NetworkAndAuthenticationMismatchFailDuringHandshake(string network, string? token)
    {
        Assert.ThrowsAny<Exception>(() => new RemoteSignerConnection(daemon.Options(network, token)));
    }

    [Theory]
    [InlineData(NodeDataPurpose.ChannelBackup)]
    [InlineData(NodeDataPurpose.PeerStorage)]
    public void PurposeScopedEncryptionRoundTripsAndPreservesAuthenticationFailures(NodeDataPurpose purpose)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        var nonce = RandomNumberGenerator.GetBytes(24);
        var associatedData = "remote-context"u8.ToArray();
        var plaintext = "private-node-data"u8.ToArray();
        var ciphertext = keys.EncryptNodeData(purpose, nonce, associatedData, plaintext);
        Assert.Equal(daemon.LocalKeys.EncryptNodeData(purpose, nonce, associatedData, plaintext), ciphertext);
        Assert.Equal(plaintext, keys.DecryptNodeData(purpose, nonce, associatedData, ciphertext));
        ciphertext[0] ^= 1;
        Assert.Throws<CryptographicException>(() => keys.DecryptNodeData(purpose, nonce, associatedData, ciphertext));
        Assert.Equal(daemon.LocalKeys.ComputeOfferPathId("offer"u8.ToArray()),
                     keys.ComputeOfferPathId("offer"u8.ToArray()));
    }

    [Fact]
    public async Task ProtocolVersionAndRequestIdReuseAreRejected()
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(daemon.SocketPath), cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        var client = new SignerRpc.SignerRpcClient(channel);
        var headers = new Metadata { { "x-signer-token", SignerDaemonFixture.Token } };
        var request = new SigningRequest
        {
            Version = 999,
            RequestId = Guid.NewGuid().ToString("N"),
            Operation = 0,
            Payload = ByteString.CopyFrom(SignerWire.Encode([]))
        };
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ExecuteAsync(request, headers, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        request.Version = 1;
        var original = await client.ExecuteAsync(request, headers, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
        var repeated = await client.ExecuteAsync(request, headers, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
        Assert.Equal(original.Payload, repeated.Payload);
        request.Operation = SignerOperations.GetNodePublicKey;
        error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ExecuteAsync(request, headers, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public void WrongExpectedIdentityFailsBeforeSigning()
    {
        var options = daemon.Options();
        options.ExpectedNodePublicKey = Convert.ToHexString(new Key().PubKey.ToBytes());
        Assert.Throws<RemoteSignerTransportException>(() => new RemoteSignerConnection(options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeNodeDependencyGraphUsesRemoteSignerWithoutPrivateKeyProvider(bool enableSilentPayments)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Network"] = "regtest",
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=:memory:",
            ["Signing:Mode"] = "RemoteNative",
            ["Signing:SocketPath"] = daemon.SocketPath,
            ["Signing:AuthTokenFile"] = Path.Combine(daemon.DirectoryPath, "token"),
            ["SilentPayments:Enabled"] = enableSilentPayments.ToString()
        }).Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, keys, connection);
        using var provider = services.BuildServiceProvider();
        if (enableSilentPayments)
        {
            var failure = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NLightning.Domain.Bitcoin.SilentPayments.SilentPaymentsOptions>>().Value);
            Assert.Contains("Silent-payment scanning and receiving are not supported by the remote signer.", failure.Failures);
            return;
        }
        var registeredKeys = provider.GetRequiredService<ISecureKeyManager>();
        Assert.Same(keys, registeredKeys);
        Assert.Same(provider.GetRequiredService<RemoteSigningWorkflowCoordinator>(),
            provider.GetRequiredService<NLightning.Domain.Signing.Recovery.IRemoteSigningWorkflowCoordinator>());
        Assert.Throws<NotSupportedException>(() => registeredKeys.GetNodeKeyPair());
        var signer = Assert.IsType<RemoteLightningSigner>(provider.GetRequiredService<ILightningSigner>());
        Assert.Equal(keys.GetNodePubKey(), signer.GetNodePublicKey());
    }

    [Fact]
    public async Task SignerRestartPreservesMonotonicSafetyStateAndClosedChannelTombstone()
    {
        await using var ownDaemon = new SignerDaemonFixture();
        await ownDaemon.InitializeAsync();
        var id = new ChannelId(RandomUtils.GetBytes(32));
        ChannelSigningInfo info;
        using (var connection = new RemoteSignerConnection(ownDaemon.Options()))
        {
            var signer = new RemoteLightningSigner(connection);
            var index = signer.CreateNewChannel(out var points, out _);
            info = new ChannelSigningInfo(new TxId(RandomUtils.GetBytes(32)), 0, 100_000,
                                          points.FundingPubKey, new Key().PubKey.ToBytes(), index);
            signer.RegisterChannel(id, info);
            signer.AdvanceLocalCommitment(id, 3);
            signer.MarkBroadcastSigned(id, 2);
            signer.MarkDataLoss(id);
        }
        await ownDaemon.RestartAsync();
        using (var connection = new RemoteSignerConnection(ownDaemon.Options()))
        {
            var signer = new RemoteLightningSigner(connection);
            signer.RegisterChannel(id, info);
            Assert.True(signer.TryGetBroadcastSignedCommitment(id, out var broadcastNumber));
            Assert.Equal(2UL, broadcastNumber);
            var unsignedTx = Network.RegTest.CreateTransaction();
            unsignedTx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])info.FundingTxId), 0)));
            unsignedTx.Outputs.Add(Money.Satoshis(99_000), new Key().PubKey.WitHash.ScriptPubKey);
            Assert.Throws<SignerException>(() => signer.SignChannelTransaction(id,
                new SignedTransaction(unsignedTx.GetHash().ToBytes(), unsignedTx.ToBytes())));
            Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(id, 2));
            signer.UnregisterChannel(id);
        }
        await ownDaemon.RestartAsync();
        using var lastConnection = new RemoteSignerConnection(ownDaemon.Options());
        var lastSigner = new RemoteLightningSigner(lastConnection);
        Assert.Throws<SignerException>(() => lastSigner.RegisterChannel(id, info));
    }

    [Fact]
    public async Task TaprootBroadcastNonceCannotSignAnotherTransactionThroughFundingAliasAfterRestart()
    {
        await using var ownDaemon = new SignerDaemonFixture();
        await ownDaemon.InitializeAsync();
        var id = new ChannelId(RandomUtils.GetBytes(32));
        var funding = new TxId(RandomUtils.GetBytes(32));
        MusigPublicNonce verification;
        MusigPartialSignatureWithNonce peerSignature;
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])funding), 0)));
        tx.Outputs.Add(Money.Satoshis(99_000), new Key().PubKey.WitHash.ScriptPubKey);
        var unsigned = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        byte[] broadcast;
        using (var connection = new RemoteSignerConnection(ownDaemon.Options()))
        {
            var signer = new RemoteLightningSigner(connection);
            var index = signer.CreateNewChannel(out var points, out _);
            var peerIndex = signer.CreateNewChannel(out var peerPoints, out _);
            signer.RegisterChannel(id, new ChannelSigningInfo(funding, 0, 100_000, points.FundingPubKey,
                                                             peerPoints.FundingPubKey, index)
            { IsSimpleTaproot = true, LocalCommitmentNumber = 1 });
            ownDaemon.LocalSigner.RegisterChannel(id,
                new ChannelSigningInfo(funding, 0, 100_000, peerPoints.FundingPubKey, points.FundingPubKey, peerIndex)
                { IsSimpleTaproot = true });
            verification = signer.GetLocalVerificationNonce(id, null, 1);
            peerSignature = ownDaemon.LocalSigner.SignRemoteCommitmentPartial(id, null, unsigned, verification);
            broadcast = signer.SignLocalCommitmentForBroadcast(id, null, 1, unsigned, peerSignature).RawTxBytes;
        }
        await ownDaemon.RestartAsync();
        using var after = new RemoteSignerConnection(ownDaemon.Options());
        var restarted = new RemoteLightningSigner(after);
        Assert.Equal(broadcast, restarted.SignLocalCommitmentForBroadcast(id, null, 1, unsigned, peerSignature).RawTxBytes);
        tx.Outputs[0].Value = Money.Satoshis(98_000);
        var changed = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        var changedPeer = ownDaemon.LocalSigner.SignRemoteCommitmentPartial(id, null, changed, verification);
        Assert.Throws<SignerException>(() => restarted.SignLocalCommitmentForBroadcast(id, funding, 1, changed, changedPeer));
    }

    [Fact]
    public async Task InjectedSeedKeepsIdentityAndAllocationsAcrossRestartWithoutASecretKeyFile()
    {
        await using var ownDaemon = new SignerDaemonFixture(injected: true);
        await ownDaemon.InitializeAsync();
        CompactPubKey identity;
        uint first;
        using (var connection = new RemoteSignerConnection(ownDaemon.Options()))
        {
            var signer = new RemoteLightningSigner(connection);
            identity = signer.GetNodePublicKey();
            Assert.Equal(ownDaemon.LocalKeys.GetNodePubKey(), identity);
            first = signer.CreateNewChannel(out _, out _);
        }
        await ownDaemon.RestartAsync();
        using var after = new RemoteSignerConnection(ownDaemon.Options());
        var restarted = new RemoteLightningSigner(after);
        Assert.Equal(identity, restarted.GetNodePublicKey());
        Assert.True(restarted.CreateNewChannel(out _, out _) > first);
        Assert.False(File.Exists(Path.Combine(ownDaemon.DirectoryPath, "node.key")));
        Assert.True(File.Exists(Path.Combine(ownDaemon.DirectoryPath, "state.key-index")));
    }

    [Fact]
    public async Task InjectingAnotherSeedIntoExistingStateFailsBeforeReadiness()
    {
        await using var ownDaemon = new SignerDaemonFixture(injected: true);
        await ownDaemon.InitializeAsync();
        ownDaemon.ReplaceInjectedSeed(new string('0', 63) + "2");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ownDaemon.RestartAsync());
        Assert.Contains("before readiness", error.Message);
    }

    [Fact]
    public async Task MissingInjectedSignerStateFailsClosedEvenBeforeAnyChannelAllocation()
    {
        await using var ownDaemon = new SignerDaemonFixture(injected: true);
        await ownDaemon.InitializeAsync();
        await ownDaemon.StopAsync();
        File.Delete(Path.Combine(ownDaemon.DirectoryPath, "state"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ownDaemon.RestartAsync());
        Assert.Contains("before readiness", error.Message);
    }

    [Fact]
    public async Task LiveTransportOutageIsNotReportedAsAnInvalidPeerSignature()
    {
        await using var ownDaemon = new SignerDaemonFixture();
        await ownDaemon.InitializeAsync();
        var options = ownDaemon.Options();
        using var connection = new RemoteSignerConnection(options);
        var signer = new RemoteLightningSigner(connection);
        Assert.Equal(ownDaemon.LocalKeys.GetNodePubKey(), signer.GetNodePublicKey());
        options.TimeoutSeconds = 1;
        await ownDaemon.StopAsync();
        Assert.Throws<RemoteSignerTransportException>(() => signer.GetNodePublicKey());
    }

    [Fact]
    public void MissingTransportFailsWithoutLocalFallbackOrPeerSignatureError()
    {
        var options = daemon.Options();
        options.SocketPath = Path.Combine(daemon.DirectoryPath, "missing.sock");
        var exception = Record.Exception(() => new RemoteSignerConnection(options));
        Assert.NotNull(exception);
        Assert.IsNotType<SignerException>(exception);
    }
}