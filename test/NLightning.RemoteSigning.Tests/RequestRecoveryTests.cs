using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Builders;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.Bitcoin.Services;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class RequestRecoveryTests
{
    [Fact]
    public async Task LostAllocationReplyIsReconciledAfterActualProcessCrashWithoutAllocatingTwice()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        var gate = new ReplyGate();
        using var connection = new RemoteSignerConnection(daemon.Options(), async cancellation =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(daemon.SocketPath), cancellation);
            return new GatedReadStream(new NetworkStream(socket, ownsSocket: true), gate);
        });
        var request = RemoteSignerConnection.Prepare(SignerOperations.ReserveChannelKeyIndex);
        gate.Arm();
        var dispatch = Task.Run(() => connection.Execute(request));
        // Block delivery on the client stream. Another authenticated connection confirms completion
        // after the fsync, while the original call still cannot observe its response.
        await gate.ResponseBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        byte[] original;
        using (var observer = new RemoteSignerConnection(daemon.Options()))
        {
            var receipt = observer.Reconcile(request);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (receipt.Outcome == RequestOutcome.NotFound && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
                receipt = observer.Reconcile(request);
            }
            Assert.Equal(RequestOutcome.Completed, receipt.Outcome);
            original = receipt.Response.Payload.ToByteArray();
        }
        Assert.False(dispatch.IsCompleted);
        await daemon.StopAsync();
        gate.Release.TrySetResult();
        var failure = await Assert.ThrowsAsync<RemoteSignerTransportException>(async () => await dispatch);
        Assert.Equal(request, failure.Request);
        // Diagnostics expose a snapshot, not a mutable alias that can change what gets reconciled.
        failure.Request!.Operation = SignerOperations.Identity;
        Assert.Equal(SignerOperations.ReserveChannelKeyIndex, failure.Request!.Operation);

        await daemon.RestartAsync();
        using var recovered = new RemoteSignerConnection(daemon.Options());
        var result = recovered.Reconcile(request);
        Assert.Equal(RequestOutcome.Completed, result.Outcome);
        Assert.Equal(original, result.Response.Payload.ToByteArray());
        Assert.Equal(original, SignerWire.Encode(recovered.Execute(request).Cast<object?>().ToArray()));
        var index = SignerWire.Read<uint>(SignerWire.Decode(original)[0]);
        Assert.Equal(index + 1, SignerWire.Read<uint>(recovered.Invoke(SignerOperations.ReserveChannelKeyIndex)[0]));
        var conflict = request.Clone();
        conflict.Operation = SignerOperations.CreateNewChannel;
        Assert.Throws<ArgumentException>(() => recovered.Reconcile(conflict));
        Assert.Throws<ArgumentException>(() => recovered.Execute(conflict));
        conflict = request.Clone();
        conflict.Payload = ByteString.CopyFromUtf8("[1]");
        Assert.Throws<ArgumentException>(() => recovered.Reconcile(conflict));
        Assert.Throws<ArgumentException>(() => recovered.Execute(conflict));
    }

    [Fact]
    public async Task EquivalentBase64ChannelEncodingCannotReuseBroadcastNonceForAnotherTransactionAfterRestart()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        var channel = new ChannelId(Enumerable.Repeat((byte)1, 32).ToArray());
        var funding = new TxId(RandomUtils.GetBytes(32));
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])funding), 0)));
        transaction.Outputs.Add(Money.Satoshis(99_000), new Key().PubKey.WitHash.ScriptPubKey);
        var unsigned = new SignedTransaction(transaction.GetHash().ToBytes(), transaction.ToBytes());
        MusigPublicNonce verification;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            var signer = new RemoteLightningSigner(connection);
            var index = signer.CreateNewChannel(out var points, out _);
            var peerIndex = signer.CreateNewChannel(out var peerPoints, out _);
            signer.RegisterChannel(channel, new ChannelSigningInfo(funding, 0, 100_000, points.FundingPubKey,
                peerPoints.FundingPubKey, index)
            { IsSimpleTaproot = true, LocalCommitmentNumber = 1 });
            daemon.LocalSigner.RegisterChannel(channel, new ChannelSigningInfo(funding, 0, 100_000,
                peerPoints.FundingPubKey, points.FundingPubKey, peerIndex)
            { IsSimpleTaproot = true });
            verification = signer.GetLocalVerificationNonce(channel, null, 1);
            var peer = daemon.LocalSigner.SignRemoteCommitmentPartial(channel, null, unsigned, verification);
            signer.SignLocalCommitmentForBroadcast(channel, null, 1, unsigned, peer);
        }
        await daemon.RestartAsync();
        using var restarted = new RemoteSignerConnection(daemon.Options());
        transaction.Outputs[0].Value = Money.Satoshis(98_000);
        var changed = new SignedTransaction(transaction.GetHash().ToBytes(), transaction.ToBytes());
        var changedPeer = daemon.LocalSigner.SignRemoteCommitmentPartial(channel, null, changed, verification);
        var request = RemoteSignerConnection.Prepare(SignerOperations.SignLocalCommitmentForBroadcast2,
            channel, null, 1UL, changed, changedPeer);
        var args = SignerWire.Decode(request.Payload.ToByteArray());
        var canonical = args[0].GetString()!;
        // .NET accepts embedded whitespace in base64, but serializing the raw JsonElement
        // preserves it. Session identity must bind decoded bytes instead of that spelling.
        var alternate = canonical.Insert(10, " \n");
        Assert.NotEqual(canonical, alternate);
        args[0] = JsonSerializer.SerializeToElement(alternate);
        Assert.Equal(channel, SignerWire.Read<ChannelId>(args[0]));
        request.Payload = ByteString.CopyFrom(SignerWire.Encode(args.Cast<object?>().ToArray()));
        Assert.Throws<SignerException>(() => restarted.Execute(request));
        await daemon.StopAsync();
        // Simulate an older journal that admitted this alternate spelling as another session.
        // Typed session reconstruction must reject the collapsed conflicting history at startup.
        var statePath = Path.Combine(daemon.DirectoryPath, "state");
        var journal = File.ReadAllBytes(statePath);
        var position = 8 + 33 + 32;
        JsonObject? original = null;
        while (position < journal.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(position, 4));
            position += 4;
            var entry = JsonNode.Parse(journal.AsSpan(position, size))!.AsObject();
            position += size;
            if (entry["Operation"]!.GetValue<uint>() == SignerOperations.SignLocalCommitmentForBroadcast2)
                original = entry;
        }
        Assert.NotNull(original);
        original["Payload"] = Convert.ToBase64String(request.Payload.ToByteArray());
        original.Remove("RequestId");
        original.Remove("Fingerprint");
        var conflicting = JsonSerializer.SerializeToUtf8Bytes(original);
        using (var file = new FileStream(statePath, FileMode.Append, FileAccess.Write))
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, conflicting.Length);
            file.Write(header);
            file.Write(conflicting);
            file.Flush(flushToDisk: true);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.RestartAsync());
    }

    [Fact]
    public async Task ConsumedClosingNonceReplyIsRecoveredExactlyAndRetirementInvalidatesIt()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        var channel = new ChannelId(RandomUtils.GetBytes(32));
        var funding = new TxId(RandomUtils.GetBytes(32));
        SigningRequest request;
        byte[] reply;
        MusigPartialSignature partial;
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            var signer = new RemoteLightningSigner(connection);
            var index = signer.CreateNewChannel(out var points, out _);
            var peerIndex = signer.CreateNewChannel(out var peerPoints, out _);
            signer.RegisterChannel(channel, new ChannelSigningInfo(funding, 0, 100_000, points.FundingPubKey,
                peerPoints.FundingPubKey, index)
            { IsSimpleTaproot = true });
            daemon.LocalSigner.RegisterChannel(channel, new ChannelSigningInfo(funding, 0, 100_000,
                peerPoints.FundingPubKey, points.FundingPubKey, peerIndex)
            { IsSimpleTaproot = true });
            var nonce = signer.CreateClosingNonce(channel);
            var transaction = Network.RegTest.CreateTransaction();
            transaction.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])funding), 0)));
            transaction.Outputs.Add(Money.Satoshis(99_000), new Key().PubKey.WitHash.ScriptPubKey);
            var unsigned = new SignedTransaction(transaction.GetHash().ToBytes(), transaction.ToBytes());
            var closer = daemon.LocalSigner.SignClosingAsCloser(channel, unsigned, nonce);
            request = RemoteSignerConnection.Prepare(SignerOperations.SignClosingAsClosee, channel,
                unsigned, nonce, closer);
            var response = connection.Execute(request);
            partial = SignerWire.Read<MusigPartialSignature>(response[0]);
            daemon.LocalSigner.ValidateClosingPartialSignature(channel, unsigned,
                partial, nonce, closer.PublicNonce);
            reply = connection.Reconcile(request).Response.Payload.ToByteArray();
            Assert.Equal(partial, SignerWire.Read<MusigPartialSignature>(connection.Execute(request)[0]));
        }
        await daemon.RestartAsync();
        using (var recovered = new RemoteSignerConnection(daemon.Options()))
        {
            Assert.Equal(reply, recovered.Reconcile(request).Response.Payload.ToByteArray());
            Assert.Equal(partial, SignerWire.Read<MusigPartialSignature>(recovered.Execute(request)[0]));
            using var transport = RawChannel(daemon.SocketPath);
            var client = new SignerRpc.SignerRpcClient(transport);
            var metadata = new Metadata { { "x-signer-token", SignerDaemonFixture.Token } };
            var raw = await client.ExecuteAsync(request, metadata,
                cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
            Assert.Equal(reply, raw.Payload.ToByteArray());
            var freshId = request.Clone();
            freshId.RequestId = Guid.NewGuid().ToString("N");
            // A newly recorded receipt for an existing consumed nonce must also preserve its exact response bytes.
            raw = await client.ExecuteAsync(freshId, metadata,
                cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
            Assert.Equal(reply, raw.Payload.ToByteArray());
            Assert.Equal(reply, recovered.Reconcile(freshId).Response.Payload.ToByteArray());
            recovered.Invoke(SignerOperations.UnregisterChannel, channel);
            Assert.Equal(RequestOutcome.Invalidated, recovered.Reconcile(request).Outcome);
            Assert.Throws<SignerException>(() => recovered.Execute(request));
        }
        await daemon.RestartAsync();
        using var retired = new RemoteSignerConnection(daemon.Options());
        Assert.Equal(RequestOutcome.Invalidated, retired.Reconcile(request).Outcome);
        Assert.Throws<SignerException>(() => retired.Execute(request));
    }

    [Fact]
    public async Task DataLossInvalidatesCompletedReceiptsAcrossRestartAndCannotReplayThem()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        var channel = new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray());
        var mark = RemoteSignerConnection.Prepare(SignerOperations.MarkBroadcastSigned, channel, 7UL);
        using (var connection = new RemoteSignerConnection(daemon.Options()))
        {
            connection.Execute(mark);
            Assert.Equal(RequestOutcome.Completed, connection.Reconcile(mark).Outcome);
            connection.Invoke(SignerOperations.MarkDataLoss, channel);
            Assert.Equal(RequestOutcome.Invalidated, connection.Reconcile(mark).Outcome);
            Assert.Throws<SignerException>(() => connection.Execute(mark));
        }
        await daemon.RestartAsync();
        using var restored = new RemoteSignerConnection(daemon.Options());
        var receipt = restored.Reconcile(mark);
        Assert.Equal(RequestOutcome.Invalidated, receipt.Outcome);
        Assert.Null(receipt.Response);
        Assert.Throws<SignerException>(() => restored.Execute(mark));
    }

    [Fact]
    public void InterruptedAllocationIsUnknownAndNeverReexecutedWithSameIdAfterRestart()
    {
        using var fixture = new StateFixture();
        var request = RemoteSignerConnection.Prepare(SignerOperations.ReserveChannelKeyIndex);
        using (var state = fixture.Open())
        {
            Assert.Throws<IOException>(() => state.ExecuteRequest(request, [], () =>
            {
                fixture.Keys.ReserveChannelKeyIndex();
                throw new IOException("Simulated interruption after external index persistence.");
            }));
            Assert.Equal(RequestOutcome.Unknown, state.Reconcile(request).Outcome);
        }
        using var restored = fixture.Open();
        var invoked = false;
        Assert.Equal(RequestOutcome.Unknown, restored.Reconcile(request).Outcome);
        Assert.Throws<SignerException>(() => restored.ExecuteRequest(request, [], () =>
        {
            invoked = true;
            return [fixture.Keys.ReserveChannelKeyIndex()];
        }));
        Assert.False(invoked);
        var fresh = RemoteSignerConnection.Prepare(SignerOperations.ReserveChannelKeyIndex);
        Assert.Equal(2U, restored.ExecuteRequest(fresh, [], () => [fixture.Keys.ReserveChannelKeyIndex()])[0]);
    }

    [Fact]
    public async Task ReconciliationIsAuthenticatedAndDoesNotExecuteMissingRequests()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        var options = daemon.Options();
        using var connection = new RemoteSignerConnection(options);
        var request = RemoteSignerConnection.Prepare(SignerOperations.ReserveChannelKeyIndex);
        Assert.Equal(RequestOutcome.NotFound, connection.Reconcile(request).Outcome);
        Assert.Equal(1U, SignerWire.Read<uint>(connection.Execute(request)[0]));
        Assert.Equal(1U, SignerWire.Read<uint>(connection.Execute(request)[0]));
        Assert.Equal(RequestOutcome.Unsupported,
                     connection.Reconcile(RemoteSignerConnection.Prepare(SignerOperations.EncryptNodeData)).Outcome);
        // Change only the client credentials AFTER a valid handshake to exercise the Reconcile RPC itself.
        options.AuthToken = new string('x', 40);
        Assert.Throws<RemoteSignerTransportException>(() => connection.Reconcile(request));
    }

    private static GrpcChannel RawChannel(string path)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class ReplyGate
    {
        private int _armed;
        public TaskCompletionSource ResponseBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Armed => Volatile.Read(ref _armed) != 0;
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
    }

    private sealed class GatedReadStream(Stream inner, ReplyGate gate) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var size = await inner.ReadAsync(buffer, cancellationToken);
            if (size > 0 && gate.Armed)
            {
                gate.ResponseBlocked.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
                throw new IOException("Response withheld until signer was killed.");
            }
            return size;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class StateFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rs-receipt-" + Guid.NewGuid().ToString("N"));
        public SecureKeyManager Keys { get; }
        private readonly LocalLightningSigner _signer;
        public StateFixture()
        {
            Directory.CreateDirectory(_directory);
            Keys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)1, 32).ToArray(), NetworkConstants.Regtest, _ => { });
            _signer = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(),
                NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest },
                Keys, new UtxoMemoryRepository());
        }
        public DurableSignerState Open() => new(_signer, Path.Combine(_directory, "state"), "regtest");
        public void Dispose() { Keys.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}