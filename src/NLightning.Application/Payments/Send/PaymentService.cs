using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Send;

using Bolt11.Exceptions;
using Bolt11.Models;
using Channels.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
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
using Domain.Protocol.Onion.Interpreters;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Routing;

/// <summary>
/// Sends our payments (BOLT2 plan N8-T3, ONION M4-T6 through route hints; <see cref="IPaymentService"/>) and
/// completes them from the channel layer's outcome events (<see cref="IPaymentOutcomeHandler"/>).
/// </summary>
/// <remarks>
/// <para><see cref="PayInvoiceAsync"/>, in order:</para>
/// <list type="number">
///   <item>Decode the BOLT 11 invoice for our network (signature, features, required fields; <c>Invoice.Decode</c>),
///   refuse it when expired, when it is ours, or when the amount is missing or differs from the invoice's
///   (<see cref="ArgumentException"/>, nothing persisted).</item>
///   <item>Under a per-payment-hash lock: refuse a hash whose stored payment is <c>InFlight</c> or <c>Succeeded</c>
///   (<see cref="InvalidOperationException"/>); a <c>Failed</c> one is replaced by the new attempt.</item>
///   <item>Route (<see cref="HintRouteBuilder"/>, fee limit from <see cref="PaymentSendOptions"/>): the payee directly,
///   else our channel to the first node of a route hint and the hint's hops. The first-hop peer must have an
///   <c>Open</c> channel with a commitment snapshot whose link is up (<see cref="IPeerLivenessProbe"/>); the channel
///   with the largest local balance is used. No route: the payment is stored <c>Failed</c> and returned.</item>
///   <item>Onion (<see cref="PaymentOnionFactory"/>, CSPRNG session key); the payment is persisted <c>InFlight</c> with
///   every hop's Sphinx shared secret (<see cref="PaymentModel.Route"/>) before the HTLC is offered.</item>
///   <item><c>IChannelOperations.OfferHtlcAsync</c> with <c>HtlcOrigin.Local(hash)</c>; the HTLC id is recorded in the
///   next save. A refused offer (<see cref="CommitmentRefusedException"/>, nothing sent) fails the payment without a
///   failure code.</item>
///   <item>Wait for the outcome until the timeout or the cancellation; return the stored payment.</item>
/// </list>
/// <para>Outcome (<see cref="IPaymentOutcomeHandler"/>, called by the HTLC switch): a fulfill whose preimage hashes to
/// the payment hash succeeds the payment; an irrevocable failure is decrypted with the stored shared secrets
/// (<see cref="IFailureOnionService.DecryptErrorPacket"/>), interpreted (<see cref="FailureInterpreter"/>) and stored
/// (code, erring hop index, reason). An <c>update_fail_malformed_htlc</c> comes from our peer (hop 0), which could not
/// parse our onion. There are no automatic retries (<c>IPaymentService</c> retry policy: the caller may pay a failed
/// hash again).</para>
/// <para>Singleton; thread-safe. Persistence goes through a fresh DI scope per step (scoped
/// <see cref="IPaymentDbRepository"/> sharing the scope's <see cref="IUnitOfWork"/>).</para>
/// </remarks>
public sealed class PaymentService : IPaymentService, IPaymentOutcomeHandler
{
    private const int LockStripes = 64;

    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IFailureOnionService _failureOnionService;
    private readonly ILogger<PaymentService> _logger;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly PaymentOnionFactory _onionFactory;
    private readonly IPeerLivenessProbe _peerLivenessProbe;
    private readonly HintRouteBuilder _routeBuilder;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IOptions<PaymentSendOptions> _sendOptions;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    private readonly SemaphoreSlim[] _hashLocks =
        Enumerable.Range(0, LockStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly ConcurrentDictionary<Hash, TaskCompletionSource> _waiters = new();

    public PaymentService(IBlockchainMonitor blockchainMonitor, IChannelMemoryRepository channelMemoryRepository,
                          IChannelOperations channelOperations, IFailureOnionService failureOnionService,
                          ILogger<PaymentService> logger, IOptions<NodeOptions> nodeOptions,
                          PaymentOnionFactory onionFactory, IPeerLivenessProbe peerLivenessProbe,
                          HintRouteBuilder routeBuilder, ISecureKeyManager secureKeyManager,
                          IOptions<PaymentSendOptions> sendOptions, IServiceScopeFactory serviceScopeFactory,
                          TimeProvider timeProvider)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _failureOnionService = failureOnionService;
        _logger = logger;
        _nodeOptions = nodeOptions;
        _onionFactory = onionFactory;
        _peerLivenessProbe = peerLivenessProbe;
        _routeBuilder = routeBuilder;
        _secureKeyManager = secureKeyManager;
        _sendOptions = sendOptions;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Also when no block was processed yet (the final CLTV would be
    /// wrong). Nothing is persisted.</exception>
    public async Task<PaymentModel> PayInvoiceAsync(string bolt11, LightningMoney? amount, TimeSpan timeout,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bolt11);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");

        var target = DecodeInvoice(bolt11);
        var paymentAmount = ResolveAmount(target.Amount, amount);
        var ourNodeId = _secureKeyManager.GetNodePubKey();
        if (target.PayeeNodeId == ourNodeId)
            throw new ArgumentException("The invoice is ours; a node cannot pay itself.", nameof(bolt11));

        var height = _blockchainMonitor.LastProcessedBlockHeight;
        if (height == 0)
            throw new InvalidOperationException("No block has been processed yet; cannot set the HTLC expiry.");

        var paymentHash = target.PaymentHash;
        PaymentModel payment;
        TaskCompletionSource? waiter = null;
        using (await AcquireHashLockAsync(paymentHash, cancellationToken))
        {
            await ThrowIfPaymentExistsAsync(paymentHash);

            var usableChannels = await GetUsableChannelsAsync(cancellationToken);
            var now = _timeProvider.GetUtcNow();
            if (!_routeBuilder.TryBuild(target, paymentAmount, _sendOptions.Value.GetMaxFee(paymentAmount), height,
                                        ourNodeId, usableChannels.ContainsKey, out var route, out var noRouteReason))
            {
                payment = new PaymentModel(paymentHash, bolt11, target.PayeeNodeId, paymentAmount,
                                           LightningMoney.Zero, now);
                payment.Fail(null, null, noRouteReason, now);
                await SaveAsync(payment, isNew: true);
                LogFailed(payment);
                return payment;
            }

            var channel = usableChannels[route.FirstHopNodeId];
            var onion = await _onionFactory.CreateAsync(route);
            payment = new PaymentModel(paymentHash, bolt11, target.PayeeNodeId, route.Amount, route.Fee, now,
                                       BuildHops(route, onion.SharedSecrets, channel));

            // Persisted before the offer: after a crash the outcome still finds its payment
            await SaveAsync(payment, isNew: true);
            waiter = _waiters.AddOrUpdate(paymentHash, _ => NewWaiter(), (_, _) => NewWaiter());

            ulong htlcId;
            try
            {
                htlcId = await _channelOperations.OfferHtlcAsync(channel.ChannelId, route.FirstHopAmount,
                                                                 paymentHash, route.FirstHopCltvExpiry, onion.Packet,
                                                                 null, HtlcOrigin.Local(paymentHash),
                                                                 CancellationToken.None);
            }
            catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
            {
                // Nothing was persisted or sent for the HTLC
                payment.Fail(null, null, $"The HTLC could not be offered on channel {channel.ChannelId}: {e.Message}",
                             _timeProvider.GetUtcNow());
                await SaveAsync(payment, isNew: false);
                CompleteWaiter(paymentHash);
                LogFailed(payment);
                return payment;
            }

            payment.AddOutgoingHtlc(channel.ChannelId, htlcId);
            await SaveAsync(payment, isNew: false);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "Paying {PaymentHash}: {Amount} msat to {Payee} over {Hops} hop(s), fee {Fee} msat, HTLC {HtlcId} "
                  + "on channel {ChannelId}", paymentHash, payment.Amount.MilliSatoshi, payment.PayeeNodeId,
                    route.Hops.Count, payment.Fee.MilliSatoshi, htlcId, channel.ChannelId);
        }

        await WaitAsync(waiter.Task, timeout, cancellationToken);

        return await GetPaymentAsync(paymentHash, CancellationToken.None) ?? payment;
    }

    /// <inheritdoc />
    public async Task<PaymentModel?> GetPaymentAsync(Hash paymentHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _serviceScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>()
                          .GetByPaymentHashAsync(paymentHash);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentModel>> ListPaymentsAsync(int skip, int take,
                                                                     CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _serviceScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().ListAsync(skip, take);
    }

    /// <inheritdoc />
    public async Task<bool> HandleOutgoingHtlcFulfilledAsync(OutgoingHtlcFulfilled fulfilled,
                                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fulfilled);

        if (!SHA256.HashData((byte[])fulfilled.PaymentPreimage).AsSpan().SequenceEqual((byte[])fulfilled.PaymentHash))
        {
            // The engine checks the preimage on receipt; this only guards a wrong event
            _logger.LogError("Ignoring a fulfill of HTLC {HtlcId} on channel {ChannelId}: the preimage does not hash to "
                           + "{PaymentHash}", fulfilled.HtlcId, fulfilled.ChannelId, fulfilled.PaymentHash);
            return false;
        }

        using (await AcquireHashLockAsync(fulfilled.PaymentHash, cancellationToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var payment = await FindPaymentAsync(scope, fulfilled);
            if (payment is null)
                return false;

            payment.Succeed(fulfilled.PaymentPreimage, _timeProvider.GetUtcNow());
            await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Payment {PaymentHash} succeeded ({Amount} msat, fee {Fee} msat)",
                                       payment.PaymentHash, payment.Amount.MilliSatoshi, payment.Fee.MilliSatoshi);
        }

        CompleteWaiter(fulfilled.PaymentHash);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HandleOutgoingHtlcFailedAsync(OutgoingHtlcFailed failed,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failed);

        using (await AcquireHashLockAsync(failed.PaymentHash, cancellationToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var payment = await FindPaymentAsync(scope, failed);
            if (payment is null)
                return false;

            var (code, sourceIndex, reason) = InterpretFailure(payment, failed.Removal);
            payment.Fail(code, sourceIndex, reason, _timeProvider.GetUtcNow());
            await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            LogFailed(payment);
        }

        CompleteWaiter(failed.PaymentHash);
        return true;
    }

    /// <summary>
    /// What an irrevocable failure means at the origin: (BOLT 4 code, erring hop index, local description).
    /// </summary>
    internal (FailureCode? Code, int? SourceIndex, string Reason) InterpretFailure(PaymentModel payment,
                                                                                  HtlcRemoval removal)
    {
        if (removal.Kind == HtlcRemovalKind.FailMalformed)
        {
            // BOLT 4: our peer could not parse the onion we built (it is the only hop that can send this to us)
            var malformed = (FailureCode)removal.FailureCode;
            return (malformed, 0, $"Our peer {DescribeHop(payment, 0)} rejected the onion as malformed "
                                + $"({malformed}, 0x{removal.FailureCode:X4}).");
        }

        if (payment.Route.Count == 0)
            return (null, null, "The HTLC failed and the route's shared secrets were not recorded; the error onion "
                              + "cannot be read.");

        var decrypted = _failureOnionService.DecryptErrorPacket(payment.HopSharedSecrets, removal.Reason.Span);
        var interpretation = FailureInterpreter.Interpret(decrypted, payment.Route.Count);
        if (!interpretation.IsAttributed)
            return (null, null, "The HTLC failed with an error onion no hop of the route authenticated.");

        var index = interpretation.ErringHopIndex!.Value;
        var codeText = interpretation.Code is { } failureCode
                           ? $"{failureCode} (0x{(ushort)failureCode:X4})"
                           : "an unreadable failure";
        var role = interpretation.IsFinalNode ? "the payee" : "hop";
        var detail = interpretation.IsFinalNode
                         ? interpretation.IsPermanent ? "permanent" : "final node"
                         : interpretation.IsNodeFailure ? "node failure" : "channel failure";
        return (interpretation.Code, index,
                $"{codeText} from {role} {index} ({DescribeHop(payment, index)}), {detail}; not retried.");
    }

    private static string DescribeHop(PaymentModel payment, int index) =>
        index < payment.Route.Count ? payment.Route[index].NodeId.ToString() : "unknown node";

    private PaymentTarget DecodeInvoice(string bolt11)
    {
        Invoice invoice;
        try
        {
            invoice = Invoice.Decode(bolt11.Trim(), _nodeOptions.Value.BitcoinNetwork);
        }
        catch (InvoiceSerializationException e)
        {
            throw new ArgumentException($"The invoice cannot be decoded for {_nodeOptions.Value.BitcoinNetwork}: "
                                      + (e.InnerException?.Message ?? e.Message), nameof(bolt11), e);
        }

        if (invoice.ExpiryDate <= _timeProvider.GetUtcNow())
            throw new ArgumentException($"The invoice expired at {invoice.ExpiryDate:O}.", nameof(bolt11));

        return PaymentTarget.FromInvoice(invoice);
    }

    private static LightningMoney ResolveAmount(LightningMoney? invoiceAmount, LightningMoney? amount)
    {
        if (amount is { IsZero: true })
            throw new ArgumentException("The amount must be positive.", nameof(amount));

        if (invoiceAmount is null)
            return amount ?? throw new ArgumentException("The invoice has no amount; give one.", nameof(amount));

        if (amount is not null && amount != invoiceAmount)
            throw new ArgumentException($"The invoice asks for {invoiceAmount.MilliSatoshi} msat, not "
                                      + $"{amount.MilliSatoshi} msat.", nameof(amount));

        return invoiceAmount;
    }

    private async Task ThrowIfPaymentExistsAsync(Hash paymentHash)
    {
        var existing = await GetPaymentAsync(paymentHash, CancellationToken.None);
        if (existing is { Status: PaymentStatus.InFlight or PaymentStatus.Succeeded })
            throw new InvalidOperationException(
                $"A payment for payment hash {paymentHash} is already {existing.Status}.");
    }

    /// <summary>
    /// Our usable channel to each peer: <c>Open</c>, with a commitment snapshot and the link up; the one with the
    /// largest local balance when there are several.
    /// </summary>
    private async Task<Dictionary<CompactPubKey, ChannelModel>> GetUsableChannelsAsync(
        CancellationToken cancellationToken)
    {
        var usable = new Dictionary<CompactPubKey, ChannelModel>();
        var channels = _channelMemoryRepository.FindChannels(c => c is { State: ChannelState.Open, Commitments: not null });
        foreach (var channel in channels.OrderByDescending(c => c.LocalBalance.MilliSatoshi))
        {
            if (usable.ContainsKey(channel.RemoteNodeId))
                continue;
            if (await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId, cancellationToken))
                usable[channel.RemoteNodeId] = channel;
        }

        return usable;
    }

    /// <summary>
    /// The persisted route: hop <c>i</c> is the node that peels layer <c>i</c>, with the HTLC it receives (ours for hop
    /// 0, then what the previous hop forwards) and the channel that reaches it.
    /// </summary>
    private static List<PaymentHop> BuildHops(PaymentRoute route, IReadOnlyList<Secret> sharedSecrets,
                                              ChannelModel firstChannel)
    {
        var hops = new List<PaymentHop>(route.Hops.Count);
        for (var i = 0; i < route.Hops.Count; i++)
        {
            var previous = i == 0 ? null : route.Hops[i - 1];
            hops.Add(new PaymentHop(route.Hops[i].NodeId,
                                    previous?.OutgoingShortChannelId ?? firstChannel.ShortChannelId,
                                    previous?.AmountToForward ?? route.FirstHopAmount,
                                    previous?.OutgoingCltvValue ?? route.FirstHopCltvExpiry, sharedSecrets[i]));
        }

        return hops;
    }

    /// <summary>
    /// The in-flight payment an outcome event belongs to (see <see cref="IPaymentOutcomeHandler"/>), or null.
    /// </summary>
    private async Task<PaymentModel?> FindPaymentAsync(IServiceScope scope, IChannelDomainEvent channelEvent)
    {
        var paymentHash = channelEvent switch
        {
            OutgoingHtlcFulfilled f => f.PaymentHash,
            OutgoingHtlcFailed f => f.PaymentHash,
            _ => throw new ArgumentOutOfRangeException(nameof(channelEvent))
        };

        var payment = await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>()
                                 .GetByPaymentHashAsync(paymentHash);
        if (payment is not { Status: PaymentStatus.InFlight })
            return null;

        if (payment.OutgoingHtlcId is { } htlcId)
            return payment.OutgoingChannelId == channelEvent.ChannelId && htlcId == channelEvent.HtlcId
                       ? payment
                       : null;

        // The id was not recorded (crash between the offer's save and ours): trust the HTLC's stored origin
        var origin = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelStateDbRepository
                                .GetHtlcOriginAsync(channelEvent.ChannelId,
                                                    new HtlcKey(HtlcDirection.Outgoing, channelEvent.HtlcId));
        if (origin != HtlcOrigin.Local(paymentHash))
            return null;

        payment.AddOutgoingHtlc(channelEvent.ChannelId, channelEvent.HtlcId);
        return payment;
    }

    private async Task SaveAsync(PaymentModel payment, bool isNew)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
        if (isNew)
            await repository.AddAsync(payment);
        else
            await repository.UpdateAsync(payment);

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private async Task<IDisposable> AcquireHashLockAsync(Hash paymentHash, CancellationToken cancellationToken)
    {
        var stripe = _hashLocks[((byte[])paymentHash)[0] % LockStripes];
        await stripe.WaitAsync(cancellationToken);
        return new Releaser(stripe);
    }

    private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void CompleteWaiter(Hash paymentHash)
    {
        if (_waiters.TryRemove(paymentHash, out var waiter))
            waiter.TrySetResult();
    }

    private static async Task WaitAsync(Task outcome, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await outcome.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // The payment stays InFlight; its HTLC resolves later
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop waiting, as with the timeout
        }
    }

    private void LogFailed(PaymentModel payment)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Payment {PaymentHash} failed: {Reason}", payment.PaymentHash, payment.FailureReason);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}