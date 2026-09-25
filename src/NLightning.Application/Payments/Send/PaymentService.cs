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
using Domain.Channels.ValueObjects;
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
///   (<see cref="InvalidOperationException"/>); a <c>Failed</c> one is replaced by the new attempt. An <c>InFlight</c>
///   payment without a recorded HTLC id (a crash or a failed save around the offer) is reconciled first against the
///   channel state (see below), so a hash whose HTLC was never offered can be paid again.</item>
///   <item>Route (<see cref="HintRouteBuilder"/>, fee limit from <see cref="PaymentSendOptions"/>): the payee directly,
///   else our channel to the first node of a route hint and the hint's hops. The first-hop peer must have an
///   <c>Open</c> channel with a commitment snapshot whose link is up (<see cref="IPeerLivenessProbe"/>); the channel
///   with the largest local balance is used. No route: the payment is stored <c>Failed</c> and returned.</item>
///   <item>Onion (<see cref="PaymentOnionFactory"/>, CSPRNG session key); the payment is persisted <c>InFlight</c> with
///   every hop's Sphinx shared secret (<see cref="PaymentModel.Route"/>) before the HTLC is offered.</item>
///   <item><c>IChannelOperations.OfferHtlcAsync</c> with <c>HtlcOrigin.Local(hash)</c>; the HTLC id is recorded in the
///   next save (a failure of that save is logged: the outcome still finds the payment, see below). A refused offer
///   (<see cref="CommitmentRefusedException"/>, nothing sent) fails the payment without a failure code. Any other
///   exception leaves it unknown whether the add was persisted, so the payment is reconciled against the channel
///   state: attached to its HTLC if one exists, else failed without a code.</item>
///   <item>Wait for the outcome until the timeout or the cancellation; return the stored payment.</item>
/// </list>
/// <para>Outcome (<see cref="IPaymentOutcomeHandler"/>, called by the HTLC switch): a fulfill whose preimage hashes to
/// the payment hash succeeds the payment; an irrevocable failure is decrypted with the stored shared secrets
/// (<see cref="IFailureOnionService.DecryptErrorPacket"/>), interpreted (<see cref="FailureInterpreter"/>) and stored
/// (code, erring hop index, reason). An <c>update_fail_malformed_htlc</c> comes from our peer (hop 0), which could not
/// parse our onion. There are no automatic retries (<c>IPaymentService</c> retry policy: the caller may pay a failed
/// hash again).</para>
/// <para>Matching an outcome to the payment: by the recorded (channel, HTLC id); when no id is recorded, the HTLC must
/// not carry another origin (<c>IChannelStateDbRepository.GetHtlcOriginAsync</c>; <c>OfferHtlcAsync</c> does not store
/// origins before NL-250, so a missing origin is accepted), its record in channel memory (when still there) must match
/// the stored first hop (peer, amount, CLTV expiry), and a failure is applied only when no other non-final outgoing HTLC
/// carries the hash (else it may be an earlier failed attempt's, replayed on startup, while the retry's HTLC is live).
/// A fulfill whose preimage is right but that matches no in-flight attempt (the payment is <c>Failed</c>, or recorded
/// another HTLC) is logged at Error and still recorded: the preimage proves the payment.</para>
/// <para>Reconciliation (<see cref="ReconcileInFlightPaymentsAsync"/> at startup, after the channels are registered in
/// memory, and lazily when a hash is paid again): an <c>InFlight</c> payment without an HTLC id is attached to its HTLC
/// when exactly one non-final outgoing HTLC in channel memory matches its first hop (or carries its stored
/// <c>HtlcOrigin.Local</c>), failed without a code when none does and no HTLC with its origin sits on a channel that
/// is not in memory, and left alone otherwise.</para>
/// <para>Singleton; thread-safe. The lock is per payment hash (refcounted), so a slow payment attempt never delays the
/// outcome of another hash. Persistence goes through a fresh DI scope per step (scoped
/// <see cref="IPaymentDbRepository"/> sharing the scope's <see cref="IUnitOfWork"/>).</para>
/// </remarks>
public sealed class PaymentService : IPaymentService, IPaymentOutcomeHandler
{
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

    private readonly Dictionary<Hash, HashLock> _hashLocks = [];
    private readonly Lock _hashLocksSync = new();

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

    private enum OutcomeMatch
    {
        /// <summary>The event belongs to the payment's in-flight attempt.</summary>
        Match,

        /// <summary>The HTLC is not one of our payments (no payment for the hash, or another origin).</summary>
        NotOurs,

        /// <summary>A payment exists for the hash, but the event does not complete its in-flight attempt.</summary>
        Unmatched
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
        TaskCompletionSource waiter;
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
            waiter = NewWaiter();
            _waiters[paymentHash] = waiter;

            try
            {
                ulong htlcId;
                try
                {
                    htlcId = await _channelOperations.OfferHtlcAsync(channel.ChannelId, route.FirstHopAmount,
                                                                     paymentHash, route.FirstHopCltvExpiry,
                                                                     onion.Packet, null, HtlcOrigin.Local(paymentHash),
                                                                     CancellationToken.None);
                }
                catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
                {
                    // Nothing was persisted or sent for the HTLC
                    payment.Fail(null, null,
                                 $"The HTLC could not be offered on channel {channel.ChannelId}: {e.Message}",
                                 _timeProvider.GetUtcNow());
                    await SaveAsync(payment, isNew: false);
                    CompleteWaiter(paymentHash);
                    LogFailed(payment);
                    return payment;
                }
                catch (Exception e)
                {
                    // Unknown whether the add was persisted: settle the payment from the channel state
                    _logger.LogError(e, "Offering the HTLC of payment {PaymentHash} on channel {ChannelId} failed; "
                                      + "reconciling the payment with the channel state", paymentHash,
                                     channel.ChannelId);
                    payment = await ReconcileUnrecordedAsync(
                                  payment, $"The HTLC could not be offered on channel {channel.ChannelId}: "
                                         + e.Message);
                    if (payment.Status != PaymentStatus.InFlight)
                        return payment;
                    if (payment.OutgoingHtlcId is null)
                        throw;

                    htlcId = payment.OutgoingHtlcId.Value;
                }

                if (payment.OutgoingHtlcId is null)
                {
                    payment.AddOutgoingHtlc(channel.ChannelId, htlcId);
                    try
                    {
                        await SaveAsync(payment, isNew: false);
                    }
                    catch (Exception e)
                    {
                        // The HTLC is live: its outcome still matches the payment through the channel state
                        _logger.LogError(e, "Could not record HTLC {HtlcId} on channel {ChannelId} for payment "
                                          + "{PaymentHash}; its outcome will be matched through the channel state",
                                         htlcId, channel.ChannelId, paymentHash);
                    }
                }

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Paying {PaymentHash}: {Amount} msat to {Payee} over {Hops} hop(s), fee {Fee} msat, HTLC "
                      + "{HtlcId} on channel {ChannelId}", paymentHash, payment.Amount.MilliSatoshi,
                        payment.PayeeNodeId, route.Hops.Count, payment.Fee.MilliSatoshi, htlcId, channel.ChannelId);
            }
            catch
            {
                _waiters.TryRemove(KeyValuePair.Create(paymentHash, waiter));
                throw;
            }
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
            var (payment, match) = await MatchOutcomeAsync(scope, fulfilled.ChannelId, fulfilled.HtlcId,
                                                           fulfilled.PaymentHash, isFailure: false);
            if (payment is null || match == OutcomeMatch.NotOurs)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("The fulfill of HTLC {HtlcId} on channel {ChannelId} ({PaymentHash}) is not one "
                                   + "of our payments", fulfilled.HtlcId, fulfilled.ChannelId, fulfilled.PaymentHash);
                return false;
            }

            if (payment.Status == PaymentStatus.Succeeded)
                return false;

            var now = _timeProvider.GetUtcNow();
            if (match == OutcomeMatch.Match)
            {
                payment.Succeed(fulfilled.PaymentPreimage, now);
            }
            else
            {
                // The preimage proves the payment: record it rather than lose it (as LND does)
                _logger.LogError("HTLC {HtlcId} on channel {ChannelId} was fulfilled for payment {PaymentHash}, which is "
                               + "{Status} with HTLC {RecordedHtlcId} on channel {RecordedChannelId}; recording the "
                               + "preimage and marking the payment succeeded", fulfilled.HtlcId, fulfilled.ChannelId,
                                 payment.PaymentHash, payment.Status, payment.OutgoingHtlcId,
                                 payment.OutgoingChannelId);
                payment = WithPreimage(payment, fulfilled, now);
            }

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
            var (payment, match) = await MatchOutcomeAsync(scope, failed.ChannelId, failed.HtlcId,
                                                           failed.PaymentHash, isFailure: true);
            if (payment is null || match != OutcomeMatch.Match)
            {
                if (payment is { Status: PaymentStatus.InFlight } && match == OutcomeMatch.Unmatched)
                    _logger.LogWarning("Ignoring the failure of HTLC {HtlcId} on channel {ChannelId}: it does not "
                                     + "match the in-flight attempt of payment {PaymentHash}", failed.HtlcId,
                                       failed.ChannelId, failed.PaymentHash);
                else if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("The failure of HTLC {HtlcId} on channel {ChannelId} ({PaymentHash}) completes "
                                   + "none of our payments", failed.HtlcId, failed.ChannelId, failed.PaymentHash);
                return false;
            }

            var (code, sourceIndex, reason) = InterpretFailure(payment, failed.Removal);
            payment.Fail(code, sourceIndex, reason, _timeProvider.GetUtcNow());
            await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            LogFailed(payment);
        }

        CompleteWaiter(failed.PaymentHash);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> ReconcileInFlightPaymentsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PaymentModel> inFlight;
        using (var scope = _serviceScopeFactory.CreateScope())
            inFlight = await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().GetInFlightAsync();

        var reconciled = 0;
        foreach (var candidate in inFlight.Where(p => p.OutgoingHtlcId is null))
        {
            using (await AcquireHashLockAsync(candidate.PaymentHash, cancellationToken))
            {
                // Re-read under the lock: an outcome may have completed it meanwhile
                var payment = await GetPaymentAsync(candidate.PaymentHash, CancellationToken.None);
                if (payment is not { Status: PaymentStatus.InFlight, OutgoingHtlcId: null })
                    continue;

                payment = await ReconcileUnrecordedAsync(
                              payment, "The HTLC was never offered (no HTLC found for the payment at startup).");
                if (payment.Status != PaymentStatus.InFlight || payment.OutgoingHtlcId is not null)
                    reconciled++;
            }
        }

        return reconciled;
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

    /// <summary>
    /// Takes the lock of one payment hash. Locks are per hash (created on demand, dropped when unused), so unrelated
    /// payments never wait for each other.
    /// </summary>
    internal async Task<IDisposable> AcquireHashLockAsync(Hash paymentHash, CancellationToken cancellationToken)
    {
        HashLock? entry;
        lock (_hashLocksSync)
        {
            if (!_hashLocks.TryGetValue(paymentHash, out entry))
                _hashLocks[paymentHash] = entry = new HashLock();
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            ReleaseHashLock(paymentHash, entry, held: false);
            throw;
        }

        return new HashLockReleaser(this, paymentHash, entry);
    }

    private void ReleaseHashLock(Hash paymentHash, HashLock entry, bool held)
    {
        if (held)
            entry.Semaphore.Release();

        lock (_hashLocksSync)
        {
            if (--entry.References == 0)
                _hashLocks.Remove(paymentHash);
        }
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

    /// <summary>
    /// Refuses a hash whose payment is in flight or succeeded. Call it under the hash's lock: an in-flight payment
    /// without an HTLC id is reconciled first (no offer of this node is running for the hash then).
    /// </summary>
    private async Task ThrowIfPaymentExistsAsync(Hash paymentHash)
    {
        var existing = await GetPaymentAsync(paymentHash, CancellationToken.None);
        if (existing is { Status: PaymentStatus.InFlight, OutgoingHtlcId: null })
            existing = await ReconcileUnrecordedAsync(
                           existing, "The HTLC was never offered (no HTLC found for the payment when paying the hash "
                                   + "again).");

        if (existing is { Status: PaymentStatus.InFlight or PaymentStatus.Succeeded })
            throw new InvalidOperationException(
                $"A payment for payment hash {paymentHash} is already {existing.Status}.");
    }

    /// <summary>
    /// Settles an <c>InFlight</c> payment that has no recorded HTLC id, under its hash's lock (see the class remarks):
    /// attaches its HTLC, fails it with <paramref name="neverOfferedReason"/>, or leaves it when that is ambiguous.
    /// </summary>
    /// <returns>The payment as stored.</returns>
    private async Task<PaymentModel> ReconcileUnrecordedAsync(PaymentModel payment, string neverOfferedReason)
    {
        var candidates = FindAttemptHtlcs(payment, matchFirstHop: true);

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var unknownChannels = false;
        var byOrigin = await unitOfWork.ChannelStateDbRepository
                                       .FindHtlcsByOriginAsync(HtlcOrigin.Local(payment.PaymentHash))
                    ?? [];
        foreach (var (channelId, key) in byOrigin)
        {
            if (key.Direction != HtlcDirection.Outgoing)
                continue;
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel) || channel.Commitments is null)
            {
                unknownChannels = true;
                continue;
            }

            // An archived row (already final, e.g. an earlier attempt's) is not in the snapshot
            if (channel.Commitments.GetHtlc(HtlcDirection.Outgoing, key.Id) is { } htlc
             && !HtlcStateTable.IsFinal(htlc.State) && !candidates.Contains((channelId, key.Id)))
                candidates.Add((channelId, key.Id));
        }

        if (candidates.Count == 1)
        {
            var (channelId, htlcId) = candidates[0];
            payment.AddOutgoingHtlc(channelId, htlcId);
            _logger.LogWarning("Payment {PaymentHash} had no recorded HTLC; attached HTLC {HtlcId} on channel "
                             + "{ChannelId}", payment.PaymentHash, htlcId, channelId);
        }
        else if (candidates.Count == 0 && !unknownChannels)
        {
            payment.Fail(null, null, neverOfferedReason, _timeProvider.GetUtcNow());
        }
        else
        {
            _logger.LogWarning("Payment {PaymentHash} has no recorded HTLC and {Count} candidate HTLC(s) (HTLCs on "
                             + "channels not in memory: {UnknownChannels}); leaving it in flight",
                               payment.PaymentHash, candidates.Count, unknownChannels);
            return payment;
        }

        await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
        await unitOfWork.SaveChangesAsync();
        if (payment.Status != PaymentStatus.InFlight)
        {
            LogFailed(payment);
            CompleteWaiter(payment.PaymentHash);
        }

        return payment;
    }

    /// <summary>
    /// The non-final outgoing HTLCs in channel memory that carry the payment's hash; with
    /// <paramref name="matchFirstHop"/>, only those that also match its stored first hop (peer, amount, CLTV expiry).
    /// </summary>
    private List<(ChannelId ChannelId, ulong HtlcId)> FindAttemptHtlcs(PaymentModel payment, bool matchFirstHop)
    {
        var firstHop = matchFirstHop && payment.Route.Count > 0 ? payment.Route[0] : null;
        var found = new List<(ChannelId ChannelId, ulong HtlcId)>();
        foreach (var channel in _channelMemoryRepository.FindChannels(c => c.Commitments is not null))
        {
            if (channel.Commitments is not { } commitments)
                continue;

            foreach (var htlc in commitments.Htlcs.Values)
            {
                if (htlc.Direction != HtlcDirection.Outgoing || htlc.PaymentHash != payment.PaymentHash
                 || HtlcStateTable.IsFinal(htlc.State))
                    continue;
                if (firstHop is null || MatchesFirstHop(firstHop, channel, htlc))
                    found.Add((channel.ChannelId, htlc.Id));
            }
        }

        return found;
    }

    private static bool MatchesFirstHop(PaymentHop firstHop, ChannelModel channel, HtlcRecord htlc) =>
        channel.RemoteNodeId == firstHop.NodeId && htlc.AmountMsat == firstHop.Amount.MilliSatoshi
                                                && htlc.CltvExpiry == firstHop.CltvExpiry;

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
    /// The payment for an outcome event's hash and whether the event completes its in-flight attempt (see the class
    /// remarks). On a match without a recorded HTLC id, the event's HTLC is attached to the returned payment.
    /// </summary>
    private async Task<(PaymentModel? Payment, OutcomeMatch Match)> MatchOutcomeAsync(
        IServiceScope scope, ChannelId channelId, ulong htlcId, Hash paymentHash, bool isFailure)
    {
        var payment = await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>()
                                 .GetByPaymentHashAsync(paymentHash);
        if (payment is null)
            return (null, OutcomeMatch.NotOurs);

        var origin = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelStateDbRepository
                                .GetHtlcOriginAsync(channelId, new HtlcKey(HtlcDirection.Outgoing, htlcId));
        if (origin is { } stored && stored != HtlcOrigin.Local(paymentHash))
            return (payment, OutcomeMatch.NotOurs);

        if (payment.Status != PaymentStatus.InFlight)
            return (payment, OutcomeMatch.Unmatched);

        if (payment.OutgoingHtlcId is { } recordedId)
            return (payment, payment.OutgoingChannelId == channelId && recordedId == htlcId
                                 ? OutcomeMatch.Match
                                 : OutcomeMatch.Unmatched);

        // No id recorded (a crash or a failed save around the offer). Origins are not stored before NL-250, so the
        // HTLC's record, while channel memory still has it, must match the attempt's first hop
        if (payment.Route.Count > 0 && _channelMemoryRepository.TryGetChannel(channelId, out var channel)
                                    && channel.Commitments?.GetHtlc(HtlcDirection.Outgoing, htlcId) is { } htlc
                                    && !MatchesFirstHop(payment.Route[0], channel, htlc))
            return (payment, OutcomeMatch.Unmatched);

        // Every attempt of a hash has the same origin: a failure replayed from an earlier attempt must not fail a retry
        // whose HTLC is still live
        if (isFailure && FindAttemptHtlcs(payment, matchFirstHop: false)
               .Any(h => h.ChannelId != channelId || h.HtlcId != htlcId))
            return (payment, OutcomeMatch.Unmatched);

        payment.AddOutgoingHtlc(channelId, htlcId);
        return (payment, OutcomeMatch.Match);
    }

    /// <summary>
    /// The payment marked succeeded with a proven preimage, whatever its status (see the class remarks).
    /// </summary>
    private static PaymentModel WithPreimage(PaymentModel payment, OutgoingHtlcFulfilled fulfilled,
                                             DateTimeOffset completedAt)
    {
        var (channelId, htlcId) = payment.OutgoingHtlcId is { } recordedId
                                      ? (payment.OutgoingChannelId!.Value, recordedId)
                                      : (fulfilled.ChannelId, fulfilled.HtlcId);
        return PaymentModel.Restore(payment.PaymentHash, payment.Bolt11, payment.PayeeNodeId, payment.Amount,
                                    payment.Fee, payment.CreatedAt, PaymentStatus.Succeeded, channelId, htlcId,
                                    fulfilled.PaymentPreimage, null, null, null, completedAt, payment.Route);
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

    private sealed class HashLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class HashLockReleaser(PaymentService owner, Hash paymentHash, HashLock entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseHashLock(paymentHash, entry, held: true);
        }
    }
}