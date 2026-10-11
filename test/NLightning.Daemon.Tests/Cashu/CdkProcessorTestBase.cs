using System.Text;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Daemon.Tests.Cashu;

using Application.Payments.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Interfaces;
using Domain.Onchain.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using NLightning.Cashu.PaymentProcessor;
using NLightning.Cashu.PaymentProcessor.Grpc;

/// <summary>
/// The CDK payment processor (Cashu plan C1, NL-992; NL-997) over a real Kestrel gRPC server on loopback, called with
/// the client generated from CDK's own proto, with the node's services mocked and the quotes in memory
/// (<see cref="InMemoryCashuQuoteStore"/>). BOLT 12 and on-chain are served (offers available, on-chain enabled with 2
/// confirmations) unless a test builds its own instance.
/// </summary>
public abstract class CdkProcessorTestBase : IAsyncLifetime
{
    protected const string MintLabel = CashuPaymentProcessorOptions.DefaultLabel;

    protected static readonly Secret Preimage = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());
    protected static readonly CompactPubKey Payee = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);

    protected readonly Mock<IBlockchainMonitor> BlockchainMonitor = new();
    protected readonly Mock<IBroadcastTransactionDbRepository> Broadcasts = new();
    protected readonly Mock<IFeeService> FeeService = new();
    protected readonly PaymentEventHub Hub = new();
    protected readonly Mock<IInvoiceDbRepository> InvoiceRepository = new();
    protected readonly Mock<IInvoiceService> InvoiceService = new();
    protected readonly Mock<IOfferPaymentService> OfferPaymentService = new();
    protected readonly Mock<IOfferService> OfferService = new();
    protected readonly Mock<IPaymentService> PaymentService = new();
    protected readonly InMemoryCashuQuoteStore Quotes = new();
    protected readonly Mock<IUtxoDbRepository> Utxos = new();
    protected readonly Mock<IBitcoinWalletService> Wallet = new();
    protected readonly Mock<IWalletSpendService> WalletSpend = new();

    private GrpcChannel? _channel;
    private CashuPaymentProcessorHost? _host;
    private ServiceProvider? _provider;

    /// <summary>The chain monitor's tip.</summary>
    protected uint Tip { get; set; } = 100;

    protected CdkPaymentProcessor.CdkPaymentProcessorClient Client => new(_channel!);

    protected CdkPaymentProcessorService Service => _provider!.GetRequiredService<CdkPaymentProcessorService>();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The test's token, canceled after 20 s too: a stream read that never gets its message fails the test
    /// instead of hanging the test host.</summary>
    protected static CancellationToken Bounded
    {
        get
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            source.CancelAfter(TimeSpan.FromSeconds(20));
            return source.Token;
        }
    }

    public async ValueTask InitializeAsync()
    {
        OfferService.SetupGet(s => s.IsAvailable).Returns(true);
        OfferPaymentService.SetupGet(s => s.IsAvailable).Returns(true);
        BlockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => Tip);
        Utxos.Setup(u => u.GetUnspentAsync(true)).ReturnsAsync([]);
        FeeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((uint target, CancellationToken _) => LightningMoney.Satoshis(10_000 / target));

        _provider = BuildProvider(new CashuPaymentProcessorOptions
        {
            Enabled = true,
            Port = 0,
            AllowInsecureLoopback = true,
            OnchainEnabled = true,
            OnchainConfirmations = 2
        });
        _host = _provider.GetRequiredService<CashuPaymentProcessorHost>();
        await _host.StartAsync(Ct);
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_host.BoundPort}");
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        if (_host is not null)
            await _host.StopAsync(CancellationToken.None);
        if (_provider is not null)
            await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>The node's services with the processor and its host (<paramref name="services"/> false: none of the
    /// offer, wallet and chain services, as on a node without them).</summary>
    protected ServiceProvider BuildProvider(CashuPaymentProcessorOptions options, bool services = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.CashuQuoteDbRepository).Returns(Quotes);
        unitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(InvoiceRepository.Object);
        unitOfWork.SetupGet(u => u.UtxoDbRepository).Returns(Utxos.Object);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(Broadcasts.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(InvoiceService.Object);
        collection.AddSingleton(PaymentService.Object);
        collection.AddSingleton<IPaymentEventSource>(Hub);
        collection.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
        collection.AddSingleton(Options.Create(options));
        collection.AddScoped(_ => unitOfWork.Object);
        if (services)
        {
            collection.AddSingleton(OfferService.Object);
            collection.AddSingleton(OfferPaymentService.Object);
            collection.AddSingleton(WalletSpend.Object);
            collection.AddSingleton(FeeService.Object);
            collection.AddSingleton(BlockchainMonitor.Object);
            collection.AddScoped(_ => Wallet.Object);
        }

        collection.AddSingleton<CdkPaymentProcessorService>();
        collection.AddSingleton<CashuPaymentProcessorHost>();
        return collection.BuildServiceProvider();
    }

    /// <summary>A processor as a node restarted over the same quotes would build it (not served over gRPC).</summary>
    protected CdkPaymentProcessorService NewServiceInstance() =>
        new(InvoiceService.Object, PaymentService.Object, Hub,
            Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            Options.Create(new CashuPaymentProcessorOptions { Enabled = true, OnchainEnabled = true }),
            NullLogger<CdkPaymentProcessorService>.Instance, null, _provider!.GetRequiredService<IServiceScopeFactory>(),
            OfferService.Object, OfferPaymentService.Object, WalletSpend.Object, FeeService.Object,
            BlockchainMonitor.Object);

    /// <summary>Waits until a <c>WaitPaymentEvent</c> stream is open.</summary>
    protected async Task WaitForStreamAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Service.StreamCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);
        Assert.Equal(1, Service.StreamCount);
    }

    protected static Hash Hash(byte b) => new(Enumerable.Repeat(b, 32).ToArray());

    /// <summary>A regtest offer string (issuer id set), with an amount in msat or none.</summary>
    protected static string RegtestOffer(ulong? amountMsat, string description = "coffee")
    {
        var records = new List<Bolt12TlvRecord>
        {
            new(Bolt12TlvTypes.OfferChains, (byte[])BitcoinNetwork.Regtest.ChainHash),
            new(Bolt12TlvTypes.OfferDescription, Encoding.UTF8.GetBytes(description)),
            new(Bolt12TlvTypes.OfferIssuerId, new Key().PubKey.ToBytes())
        };
        if (amountMsat is { } amount)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferAmount, TruncatedInt.EncodeTu64(amount)));
        var stream = new Bolt12TlvStream(records.OrderBy(r => r.Type).ToList());
        return Bolt12Bech32.Encode(Bolt12Constants.OfferHrp, stream.Encode());
    }
}