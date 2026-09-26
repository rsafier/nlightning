using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Channels.Interfaces;
using Application.Payments;
using Application.Payments.Routing;
using Application.Payments.Send;
using Application.Payments.Send.Interfaces;
using Bolt11.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Protocol.Onion;
using Infrastructure.Serialization;

/// <summary>
/// <see cref="PaymentService"/> with mocked channels and persistence: invoice validation, refusals, no-route
/// failures, and the outcome hook's matching and interpretation rules. The end-to-end paths are in
/// <see cref="PaymentHarnessTests"/>.
/// </summary>
public class PaymentServiceTests : IDisposable
{
    private const uint Height = 500;
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private readonly TestNodeKeyManager _us = new(0x0b);
    private readonly TestNodeKeyManager _payee = new(0x0c);
    private readonly InMemoryPaymentDbRepository _payments = new();
    private readonly Mock<IChannelStateDbRepository> _channelState = new();
    private readonly Mock<IChannelOperations> _channelOperations = new();
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly ShiftedTimeProvider _time = new();
    private readonly ServiceProvider _provider;

    public PaymentServiceTests()
    {
        _blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(Height);
        _channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(_channelState.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(_us);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
                                  new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
        services.AddSingleton<IOnionReplayStore>(new InMemoryOnionReplayStore());
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddSingleton(_blockchainMonitor.Object);
        services.AddSingleton(_channels.Object);
        services.AddSingleton(_channelOperations.Object);
        services.AddSingleton(new Mock<IPeerLivenessProbe>().Object);
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped<IPaymentDbRepository>(_ => _payments);
        services.AddPaymentsServices();
        services.AddPaymentSendServices();
        _provider = services.BuildServiceProvider();
    }

    private PaymentService Service => _provider.GetRequiredService<PaymentService>();

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void Given_OurHtlcTimedOutOnChain_When_Interpreted_Then_PermanentChannelFailureFromUs()
    {
        // Arrange (BOLT 5 plan O3-T4: no hop sent an error, the channel to our peer closed on chain)
        var payment = StoredPayment(HashOf(Preimage()), PaymentStatus.InFlight, 3);

        // Act
        var (code, sourceIndex, reason) = Service.InterpretFailure(payment, HtlcRemoval.OnchainTimeout());

        // Assert
        Assert.Equal(FailureCode.PermanentChannelFailure, code);
        Assert.Null(sourceIndex);
        Assert.Contains("closed on chain", reason);
    }

    [Fact]
    public void Given_SendServices_When_Resolved_Then_OneServiceBehindBothInterfaces()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddPaymentSendServices();
        services.AddPaymentSendServices();

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(IPaymentService));
        Assert.Single(services, d => d.ServiceType == typeof(IPaymentOutcomeHandler));
        Assert.Same(_provider.GetRequiredService<IPaymentService>(),
                    _provider.GetRequiredService<IPaymentOutcomeHandler>());
    }

    [Fact]
    public async Task Given_InvoiceForAnotherNetwork_When_Paying_Then_ArgumentExceptionAndNothingPersisted()
    {
        // Arrange
        var (bolt11, _) = CreateInvoice(_payee, s_amount, BitcoinNetwork.Mainnet);

        // Act / Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Service.PayInvoiceAsync(bolt11, null, s_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, _payments.AddCalls);
    }

    [Fact]
    public async Task Given_ExpiredInvoice_When_Paying_Then_ArgumentException()
    {
        // Arrange
        var (bolt11, _) = CreateInvoice(_payee, s_amount, expirySeconds: 60);
        _time.Shift = TimeSpan.FromMinutes(2);

        // Act
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
                            () => Service.PayInvoiceAsync(bolt11, null, s_timeout,
                                                          TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("expired", exception.Message);
        Assert.Equal(0, _payments.AddCalls);
    }

    [Theory]
    [InlineData(true, 1_000UL)]
    [InlineData(false, null)]
    [InlineData(true, 0UL)]
    public async Task Given_InconsistentAmount_When_Paying_Then_ArgumentException(bool invoiceHasAmount,
                                                                                 ulong? amountMsat)
    {
        // Arrange
        var (bolt11, _) = CreateInvoice(_payee, invoiceHasAmount ? s_amount : null);
        var amount = amountMsat is { } msat ? LightningMoney.MilliSatoshis(msat) : null;

        // Act / Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Service.PayInvoiceAsync(bolt11, amount, s_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, _payments.AddCalls);
    }

    [Fact]
    public async Task Given_OurOwnInvoice_When_Paying_Then_ArgumentException()
    {
        // Arrange
        var (bolt11, _) = CreateInvoice(_us, s_amount);

        // Act / Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Service.PayInvoiceAsync(bolt11, null, s_timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_NoBlockProcessed_When_Paying_Then_InvalidOperationAndNothingPersisted()
    {
        // Arrange
        _blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(0u);
        var (bolt11, _) = CreateInvoice(_payee, s_amount);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.PayInvoiceAsync(bolt11, null, s_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, _payments.AddCalls);
    }

    [Fact]
    public async Task Given_NoUsableChannel_When_Paying_Then_FailedPaymentIsStoredAndNothingOffered()
    {
        // Arrange
        var (bolt11, hash) = CreateInvoice(_payee, s_amount);

        // Act
        var payment = await Service.PayInvoiceAsync(bolt11, null, s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Contains("No route", payment.FailureReason);
        Assert.Equal(PaymentStatus.Failed, (await _payments.GetByPaymentHashAsync(hash))!.Status);
        _channelOperations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_InvoiceWithoutAmount_When_PayingWithAmount_Then_ThePaymentCarriesIt()
    {
        // Arrange
        var (bolt11, _) = CreateInvoice(_payee, null);

        // Act
        var payment = await Service.PayInvoiceAsync(bolt11, LightningMoney.MilliSatoshis(7_000), s_timeout,
                                                    TestContext.Current.CancellationToken);

        // Assert: no route, but the attempt is recorded with the caller's amount
        Assert.Equal(7_000UL, payment.Amount.MilliSatoshi);
    }

    [Theory]
    [InlineData(PaymentStatus.InFlight)]
    [InlineData(PaymentStatus.Succeeded)]
    public async Task Given_StoredPayment_When_PayingTheSameHash_Then_InvalidOperationException(PaymentStatus status)
    {
        // Arrange
        var (bolt11, hash) = CreateInvoice(_payee, s_amount);
        await _payments.AddAsync(StoredPayment(hash, status, htlcId: 3));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => Service.PayInvoiceAsync(bolt11, null, s_timeout,
                                                          TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(status.ToString(), exception.Message);
        Assert.Equal(1, _payments.AddCalls);
    }

    [Fact]
    public async Task Given_InFlightPaymentWithAnotherHtlc_When_AFulfillWithTheRightPreimageArrives_Then_ItIsRecorded()
    {
        // Arrange
        var preimage = Preimage();
        var hash = HashOf(preimage);
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight, htlcId: 3));

        // Act
        var handled = await Service.HandleOutgoingHtlcFulfilledAsync(
                          new OutgoingHtlcFulfilled(s_channelId, 4, hash, preimage),
                          TestContext.Current.CancellationToken);

        // Assert: the preimage proves the payment; the recorded HTLC is kept
        Assert.True(handled);
        var stored = await _payments.GetByPaymentHashAsync(hash);
        Assert.Equal(PaymentStatus.Succeeded, stored!.Status);
        Assert.Equal(preimage, stored.Preimage);
        Assert.Equal(3UL, stored.OutgoingHtlcId);
    }

    [Fact]
    public async Task Given_FailedPayment_When_AFulfillWithTheRightPreimageArrives_Then_ItSucceedsWithThePreimage()
    {
        // Arrange: e.g. a failure was applied to the wrong attempt, then the real HTLC was fulfilled
        var preimage = Preimage();
        var hash = HashOf(preimage);
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.Failed, htlcId: 3));

        // Act
        var handled = await Service.HandleOutgoingHtlcFulfilledAsync(
                          new OutgoingHtlcFulfilled(s_channelId, 3, hash, preimage),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.True(handled);
        var stored = await _payments.GetByPaymentHashAsync(hash);
        Assert.Equal(PaymentStatus.Succeeded, stored!.Status);
        Assert.Equal(preimage, stored.Preimage);
        Assert.Null(stored.FailureReason);
    }

    [Fact]
    public async Task Given_FulfillOfAForwardedHtlcWithTheSameHash_When_Handled_Then_PaymentUnchanged()
    {
        // Arrange
        var preimage = Preimage();
        var hash = HashOf(preimage);
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.Failed, htlcId: 3));
        _channelState.Setup(s => s.GetHtlcOriginAsync(s_channelId, new HtlcKey(HtlcDirection.Outgoing, 9)))
                     .ReturnsAsync(HtlcOrigin.Forwarded(s_channelId, 1));

        // Act
        var handled = await Service.HandleOutgoingHtlcFulfilledAsync(
                          new OutgoingHtlcFulfilled(s_channelId, 9, hash, preimage),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.False(handled);
        Assert.Equal(PaymentStatus.Failed, (await _payments.GetByPaymentHashAsync(hash))!.Status);
    }

    [Fact]
    public async Task Given_AnotherPartWithOurOriginFailsAndNoneIsLive_When_Handled_Then_ThePaymentFailsWithoutCode()
    {
        // Arrange: after a restart (no session), a part of a split payment that the row does not record fails; its
        // stored origin says it is ours, and no HTLC of the payment is live any more (NL-270)
        var hash = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight, htlcId: 3));
        _channelState.Setup(s => s.GetHtlcOriginAsync(s_channelId, new HtlcKey(HtlcDirection.Outgoing, 4)))
                     .ReturnsAsync(HtlcOrigin.Local(hash));

        // Act
        var handled = await Service.HandleOutgoingHtlcFailedAsync(
                          new OutgoingHtlcFailed(s_channelId, 4, hash, HtlcRemoval.Fail(new byte[292])),
                          TestContext.Current.CancellationToken);

        // Assert: its route was not stored, so the failure is recorded without a code
        Assert.True(handled);
        var stored = await _payments.GetByPaymentHashAsync(hash);
        Assert.Equal(PaymentStatus.Failed, stored!.Status);
        Assert.Null(stored.FailureCode);
        Assert.Contains("one part of the payment", stored.FailureReason);
        Assert.Equal(3UL, stored.OutgoingHtlcId);
    }

    [Fact]
    public async Task Given_FailOfAnotherHtlc_When_Handled_Then_PaymentUnchanged()
    {
        // Arrange
        var hash = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight, htlcId: 3));

        // Act
        var handled = await Service.HandleOutgoingHtlcFailedAsync(
                          new OutgoingHtlcFailed(s_channelId, 4, hash, HtlcRemoval.Fail(new byte[292])),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.False(handled);
        Assert.Equal(PaymentStatus.InFlight, (await _payments.GetByPaymentHashAsync(hash))!.Status);
    }

    [Fact]
    public async Task Given_HtlcIdNotRecordedAndNoStoredOrigin_When_Failed_Then_ThePaymentFails()
    {
        // Arrange: production before NL-250 stores no origin, and a failed HTLC has left channel memory
        var hash = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight));

        // Act
        var handled = await Service.HandleOutgoingHtlcFailedAsync(
                          new OutgoingHtlcFailed(s_channelId, 9, hash, HtlcRemoval.Fail(new byte[292])),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.True(handled);
        var stored = await _payments.GetByPaymentHashAsync(hash);
        Assert.Equal(PaymentStatus.Failed, stored!.Status);
        Assert.Equal(9UL, stored.OutgoingHtlcId);
    }

    [Fact]
    public async Task Given_InFlightPaymentThatWasNeverOffered_When_PayingTheHashAgain_Then_TheNewAttemptProceeds()
    {
        // Arrange: a crash after the InFlight save, before the offer (no HTLC anywhere)
        var (bolt11, hash) = CreateInvoice(_payee, s_amount);
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight));

        // Act
        var payment = await Service.PayInvoiceAsync(bolt11, null, s_timeout, TestContext.Current.CancellationToken);

        // Assert: the stale attempt was failed, then replaced by the new one (no route here)
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Contains("No route", payment.FailureReason);
        Assert.Equal(2, _payments.AddCalls);
        Assert.Equal(PaymentStatus.Failed, (await _payments.GetByPaymentHashAsync(hash))!.Status);
    }

    [Fact]
    public async Task Given_InFlightPaymentsWithoutHtlc_When_ReconcilingAtStartup_Then_OnlyTheNeverOfferedOneFails()
    {
        // Arrange: one attempt has no HTLC at all, the other has one on a channel that is not in memory
        var neverOffered = HashOf(Preimage());
        var unknownChannel = HashOf(Preimage());
        var recorded = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(neverOffered, PaymentStatus.InFlight));
        await _payments.AddAsync(StoredPayment(unknownChannel, PaymentStatus.InFlight));
        await _payments.AddAsync(StoredPayment(recorded, PaymentStatus.InFlight, htlcId: 3));
        _channelState.Setup(s => s.FindHtlcsByOriginAsync(HtlcOrigin.Local(unknownChannel)))
                     .ReturnsAsync([(s_channelId, new HtlcKey(HtlcDirection.Outgoing, 5))]);

        // Act
        var reconciled = await Service.ReconcileInFlightPaymentsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, reconciled);
        var failed = await _payments.GetByPaymentHashAsync(neverOffered);
        Assert.Equal(PaymentStatus.Failed, failed!.Status);
        Assert.Null(failed.FailureCode);
        Assert.Contains("never offered", failed.FailureReason);
        Assert.Equal(PaymentStatus.InFlight, (await _payments.GetByPaymentHashAsync(unknownChannel))!.Status);
        Assert.Equal(PaymentStatus.InFlight, (await _payments.GetByPaymentHashAsync(recorded))!.Status);
    }

    [Fact]
    public async Task Given_OneHashLocked_When_LockingAnotherHashWithTheSameFirstByte_Then_ItDoesNotWait()
    {
        // Arrange: the old 64 striped locks put these two hashes in the same stripe
        var ct = TestContext.Current.CancellationToken;
        var service = Service;
        var first = HashOf(Preimage());
        var bytes = ((byte[])first).ToArray();
        bytes[31] ^= 0xFF;
        var second = new Hash(bytes);
        var held = await service.AcquireHashLockAsync(first, ct);

        // Act
        var other = service.AcquireHashLockAsync(second, ct);
        var same = service.AcquireHashLockAsync(first, ct);

        // Assert
        Assert.True(other.IsCompletedSuccessfully);
        Assert.False(same.IsCompleted);
        held.Dispose();
        (await same.WaitAsync(TimeSpan.FromSeconds(5), ct)).Dispose();
        (await other).Dispose();
    }

    [Fact]
    public async Task Given_ACanceledWaitForAHashLock_When_TheHolderReleases_Then_TheLockIsFreeAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var service = Service;
        var hash = HashOf(Preimage());
        var held = await service.AcquireHashLockAsync(hash, ct);
        using var canceled = new CancellationTokenSource();
        var waiting = service.AcquireHashLockAsync(hash, canceled.Token);

        // Act
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        held.Dispose();

        // Assert: released once despite the double dispose, and reusable
        using var again = await service.AcquireHashLockAsync(hash, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task Given_PreimageNotMatchingTheHash_When_Handled_Then_Ignored()
    {
        // Arrange
        var hash = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight, htlcId: 3));

        // Act
        var handled = await Service.HandleOutgoingHtlcFulfilledAsync(
                          new OutgoingHtlcFulfilled(s_channelId, 3, hash, new Secret(new byte[32])),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.False(handled);
        Assert.Equal(PaymentStatus.InFlight, (await _payments.GetByPaymentHashAsync(hash))!.Status);
    }

    [Fact]
    public async Task Given_SucceededPayment_When_TheFulfillIsReplayed_Then_NothingChanges()
    {
        // Arrange
        var preimage = Preimage();
        var hash = HashOf(preimage);
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight, htlcId: 3));
        var fulfilled = new OutgoingHtlcFulfilled(s_channelId, 3, hash, preimage);
        Assert.True(await Service.HandleOutgoingHtlcFulfilledAsync(fulfilled, TestContext.Current.CancellationToken));
        var updates = _payments.UpdateCalls;

        // Act
        var handled = await Service.HandleOutgoingHtlcFulfilledAsync(fulfilled, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(handled);
        Assert.Equal(updates, _payments.UpdateCalls);
        Assert.Equal(preimage, (await _payments.GetByPaymentHashAsync(hash))!.Preimage);
    }

    [Fact]
    public async Task Given_HtlcIdNotRecordedButLocalOrigin_When_Fulfilled_Then_PaymentSucceedsWithTheHtlc()
    {
        // Arrange: a crash between the offer's save and the save of the HTLC id
        var preimage = Preimage();
        var hash = HashOf(preimage);
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight));
        _channelState.Setup(s => s.GetHtlcOriginAsync(s_channelId, new HtlcKey(HtlcDirection.Outgoing, 9)))
                     .ReturnsAsync(HtlcOrigin.Local(hash));

        // Act
        var handled = await Service.HandleOutgoingHtlcFulfilledAsync(
                          new OutgoingHtlcFulfilled(s_channelId, 9, hash, preimage),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.True(handled);
        var stored = await _payments.GetByPaymentHashAsync(hash);
        Assert.Equal(PaymentStatus.Succeeded, stored!.Status);
        Assert.Equal((s_channelId, 9UL), (stored.OutgoingChannelId!.Value, stored.OutgoingHtlcId!.Value));
    }

    [Fact]
    public async Task Given_HtlcIdNotRecordedAndForwardedOrigin_When_Failed_Then_Ignored()
    {
        // Arrange: same hash, but the HTLC forwards someone else's payment
        var hash = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight));
        _channelState.Setup(s => s.GetHtlcOriginAsync(s_channelId, new HtlcKey(HtlcDirection.Outgoing, 9)))
                     .ReturnsAsync(HtlcOrigin.Forwarded(s_channelId, 1));

        // Act
        var handled = await Service.HandleOutgoingHtlcFailedAsync(
                          new OutgoingHtlcFailed(s_channelId, 9, hash, HtlcRemoval.Fail(new byte[292])),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.False(handled);
        Assert.Equal(PaymentStatus.InFlight, (await _payments.GetByPaymentHashAsync(hash))!.Status);
    }

    [Fact]
    public async Task Given_FailMalformed_When_Handled_Then_TheCodeIsStoredAgainstOurPeer()
    {
        // Arrange
        var hash = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight, htlcId: 3));

        // Act
        var handled = await Service.HandleOutgoingHtlcFailedAsync(
                          new OutgoingHtlcFailed(s_channelId, 3, hash,
                                                 HtlcRemoval.FailMalformed((ushort)FailureCode.InvalidOnionHmac,
                                                                           new byte[32])),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.True(handled);
        var stored = await _payments.GetByPaymentHashAsync(hash);
        Assert.Equal(PaymentStatus.Failed, stored!.Status);
        Assert.Equal(FailureCode.InvalidOnionHmac, stored.FailureCode);
        Assert.Equal(0, stored.FailureSourceIndex);
        Assert.Contains("malformed", stored.FailureReason);
    }

    [Fact]
    public async Task Given_ErrorOnionNoHopAuthenticated_When_Handled_Then_FailedWithoutCodeOrSource()
    {
        // Arrange
        var hash = HashOf(Preimage());
        await _payments.AddAsync(StoredPayment(hash, PaymentStatus.InFlight, htlcId: 3));

        // Act
        var handled = await Service.HandleOutgoingHtlcFailedAsync(
                          new OutgoingHtlcFailed(s_channelId, 3, hash, HtlcRemoval.Fail(new byte[292])),
                          TestContext.Current.CancellationToken);

        // Assert
        Assert.True(handled);
        var stored = await _payments.GetByPaymentHashAsync(hash);
        Assert.Equal(PaymentStatus.Failed, stored!.Status);
        Assert.Null(stored.FailureCode);
        Assert.Null(stored.FailureSourceIndex);
        Assert.Contains("no hop", stored.FailureReason);
    }

    [Theory]
    [InlineData(100_000UL, 5_000UL)]
    [InlineData(50_000_123UL, 250_000UL)]
    [InlineData(10_000_000_000UL, 50_000_000UL)]
    public void Given_Amount_When_GettingTheDefaultFeeLimit_Then_HalfAPercentWithA5000MsatFloor(ulong amountMsat,
        ulong expectedMsat)
    {
        // Arrange
        var options = new PaymentSendOptions();

        // Act
        var maxFee = options.GetMaxFee(LightningMoney.MilliSatoshis(amountMsat));

        // Assert
        Assert.Equal(expectedMsat, maxFee.MilliSatoshi);
    }

    private (string Bolt11, Hash PaymentHash) CreateInvoice(TestNodeKeyManager payee, LightningMoney? amount,
                                                            BitcoinNetwork? network = null, long expirySeconds = 3_600)
    {
        var hash = HashOf(Preimage());
        var invoice = new Invoice(amount ?? LightningMoney.Zero, "test", PaymentTarget.FromWireBytes(hash),
                                  PaymentTarget.FromWireBytes(RandomNumberGenerator.GetBytes(32)),
                                  network ?? BitcoinNetwork.Regtest, payee);
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + expirySeconds);
        return (invoice.Encode(), hash);
    }

    private PaymentModel StoredPayment(Hash hash, PaymentStatus status, ulong? htlcId = null)
    {
        var hop = new PaymentHop(_payee.NodeId, new ShortChannelId(400, 1, 0), s_amount, Height + 21,
                                 new Secret(RandomNumberGenerator.GetBytes(32)));
        var now = DateTimeOffset.UtcNow;
        return PaymentModel.Restore(hash, null, _payee.NodeId, s_amount, LightningMoney.Zero, now, status,
                                    htlcId is null ? (ChannelId?)null : s_channelId, htlcId,
                                    status == PaymentStatus.Succeeded ? Preimage() : (Secret?)null, null, null, null,
                                    status == PaymentStatus.InFlight ? null : now, [hop]);
    }

    private static Secret Preimage() => new(RandomNumberGenerator.GetBytes(32));

    private static Hash HashOf(Secret preimage) => new(SHA256.HashData((byte[])preimage));

    private sealed class ShiftedTimeProvider : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }
}