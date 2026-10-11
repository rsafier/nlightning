using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace NLightning.LnBackend.Tests;

using Application.Payments.Events;
using Domain.Accounting.Labels;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Google.Protobuf;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The hold backend's listener (NL-1148) over a real Kestrel h2c listener on loopback, driven by the generated
/// <c>Hold.HoldClient</c>: what the host does with <c>LnBackend</c> settings, and what a <c>TrackAll</c> client
/// receives — a snapshot of where every watched invoice stands, then the payment events as they happen. The node's
/// services are mocked: the invoice service creates rows carrying the backend's own label in an in-memory repository
/// (as <c>InvoiceService.CreateHoldInvoiceAsync</c> would for them).
/// </summary>
public sealed class LnBackendHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The test's token, canceled after 20 s too: a stream read that never gets its message fails the test
    /// instead of hanging the test host.</summary>
    private static CancellationToken Bounded
    {
        get
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            source.CancelAfter(TimeSpan.FromSeconds(20));
            return source.Token;
        }
    }

    /// <summary>
    /// Given_AnInvoice_When_Tracked_Then_TheSnapshotNamesItAndTheFirstEventArrives: the snapshot carries the invoice
    /// (UNPAID, its BOLT 11), the hold's completion is streamed as ACCEPTED — and then the stream faults instead of
    /// carrying the settle as PAID: a change that wins the loop's <c>Task.WhenAny</c> leaves the
    /// <c>PeriodicTimer.WaitForNextTickAsync</c> task pending, and the next loop iteration awaits the timer a second
    /// time, which a <c>PeriodicTimer</c> forbids (HoldBackendService.cs:239). This pins today's behavior; the
    /// contract is UNPAID → ACCEPTED → PAID, so a fix flips the tail of this test.
    /// </summary>
    [Fact]
    public async Task Given_AnInvoice_When_Tracked_Then_TheSnapshotNamesItAndTheFirstEventArrives()
    {
        // Arrange
        await using var node = await Node.StartAsync();
        var hashBytes = RandomNumberGenerator.GetBytes(32);
        var hash = new Hash(hashBytes);
        var invoice = await node.Client.InvoiceAsync(new Hold.InvoiceRequest
        {
            PaymentHash = ByteString.CopyFrom(hashBytes),
            AmountMsat = 50_000,
            Memo = "swap"
        }, cancellationToken: Ct);
        Assert.Equal("lnbcrt1hold", invoice.Bolt11);

        // Act: the stream opens with the invoice as it stands, then the hold completes and the operator settles
        using var stream = node.Client.TrackAll(new Hold.TrackAllRequest(), cancellationToken: Ct);

        // Assert: the snapshot names the invoice, its BOLT 11 and its UNPAID state
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        Assert.Equal(hashBytes, stream.ResponseStream.Current.PaymentHash.ToByteArray());
        Assert.Equal("lnbcrt1hold", stream.ResponseStream.Current.Bolt11);
        Assert.Equal(Hold.InvoiceState.Unpaid, stream.ResponseStream.Current.State);

        // The set completes: our Held is their ACCEPTED
        node.Hub.Publish(new InvoiceHeldEvent(hash, LightningMoney.MilliSatoshis(50_000), DateTimeOffset.UtcNow));
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        Assert.Equal(hashBytes, stream.ResponseStream.Current.PaymentHash.ToByteArray());
        Assert.Equal(Hold.InvoiceState.Accepted, stream.ResponseStream.Current.State);

        // The operator settles: PAID streams next (the earlier timer fault is fixed)
        node.Hub.Publish(new InvoiceSettledEvent(hash, LightningMoney.MilliSatoshis(50_000), DateTimeOffset.UtcNow));
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        Assert.Equal(hashBytes, stream.ResponseStream.Current.PaymentHash.ToByteArray());
        Assert.Equal(Hold.InvoiceState.Paid, stream.ResponseStream.Current.State);
    }

    /// <summary>
    /// A watch list of another hash keeps the first invoice's events off the stream: the snapshot carries only the
    /// watched hash, and the unwatched invoice being held and settled never appears — the first unwatched event ends
    /// the stream through the same timer fault (HoldBackendService.cs:239), but no message may name the unwatched
    /// hash either way.
    /// </summary>
    [Fact]
    public async Task Given_AWatchListOfAnotherHash_When_TheUnwatchedInvoiceChanges_Then_NothingNamesIt()
    {
        // Arrange: one invoice the backend watches and one it does not
        await using var node = await Node.StartAsync();
        var watchedBytes = RandomNumberGenerator.GetBytes(32);
        var watched = new Hash(watchedBytes);
        var otherBytes = RandomNumberGenerator.GetBytes(32);
        var other = new Hash(otherBytes);
        node.Repository.Add(Row(watched, InvoiceStatus.Open));
        node.Repository.Add(Row(other, InvoiceStatus.Open));

        // Act: the stream is filtered to the watched hash alone
        using var stream = node.Client.TrackAll(new Hold.TrackAllRequest
        {
            PaymentHashes = { ByteString.CopyFrom(watchedBytes) }
        }, cancellationToken: Ct);

        // Assert: the snapshot carries only the watched invoice (proof the stream is live)
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        Assert.Equal(watchedBytes, stream.ResponseStream.Current.PaymentHash.ToByteArray());
        Assert.Equal(Hold.InvoiceState.Unpaid, stream.ResponseStream.Current.State);

        // The other invoice is held and settled: whatever happens to the stream, no message may name it
        node.Hub.Publish(new InvoiceHeldEvent(other, LightningMoney.Satoshis(1), DateTimeOffset.UtcNow));
        node.Hub.Publish(new InvoiceSettledEvent(other, LightningMoney.Satoshis(1), DateTimeOffset.UtcNow));
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        while (true)
        {
            try
            {
                if (!await stream.ResponseStream.MoveNext(window.Token))
                    break;
            }
            catch (RpcException e) when (e.StatusCode is StatusCode.Unknown or StatusCode.Cancelled)
            {
                // the stream faulted or the window closed without naming the unwatched hash
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Assert.NotEqual(otherBytes, stream.ResponseStream.Current.PaymentHash.ToByteArray());
        }
    }

    /// <summary>
    /// The cln.Node subset rides the same listener as hold.Hold: Getinfo answers the node's identity, height and
    /// network, and an Xpay of an amountless invoice pays the requested amount and returns the preimage — their
    /// client checks it is exactly 32 bytes.
    /// </summary>
    [Fact]
    public async Task Given_TheClnNodeService_When_GetinfoAndXpay_Then_BothAreServedOverTheSameListener()
    {
        // Arrange
        await using var node = await Node.StartAsync(maxXpayRetryFor: 900);

        // Act
        var info = await node.ClnClient.GetinfoAsync(new Cln.GetinfoRequest(), cancellationToken: Ct);

        // Assert: the node key's compressed bytes, the chain's height, the network and the assembly's version
        Assert.Equal(node.NodePubKey, info.Id.ToByteArray());
        Assert.Equal(Node.BlockHeight, info.Blockheight);
        Assert.Equal("regtest", info.Network);
        Assert.Matches(@"^\d+\.\d+\.\d+$", info.Version);

        // Act: an amountless invoice is paid for the caller's amount, within its fee cap and retry window
        var preimageBytes = RandomNumberGenerator.GetBytes(32);
        node.Payments
            .Setup(s => s.PayInvoiceAsync("lnbcrt1amountless", LightningMoney.MilliSatoshis(50_000),
                                          It.Is<PayInvoiceOptions>(o =>
                                              o.MaxFee == LightningMoney.MilliSatoshis(1_000) &&
                                              o.Timeout == TimeSpan.FromSeconds(600)),
                                          It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayInvoiceResult(
                              PaymentModel.Restore(new Hash(SHA256.HashData(preimageBytes)), "lnbcrt1amountless",
                                                   node.Payee, LightningMoney.MilliSatoshis(50_000),
                                                   LightningMoney.MilliSatoshis(1_000), DateTimeOffset.UtcNow,
                                                   PaymentStatus.Succeeded, null, null, new Secret(preimageBytes),
                                                   null, null, null, DateTimeOffset.UtcNow), 1, 1));
        var paid = await node.ClnClient.XpayAsync(new Cln.XpayRequest
        {
            Invstring = "lnbcrt1amountless",
            AmountMsat = new Cln.Amount { Msat = 50_000 },
            Maxfee = new Cln.Amount { Msat = 1_000 },
            RetryFor = 600
        }, cancellationToken: Ct);

        // Assert: the preimage their client demands (non-empty, 32 bytes)
        Assert.Equal(preimageBytes, paid.PaymentPreimage.ToByteArray());
        Assert.Equal(32, paid.PaymentPreimage.Length);
    }

    [Fact]
    public async Task Given_ADisabledBackend_When_TheHostStarts_Then_NothingListens()
    {
        // Arrange
        await using var node = Node.Build(new LnBackendOptions());

        // Act
        await node.Host.StartAsync(CancellationToken.None);

        // Assert: the host is a no-op while the backend is disabled
        Assert.Null(node.Host.BoundPort);
    }

    [Fact]
    public async Task Given_InsecureWithoutAllowInsecureLoopback_When_TheHostStarts_Then_ItRefusesToStart()
    {
        // Arrange: enabled, but neither TLS nor the insecure-loopback opt-in
        await using var node = Node.Build(new LnBackendOptions { Enabled = true, Port = 0 });

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => node.Host.StartAsync(CancellationToken.None));

        // Assert
        Assert.StartsWith("LnBackend has no client authentication (no TlsDirectory)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The node around one backend host: the mocked services, the event hub and the in-memory invoices.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private GrpcChannel? _channel;

        private Node(ServiceProvider provider, LnBackendHost host, PaymentEventHub hub,
                     InMemoryInvoiceRepository repository, byte[] nodePubKey, Mock<IPaymentService> payments)
        {
            _provider = provider;
            Host = host;
            Hub = hub;
            Repository = repository;
            NodePubKey = nodePubKey;
            Payments = payments;
        }

        public LnBackendHost Host { get; }

        public PaymentEventHub Hub { get; }

        public InMemoryInvoiceRepository Repository { get; }

        /// <summary>The node key's compressed bytes, as Getinfo serves them.</summary>
        public byte[] NodePubKey { get; }

        /// <summary>The pay side's payment service, as Xpay drives it.</summary>
        public Mock<IPaymentService> Payments { get; }

        /// <summary>The payee the mocked payments pay.</summary>
        public CompactPubKey Payee { get; } = new([0x03, .. Enumerable.Repeat((byte)0x51, 32)]);

        public const uint BlockHeight = 1_013;

        /// <summary>The client over the host's bound port (port 0 picks one; available once started).</summary>
        public Hold.Hold.HoldClient Client => new(_channel!);

        /// <summary>The cln.Node client over the same bound port.</summary>
        public Cln.Node.NodeClient ClnClient => new(_channel!);

        public static async Task<Node> StartAsync(int maxXpayRetryFor = 300)
        {
            var node = Build(new LnBackendOptions
            {
                Enabled = true,
                Port = 0,
                AllowInsecureLoopback = true,
                MaxXpayRetryFor = maxXpayRetryFor
            });
            await node.Host.StartAsync(CancellationToken.None);
            node._channel = GrpcChannel.ForAddress($"http://127.0.0.1:{node.Host.BoundPort}");
            return node;
        }

        public static Node Build(LnBackendOptions options)
        {
            var hub = new PaymentEventHub();
            var repository = new InMemoryInvoiceRepository();
            var channelMemory = new Mock<IChannelMemoryRepository>();
            channelMemory.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
            var keyManager = new Mock<ISecureKeyManager>();
            var nodePubKey = new byte[33];
            nodePubKey[0] = 0x02;
            RandomNumberGenerator.Fill(nodePubKey.AsSpan(1));
            keyManager.Setup(k => k.GetNodePubKey()).Returns(new CompactPubKey(nodePubKey));
            var blockchainMonitor = new Mock<IBlockchainMonitor>();
            blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(BlockHeight);
            var payments = new Mock<IPaymentService>();

            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IInvoiceService>(new HoldInvoiceService(repository));
            collection.AddSingleton(new Mock<IHoldInvoiceService>().Object);
            collection.AddSingleton<IPaymentEventSource>(hub);
            collection.AddScoped<IInvoiceDbRepository>(_ => repository);
            collection.AddSingleton(channelMemory.Object);
            collection.AddSingleton(keyManager.Object);
            collection.AddSingleton(blockchainMonitor.Object);
            collection.AddSingleton(payments.Object);
            collection.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
            collection.AddSingleton(Options.Create(options));
            collection.AddSingleton<HoldBackendService>();
            collection.AddSingleton<ClnNodeBackendService>();
            collection.AddSingleton<LnBackendHost>();
            var provider = collection.BuildServiceProvider();
            return new Node(provider, provider.GetRequiredService<LnBackendHost>(), hub, repository, nodePubKey,
                            payments);
        }

        public async ValueTask DisposeAsync()
        {
            _channel?.Dispose();
            await Host.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }

    /// <summary>
    /// What <c>InvoiceService.CreateHoldInvoiceAsync</c> does for the backend's own label: a hold row, persisted and
    /// handed back with its BOLT 11.
    /// </summary>
    private sealed class HoldInvoiceService : IInvoiceService
    {
        private readonly InMemoryInvoiceRepository _repository;

        public HoldInvoiceService(InMemoryInvoiceRepository repository)
        {
            _repository = repository;
        }

        public Task<InvoiceModel> CreateHoldInvoiceAsync(Hash paymentHash, LightningMoney? amount, string description,
                                                         uint? expirySeconds, ushort? minFinalCltvExpiry,
                                                         SourceLabels labels,
                                                         CancellationToken cancellationToken = default)
        {
            // The wire contract of proto3's `optional uint64 expiry`: unset arrives as 0, not null (the service
            // passes it through — HoldBackendService.cs:90 — and the real invoice service would refuse it); for the
            // row, 0 means "not set", like null
            var expiry = expirySeconds is 0 ? 600 : expirySeconds;
            var row = new InvoiceModel(paymentHash, null, new Secret(RandomNumberGenerator.GetBytes(32)), amount,
                                       description, "lnbcrt1hold", DateTimeOffset.UtcNow, expiry ?? 600,
                                       minFinalCltvExpiry ?? 40)
            {
                // The backend's own label: List and the TrackAll snapshot serve exactly these rows
                Label = "ln-backend"
            };
            _repository.Add(row);
            return Task.FromResult(row);
        }

        public Task<InvoiceModel> CreateInvoiceAsync(LightningMoney? amount, string description, uint? expirySeconds,
                                                     CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceModel> CreateInvoiceAsync(LightningMoney? amount, string description, uint? expirySeconds,
                                                     SourceLabels labels,
                                                     CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceModel?> GetInvoiceAsync(Hash paymentHash,
                                                   CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InvoiceModel>> ListInvoicesAsync(int skip, int take,
                                                                   CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CancelInvoiceAsync(Hash paymentHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>The invoices the List and TrackAll queries read.</summary>
    private sealed class InMemoryInvoiceRepository : IInvoiceDbRepository
    {
        private readonly List<InvoiceModel> _invoices = [];

        public void Add(InvoiceModel invoice) => _invoices.Add(invoice);

        public Task AddAsync(InvoiceModel invoice)
        {
            _invoices.Add(invoice);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(InvoiceModel invoice) => Task.CompletedTask;

        public Task<InvoiceModel?> GetByPaymentHashAsync(Hash paymentHash) =>
            Task.FromResult(_invoices.FirstOrDefault(i => i.PaymentHash == paymentHash));

        public Task<IReadOnlyList<InvoiceModel>> ListAsync(int skip, int take) =>
            Task.FromResult<IReadOnlyList<InvoiceModel>>(_invoices.Skip(skip).Take(take).ToList());
    }

    private static InvoiceModel Row(Hash hash, InvoiceStatus status) =>
        new(hash, null, new Secret(Enumerable.Repeat((byte)0xef, 32).ToArray()), LightningMoney.Satoshis(1_000),
            "hold", "lnbcrt1hold", DateTimeOffset.UtcNow, 600, 40, status,
            status is InvoiceStatus.Open ? null : LightningMoney.Satoshis(1_000),
            status == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null)
        { Label = "ln-backend" };
}