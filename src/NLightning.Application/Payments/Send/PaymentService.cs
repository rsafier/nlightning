using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Send;

using Bolt11.Exceptions;
using Bolt11.Models;
using Channels.Interfaces;
using Domain.Bitcoin.Interfaces;
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
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Routing;

/// <summary>
/// Sends our payments (BOLT2 plan N8-T3, ONION M4-T6 through route hints; <see cref="IPaymentService"/>), retries and
/// splits them within per-call limits (NL-270), and completes them from the channel layer's outcome events
/// (<see cref="IPaymentOutcomeHandler"/>).
/// </summary>
/// <remarks>
/// <para><see cref="PayInvoiceAsync(string, LightningMoney?, PayInvoiceOptions, CancellationToken)"/>, in order:</para>
/// <list type="number">
///   <item>Decode the BOLT 11 invoice for our network (signature, features, required fields; <c>Invoice.Decode</c>),
///   refuse it when expired, when it is ours, or when the amount is missing or differs from the invoice's, or an
///   option is out of range (<see cref="ArgumentException"/>, nothing persisted).</item>
///   <item>Under a per-payment-hash lock: refuse a hash whose stored payment is <c>InFlight</c> or <c>Succeeded</c>, or
///   that a call of this process is still paying (<see cref="InvalidOperationException"/>); a <c>Failed</c> one is
///   replaced by the new attempt. An <c>InFlight</c> payment without a recorded HTLC id (a crash or a failed save
///   around the offer) is reconciled first against the channel state (see below).</item>
///   <item>A round (<see cref="PaymentRoutePlanner"/>): over our usable channels (<c>Open</c>, a commitment snapshot,
///   the link up (<see cref="IPeerLivenessProbe"/>); what each can send is the commitment engine's own answer,
///   <see cref="LocalLiquidityEstimator"/>) and the invoice's route hints, one HTLC when a route can carry the amount,
///   else (only with <c>basic_mpp</c>) several with <c>total_msat</c> = the amount, within the fee limit (the call's,
///   else <see cref="PaymentSendOptions.GetMaxFee"/>) and the part limit. No plan in the first round: the payment is
///   stored <c>Failed</c> without a code and returned.</item>
///   <item>Onions (<see cref="PaymentOnionFactory"/>, CSPRNG session keys). The payment row is persisted
///   <c>InFlight</c> before the offers with the route and shared secrets of the round's first part (see "Persistence").
///   </item>
///   <item><c>IChannelOperations.OfferHtlcAsync</c> with <c>HtlcOrigin.Local(hash)</c> for each part; the row's HTLC
///   id is recorded in the next save. An offer the engine refuses (<see cref="CommitmentRefusedException"/>) bounds
///   that channel below the refused amount when a smaller HTLC may pass the broken rule (balance, reserve, fees,
///   in-flight value, dust exposure), else the channel is not used again by this payment (link down, HTLCs disabled,
///   channel failed or shutting down, data loss, HTLC count), and the round plans again; any other exception stops
///   the payment unless channel memory shows the HTLC was added after all.</item>
///   <item>Wait for the outcome until the timeout or the cancellation; return the stored payment.</item>
/// </list>
/// <para>Outcome while the call's session lives: a fulfill whose preimage hashes to the payment hash succeeds the
/// payment. A part's irrevocable failure is decrypted with that part's shared secrets
/// (<see cref="IFailureOnionService.DecryptErrorPacket"/>), interpreted (<see cref="FailureInterpreter"/>) and handed to
/// <see cref="PaymentRetryPolicy"/>: a retryable failure sends the part's amount again in a new round on the thread
/// pool (at once, even while other parts are in flight); a permanent one stops new rounds. The payment is stored
/// <c>Failed</c> (code, erring hop index, reason of the last failure) once no part is in flight and no round may run:
/// stopped, no route left, the attempt budget (<see cref="PaymentSendOptions.MaxAttempts"/>) used, or the timeout
/// passed.</para>
/// <para>Persistence: one row per payment hash (<see cref="IPaymentDbRepository"/>) holding the amount, one route with
/// its shared secrets and one HTLC id. Whenever no part is in flight after a failure, the row is saved <c>Failed</c>
/// before a retry round replaces it (<c>AddAsync</c> over a failed row), so a crash never leaves it <c>InFlight</c>
/// without an HTLC. Parts added while others are in flight are not persisted (their HTLCs carry
/// <c>HtlcOrigin.Local(hash)</c>, so their outcomes still reach the payment after a restart, but their errors can then
/// no longer be decrypted). The row always records a part that was offered: when the recorded part is refused by the
/// engine or fails while other parts are in flight, the row is rewritten to one of those (route, shared secrets, HTLC
/// id, the fees in flight). On success the row holds the fulfilled part's route and HTLC and, as its fee, the fees of
/// the parts in flight at the fulfill (the ones the payee settles; failed parts cost nothing). The route and fee
/// change only by replacing the row (<c>AddAsync</c> over the row failed in the same save).</para>
/// <para>Outcome without a session (after a restart, or a hash this process never paid): by the recorded
/// (channel, HTLC id); when no id is recorded, the HTLC must not carry another origin
/// (<c>IChannelStateDbRepository.GetHtlcOriginAsync</c>), its record in channel memory (when still there) must match
/// the stored first hop (peer, amount, CLTV expiry). A failure is applied only when no other non-final outgoing HTLC
/// carries the hash (else it may be an earlier attempt's, replayed on startup, or one part of a split payment whose
/// other parts are live); an HTLC whose stored origin is <c>Local(hash)</c> but that is not the recorded one (another
/// part) then fails the payment without a code. A fulfill whose preimage is right but that matches no in-flight
/// attempt is logged at Error and still recorded: the preimage proves the payment.</para>
/// <para>Reconciliation (<see cref="ReconcileInFlightPaymentsAsync"/> at startup, after the channels are registered in
/// memory, and lazily when a hash is paid again): an <c>InFlight</c> payment without an HTLC id is attached to its HTLC
/// when exactly one non-final outgoing HTLC in channel memory matches its first hop (or carries its stored
/// <c>HtlcOrigin.Local</c>), failed without a code when none does and no HTLC with its origin sits on a channel that
/// is not in memory, and left alone otherwise.</para>
/// <para>Singleton; thread-safe. The lock is per payment hash (refcounted), so a slow payment never delays the outcome
/// of another hash. Persistence goes through a fresh DI scope per step (scoped <see cref="IPaymentDbRepository"/>
/// sharing the scope's <see cref="IUnitOfWork"/>).</para>
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
    private readonly PaymentRoutePlanner _planner;
    private readonly PaymentRetryPolicy _retryPolicy;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IOptions<PaymentSendOptions> _sendOptions;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAttributionDataService? _attributionDataService;

    /// <summary>
    /// The engine's sender rules (<c>UpdateValidator.ValidateSendAdd</c>) that a smaller HTLC on the same channel may
    /// pass: our balance above the reserve and the commitment fees (B2-ADD-S01..S04), the peer's
    /// <c>max_htlc_value_in_flight_msat</c> (B2-ADD-S09) and the dust exposure (B2-DUST-03/04).
    /// </summary>
    private static readonly HashSet<string> s_liquidityRules =
        ["B2-ADD-S01", "B2-ADD-S02", "B2-ADD-S03", "B2-ADD-S04", "B2-ADD-S09", "B2-DUST-03", "B2-DUST-04"];

    private readonly Dictionary<Hash, HashLock> _hashLocks = [];
    private readonly Lock _hashLocksSync = new();

    private readonly ConcurrentDictionary<Hash, PaymentSession> _sessions = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundRounds = new();

    public PaymentService(IBlockchainMonitor blockchainMonitor, IChannelMemoryRepository channelMemoryRepository,
                          IChannelOperations channelOperations, IFailureOnionService failureOnionService,
                          ILightningSigner lightningSigner, ILogger<PaymentService> logger,
                          IOptions<NodeOptions> nodeOptions, PaymentOnionFactory onionFactory,
                          IPeerLivenessProbe peerLivenessProbe, PaymentRoutePlanner planner,
                          ISecureKeyManager secureKeyManager, IOptions<PaymentSendOptions> sendOptions,
                          IServiceScopeFactory serviceScopeFactory, TimeProvider timeProvider,
                          IAttributionDataService? attributionDataService = null)
    {
        _attributionDataService = attributionDataService;
        _blockchainMonitor = blockchainMonitor;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _failureOnionService = failureOnionService;
        _logger = logger;
        _nodeOptions = nodeOptions;
        _onionFactory = onionFactory;
        _peerLivenessProbe = peerLivenessProbe;
        _planner = planner;
        _secureKeyManager = secureKeyManager;
        _sendOptions = sendOptions;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _retryPolicy = new PaymentRetryPolicy(lightningSigner, nodeOptions.Value.BitcoinNetwork.ChainHash,
                                              sendOptions.Value.ExpiryTooSoonExtraBlocks);
    }

    private enum OutcomeMatch
    {
        /// <summary>The event belongs to the payment's in-flight attempt.</summary>
        Match,

        /// <summary>The HTLC is not one of our payments (no payment for the hash, or another origin).</summary>
        NotOurs,

        /// <summary>A payment exists for the hash, but the event does not complete its in-flight attempt.</summary>
        Unmatched,

        /// <summary>The failure is one of the payment's, but another HTLC of the payment is still in flight.</summary>
        Pending
    }

    private enum OfferOutcome
    {
        Offered,
        Refused,
        Error
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Also when no block was processed yet (the final CLTV would be
    /// wrong). Nothing is persisted.</exception>
    public async Task<PaymentModel> PayInvoiceAsync(string bolt11, LightningMoney? amount, TimeSpan timeout,
                                                    CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");

        var result = await PayInvoiceAsync(bolt11, amount, new PayInvoiceOptions { Timeout = timeout },
                                           cancellationToken);
        return result.Payment;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Also when no block was processed yet (the final CLTV would be
    /// wrong). Nothing is persisted.</exception>
    public async Task<PayInvoiceResult> PayInvoiceAsync(string bolt11, LightningMoney? amount,
                                                        PayInvoiceOptions options,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bolt11);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout <= TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "The timeout must be positive.");
        if (options.MaxParts is < 1 or > PaymentSendOptions.MaxPartsLimit)
            throw new ArgumentOutOfRangeException(nameof(options),
                                                  $"The part limit must be 1 to {PaymentSendOptions.MaxPartsLimit}.");

        var target = DecodeInvoice(bolt11);
        var paymentAmount = ResolveAmount(target.Amount, amount);
        var ourNodeId = _secureKeyManager.GetNodePubKey();
        if (target.PayeeNodeId == ourNodeId)
            throw new ArgumentException("The invoice is ours; a node cannot pay itself.", nameof(bolt11));

        if (_blockchainMonitor.LastProcessedBlockHeight == 0)
            throw new InvalidOperationException("No block has been processed yet; cannot set the HTLC expiry.");

        var sendOptions = _sendOptions.Value;
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? deadline = options.Timeout == Timeout.InfiniteTimeSpan ? null : now + options.Timeout;
        var session = new PaymentSession(target, bolt11, paymentAmount,
                                         options.MaxFee ?? sendOptions.GetMaxFee(paymentAmount),
                                         options.MaxParts ?? Math.Clamp(sendOptions.MaxParts, 1,
                                                                        PaymentSendOptions.MaxPartsLimit),
                                         Math.Max(1, sendOptions.MaxAttempts), deadline, now);

        var paymentHash = target.PaymentHash;
        using (await AcquireHashLockAsync(paymentHash, cancellationToken))
        {
            await ThrowIfPaymentExistsAsync(paymentHash);
            _sessions[paymentHash] = session;
            try
            {
                await RunRoundsAsync(session);
            }
            catch
            {
                if (!session.HasPartsInFlight)
                    CompleteSession(session);
                throw;
            }
        }

        await WaitAsync(session.Completion.Task, options.Timeout, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            session.StopRequested = true;

        var payment = await GetPaymentAsync(paymentHash, CancellationToken.None)
                   ?? throw new InvalidOperationException($"Payment {paymentHash} was not stored.");
        return new PayInvoiceResult(payment, session.Attempts, session.MaxPartsInFlight);
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
            if (_sessions.TryGetValue(fulfilled.PaymentHash, out var session))
                return await HandleSessionFulfillAsync(session, fulfilled);

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
                RecordFulfillHoldTimes(payment, fulfilled, payment.Route);
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
            LogSucceeded(payment);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HandleOutgoingHtlcFailedAsync(OutgoingHtlcFailed failed,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failed);

        using (await AcquireHashLockAsync(failed.PaymentHash, cancellationToken))
        {
            if (_sessions.TryGetValue(failed.PaymentHash, out var session))
                return await HandleSessionFailureAsync(session, failed);

            using var scope = _serviceScopeFactory.CreateScope();
            var (payment, match) = await MatchOutcomeAsync(scope, failed.ChannelId, failed.HtlcId,
                                                           failed.PaymentHash, isFailure: true);
            if (match == OutcomeMatch.Pending)
            {
                _logger.LogInformation("HTLC {HtlcId} on channel {ChannelId} of payment {PaymentHash} failed; another "
                                     + "HTLC of the payment is still in flight", failed.HtlcId, failed.ChannelId,
                                       failed.PaymentHash);
                return true;
            }

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

            FailureCode? code = null;
            int? sourceIndex = null;
            string reason;
            if (payment.OutgoingChannelId == failed.ChannelId && payment.OutgoingHtlcId == failed.HtlcId)
            {
                (code, sourceIndex, reason, _, var attribution) = DescribeFailure(payment.Route, failed.Removal);
                reason += "; not retried.";
                payment.RecordHoldTimes(ToDurations(attribution));
            }
            else
            {
                reason = $"HTLC {failed.HtlcId} on channel {failed.ChannelId}, one part of the payment, failed; its "
                       + "route was not stored, so its error cannot be read; not retried.";
            }

            payment.Fail(code, sourceIndex, reason, _timeProvider.GetUtcNow());
            await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            LogFailed(payment);
        }

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
    /// Waits until no payment round queued on the thread pool is running (tests).
    /// </summary>
    internal async Task WhenRoundsIdleAsync()
    {
        while (!_backgroundRounds.IsEmpty)
            await Task.WhenAll(_backgroundRounds.Keys);
    }

    /// <summary>
    /// What an irrevocable failure means at the origin for a payment without a session (no retry): (BOLT 4 code,
    /// erring hop index, local description).
    /// </summary>
    internal (FailureCode? Code, int? SourceIndex, string Reason) InterpretFailure(PaymentModel payment,
                                                                                  HtlcRemoval removal)
    {
        var (code, sourceIndex, reason, _, _) = DescribeFailure(payment.Route, removal);
        return (code, sourceIndex, reason + "; not retried.");
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

    #region Rounds

    /// <summary>
    /// Plans, persists and offers rounds until the parts in flight carry the whole amount, the payment has to wait for
    /// them, or it is failed. Call it under the hash's lock.
    /// </summary>
    private async Task RunRoundsAsync(PaymentSession session)
    {
        while (!session.IsCompleted)
        {
            var remaining = session.RemainingMsat;
            if (remaining == 0)
                return;

            var stop = GetStopReason(session);
            if (stop is not null)
            {
                if (!session.HasPartsInFlight)
                    await FinishFailedAsync(session, stop);
                return;
            }

            var inFlight = session.InFlightParts.Count();
            var partsAllowed = Math.Min(session.MaxParts - inFlight, session.MaxAttempts - session.Attempts);
            if (partsAllowed <= 0)
                return;

            var feeLeft = session.MaxFee.MilliSatoshi > session.FeesInFlightMsat
                              ? session.MaxFee.MilliSatoshi - session.FeesInFlightMsat
                              : 0;
            var height = _blockchainMonitor.LastProcessedBlockHeight;
            var channels = await GetUsableChannelsAsync(CancellationToken.None);
            var request = new PaymentPlanRequest(session.Target, remaining, session.Amount.MilliSatoshi, feeLeft,
                                                 partsAllowed, height, _secureKeyManager.GetNodePubKey(),
                                                 channels.Select(ToCandidate).ToList(),
                                                 CreateLiquidityProbe(channels, height), session.Constraints,
                                                 _sendOptions.Value.MinPartMsat,
                                                 PaymentRoutePlanner.SumHintForwards(
                                                     session.InFlightParts.Select(p => p.Route)));
            if (!_planner.TryPlan(request, out var planned, out var noRouteReason))
            {
                if (session.HasPartsInFlight)
                {
                    _logger.LogInformation("Payment {PaymentHash}: {Remaining} msat cannot be sent again while other "
                                         + "parts are in flight ({Reason}); waiting for them", session.PaymentHash,
                                           remaining, noRouteReason);
                    return;
                }

                await FinishFailedAsync(session, noRouteReason);
                return;
            }

            var round = new List<(PaymentPart Part, OnionPacket Packet)>(planned.Count);
            foreach (var plannedPart in planned)
            {
                var onion = await _onionFactory.CreateAsync(plannedPart.Route);
                round.Add((new PaymentPart(plannedPart.Channel, plannedPart.Route,
                                           BuildHops(plannedPart.Route, onion.SharedSecrets,
                                                     plannedPart.Channel.ShortChannelId), plannedPart.Description),
                           onion.Packet));
            }

            await PersistRoundAsync(session, round.Select(r => r.Part).ToList());

            foreach (var (part, packet) in round)
            {
                session.Parts.Add(part);
                session.Attempts++;
                if (await OfferPartAsync(session, part, packet) == OfferOutcome.Error)
                    break;
            }

            // The engine refused the round's recorded part while others were offered: the row follows a live one
            await MovePrimaryPartAsync(session);

            session.MaxPartsInFlight = Math.Max(session.MaxPartsInFlight, session.InFlightParts.Count());
            if (round.Count > 1 && _logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Payment {PaymentHash}: split into {Parts} parts", session.PaymentHash,
                                       round.Count);
        }
    }

    private string? GetStopReason(PaymentSession session)
    {
        if (session.TerminalReason is { } terminal)
            return terminal;
        if (session.StopRequested)
            return "the caller stopped waiting";
        if (session.IsPastDeadline(_timeProvider.GetUtcNow()))
            return "the timeout passed";
        if (session.Attempts >= session.MaxAttempts)
            return $"{session.Attempts} HTLCs were tried, the limit";

        return null;
    }

    /// <summary>
    /// Stores the round before its offers: the first round creates the row; a round after every part failed replaces
    /// the (failed) row; a round while parts are in flight keeps it (its parts live in memory only).
    /// </summary>
    private async Task PersistRoundAsync(PaymentSession session, IReadOnlyList<PaymentPart> round)
    {
        if (session.RowCreated && session.HasPartsInFlight)
            return;

        var first = round[0];
        var fee = LightningMoney.MilliSatoshis(round.Aggregate(0UL, (sum, p) => sum + p.Route.Fee.MilliSatoshi));
        var row = new PaymentModel(session.PaymentHash, session.Bolt11, session.Target.PayeeNodeId, session.Amount,
                                   fee, session.CreatedAt, first.Hops);

        using var scope = _serviceScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
        if (session.RowCreated && await repository.GetByPaymentHashAsync(session.PaymentHash) is
            { Status: PaymentStatus.InFlight } stale)
        {
            // Every part failed: the stored attempt must be Failed before it can be replaced
            stale.Fail(session.LastFailure?.Code, session.LastFailure?.SourceIndex,
                       (session.LastFailure?.Reason ?? "The attempt failed") + " Retrying.",
                       _timeProvider.GetUtcNow());
            await repository.UpdateAsync(stale);
        }

        await repository.AddAsync(row);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        session.RowCreated = true;
        session.PrimaryPart = first;
    }

    private async Task<OfferOutcome> OfferPartAsync(PaymentSession session, PaymentPart part, OnionPacket packet)
    {
        var channelId = part.Channel.ChannelId;
        var route = part.Route;
        ulong htlcId;
        try
        {
            htlcId = await _channelOperations.OfferHtlcAsync(channelId, route.FirstHopAmount, session.PaymentHash,
                                                             route.FirstHopCltvExpiry, packet, null,
                                                             HtlcOrigin.Local(session.PaymentHash),
                                                             CancellationToken.None);
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            // Nothing was persisted or sent for the HTLC: plan without it. Only a refusal over the amount bounds the
            // channel; any other (link down, HTLCs disabled, channel failed or shutting down, data loss, HTLC count)
            // refuses every HTLC on it, so it is not tried again
            part.Status = PaymentPartStatus.Failed;
            if (e is CommitmentRefusedException { RequirementId: var rule } && s_liquidityRules.Contains(rule))
                session.Constraints.BoundLocalLiquidity(channelId, route.FirstHopAmount.MilliSatoshi);
            else
                session.Constraints.ExcludedLocalChannels.Add(channelId);

            session.LastFailure = (null, null, $"The HTLC could not be offered on channel {channelId}: {e.Message}");
            _logger.LogInformation("Payment {PaymentHash}: the HTLC of {Amount} msat on channel {ChannelId} was "
                                 + "refused ({Reason}); planning again", session.PaymentHash,
                                   route.FirstHopAmount.MilliSatoshi, channelId, e.Message);
            return OfferOutcome.Refused;
        }
        catch (Exception e)
        {
            // Unknown whether the add was persisted: channel memory tells
            _logger.LogError(e, "Offering the HTLC of payment {PaymentHash} on channel {ChannelId} failed; checking "
                              + "the channel state", session.PaymentHash, channelId);
            if (FindUnrecordedPartHtlc(session, part) is not { } found)
            {
                part.Status = PaymentPartStatus.Failed;
                var reason = $"The HTLC could not be offered on channel {channelId}: {e.Message}";
                session.TerminalReason = reason;
                session.LastFailure = (null, null, reason);
                return OfferOutcome.Error;
            }

            htlcId = found;
        }

        part.HtlcId = htlcId;
        if (ReferenceEquals(part, session.PrimaryPart))
            await RecordPrimaryHtlcAsync(session, part);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Paying {PaymentHash}: {Amount} of {Total} msat to {Payee} over {Hops} hop(s) ({Path}), fee {Fee} msat, "
              + "HTLC {HtlcId} on channel {ChannelId}", session.PaymentHash, route.Amount.MilliSatoshi,
                session.Amount.MilliSatoshi, session.Target.PayeeNodeId, route.Hops.Count, part.Description,
                route.Fee.MilliSatoshi, htlcId, channelId);
        return OfferOutcome.Offered;
    }

    private async Task RecordPrimaryHtlcAsync(PaymentSession session, PaymentPart part)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
            var row = await repository.GetByPaymentHashAsync(session.PaymentHash);
            if (row is not { Status: PaymentStatus.InFlight, OutgoingHtlcId: null })
                return;

            row.AddOutgoingHtlc(part.Channel.ChannelId, part.HtlcId!.Value);
            await repository.UpdateAsync(row);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        catch (Exception e)
        {
            // The HTLC is live: its outcome still matches the payment through the session or the channel state
            _logger.LogError(e, "Could not record HTLC {HtlcId} on channel {ChannelId} for payment {PaymentHash}; its "
                              + "outcome will be matched through the channel state", part.HtlcId,
                             part.Channel.ChannelId, session.PaymentHash);
        }
    }

    /// <summary>
    /// The HTLC of a part whose offer threw: a non-final outgoing HTLC on its channel with its hash, amount and expiry
    /// that no other part of the payment has.
    /// </summary>
    private ulong? FindUnrecordedPartHtlc(PaymentSession session, PaymentPart part)
    {
        if (!_channelMemoryRepository.TryGetChannel(part.Channel.ChannelId, out var channel)
         || channel.Commitments is not { } commitments)
            return null;

        foreach (var htlc in commitments.Htlcs.Values)
        {
            if (htlc.Direction == HtlcDirection.Outgoing && htlc.PaymentHash == session.PaymentHash
                                                         && !HtlcStateTable.IsFinal(htlc.State)
                                                         && htlc.AmountMsat == part.Route.FirstHopAmount.MilliSatoshi
                                                         && htlc.CltvExpiry == part.Route.FirstHopCltvExpiry
                                                         && session.FindPart(part.Channel.ChannelId, htlc.Id) is null)
                return htlc.Id;
        }

        return null;
    }

    /// <summary>
    /// Queues a round on the thread pool (so the switch that reported a failure is not held by our next offers).
    /// </summary>
    private void ScheduleRound(PaymentSession session)
    {
        if (session.RoundScheduled || session.IsCompleted)
            return;

        session.RoundScheduled = true;
        var round = Task.Run(async () =>
        {
            using (await AcquireHashLockAsync(session.PaymentHash, CancellationToken.None))
            {
                session.RoundScheduled = false;
                try
                {
                    await RunRoundsAsync(session);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "A retry round of payment {PaymentHash} failed", session.PaymentHash);
                    session.TerminalReason ??= $"A retry failed: {e.Message}";
                    if (!session.HasPartsInFlight)
                    {
                        try
                        {
                            await FinishFailedAsync(session, session.TerminalReason);
                        }
                        catch (Exception saveError)
                        {
                            _logger.LogError(saveError, "Could not store the failure of payment {PaymentHash}",
                                             session.PaymentHash);
                            CompleteSession(session);
                        }
                    }
                }
            }
        });
        _backgroundRounds.TryAdd(round, 0);
        round.ContinueWith(t => _backgroundRounds.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Stores the payment <c>Failed</c> with the last part's failure (or <paramref name="stopReason"/> when no part
    /// failed) and ends the session.
    /// </summary>
    private async Task FinishFailedAsync(PaymentSession session, string stopReason)
    {
        var now = _timeProvider.GetUtcNow();
        FailureCode? code = null;
        int? sourceIndex = null;
        var reason = stopReason;
        if (session.LastFailure is { } last)
        {
            (code, sourceIndex, _) = last;
            reason = session.Attempts > 1
                         ? $"{last.Reason} Gave up after {session.Attempts} HTLCs: {stopReason}."
                         : $"{last.Reason} Not retried: {stopReason}.";
        }

        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
            PaymentModel payment;
            if (!session.RowCreated)
            {
                payment = new PaymentModel(session.PaymentHash, session.Bolt11, session.Target.PayeeNodeId,
                                           session.Amount, LightningMoney.Zero, session.CreatedAt);
                payment.Fail(code, sourceIndex, reason, now);
                await repository.AddAsync(payment);
                session.RowCreated = true;
            }
            else
            {
                var stored = await repository.GetByPaymentHashAsync(session.PaymentHash)
                          ?? throw new InvalidOperationException($"Payment {session.PaymentHash} is not stored.");
                if (stored.Status == PaymentStatus.Succeeded)
                {
                    CompleteSession(session);
                    return;
                }

                if (stored.Status == PaymentStatus.InFlight)
                {
                    RecordLastFailureHoldTimes(stored, session);
                    stored.Fail(code, sourceIndex, reason, now);
                    payment = stored;
                }
                else
                {
                    payment = PaymentModel.Restore(stored.PaymentHash, stored.Bolt11, stored.PayeeNodeId,
                                                   stored.Amount, stored.Fee, stored.CreatedAt, PaymentStatus.Failed,
                                                   stored.OutgoingChannelId, stored.OutgoingHtlcId, null, code,
                                                   sourceIndex, reason, now, stored.Route);
                }

                await repository.UpdateAsync(payment);
            }

            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            LogFailed(payment);
        }

        CompleteSession(session);
    }

    /// <summary>
    /// Stores the payment <c>Failed</c> while every part failed and a retry round is queued, so no crash leaves it
    /// <c>InFlight</c> without an HTLC; the round replaces it.
    /// </summary>
    private async Task SaveAttemptFailedAsync(PaymentSession session)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
        if (await repository.GetByPaymentHashAsync(session.PaymentHash) is not { Status: PaymentStatus.InFlight } row)
            return;

        var last = session.LastFailure;
        RecordLastFailureHoldTimes(row, session);
        row.Fail(last?.Code, last?.SourceIndex, (last?.Reason ?? "The attempt failed.") + " Retrying.",
                 _timeProvider.GetUtcNow());
        await repository.UpdateAsync(row);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>
    /// When the part the row records is no longer in flight (the engine refused it, or it failed) while other offered
    /// parts are, rewrites the row to one of those: its route and shared secrets, its HTLC, and the fees of the parts
    /// in flight. A failed save is logged: the row then keeps the old part (its outcomes still reach the payment
    /// through the session, or after a restart through the HTLCs' <c>Local</c> origin).
    /// </summary>
    private async Task MovePrimaryPartAsync(PaymentSession session)
    {
        if (session.PrimaryPart is { Status: PaymentPartStatus.InFlight, HtlcId: not null } || !session.RowCreated)
            return;
        if (session.InFlightParts.FirstOrDefault(p => p.HtlcId is not null) is not { } next)
            return;

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
            if (await repository.GetByPaymentHashAsync(session.PaymentHash) is not { Status: PaymentStatus.InFlight }
                stored)
                return;

            var row = PaymentModel.Restore(stored.PaymentHash, stored.Bolt11, stored.PayeeNodeId, stored.Amount,
                                           LightningMoney.MilliSatoshis(session.FeesInFlightMsat), stored.CreatedAt,
                                           PaymentStatus.InFlight, next.Channel.ChannelId, next.HtlcId!.Value, null,
                                           null, null, null, null, next.Hops);
            await StageReplacementAsync(repository, stored, row, "Superseded by another part in flight.");
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            session.PrimaryPart = next;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not record HTLC {HtlcId} on channel {ChannelId} as the part of payment "
                              + "{PaymentHash}", next.HtlcId, next.Channel.ChannelId, session.PaymentHash);
        }
    }

    /// <summary>
    /// Stages <paramref name="replacement"/> over the stored row (not saved): only a failed row can be replaced
    /// (<see cref="IPaymentDbRepository.AddAsync"/>), so an in-flight one is marked failed first, in the same unit of
    /// work (the save writes only the replacement). The route and fee of a row change only this way.
    /// </summary>
    private async Task StageReplacementAsync(IPaymentDbRepository repository, PaymentModel stored,
                                             PaymentModel replacement, string reason)
    {
        if (stored.Status == PaymentStatus.InFlight)
        {
            stored.Fail(null, null, reason, _timeProvider.GetUtcNow());
            await repository.UpdateAsync(stored);
        }

        await repository.AddAsync(replacement);
    }

    private void CompleteSession(PaymentSession session)
    {
        _sessions.TryRemove(KeyValuePair.Create(session.PaymentHash, session));
        session.Completion.TrySetResult();
    }

    #endregion

    #region Session outcomes

    private async Task<bool> HandleSessionFulfillAsync(PaymentSession session, OutgoingHtlcFulfilled fulfilled)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var part = session.FindPart(fulfilled.ChannelId, fulfilled.HtlcId);
        if (part is null)
        {
            var origin = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelStateDbRepository
                                    .GetHtlcOriginAsync(fulfilled.ChannelId,
                                                        new HtlcKey(HtlcDirection.Outgoing, fulfilled.HtlcId));
            if (origin is { } stored && stored != HtlcOrigin.Local(fulfilled.PaymentHash))
                return false;
        }

        var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
        var payment = await repository.GetByPaymentHashAsync(fulfilled.PaymentHash);
        if (payment is null || payment.Status == PaymentStatus.Succeeded)
        {
            CompleteSession(session);
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        // The parts in flight are the ones the payee settles: the row's fee is theirs, and its route the fulfilled
        // part's (the parts of earlier rounds that failed are not paid for)
        var settledFee = LightningMoney.MilliSatoshis(session.FeesInFlightMsat);
        if (part is { Status: PaymentPartStatus.InFlight }
         && (settledFee != payment.Fee || !ReferenceEquals(part, session.PrimaryPart)
                                       || payment.OutgoingHtlcId != part.HtlcId))
        {
            var succeeded = PaymentModel.Restore(payment.PaymentHash, payment.Bolt11, payment.PayeeNodeId,
                                                 payment.Amount, settledFee, payment.CreatedAt,
                                                 PaymentStatus.Succeeded, fulfilled.ChannelId, fulfilled.HtlcId,
                                                 fulfilled.PaymentPreimage, null, null, null, now, part.Hops);
            RecordFulfillHoldTimes(succeeded, fulfilled, part.Hops);
            await StageReplacementAsync(repository, payment, succeeded, "Superseded by the fulfilled part.");
            payment = succeeded;
        }
        else
        {
            if (payment.Status == PaymentStatus.InFlight)
            {
                if (payment.OutgoingHtlcId is null)
                    payment.AddOutgoingHtlc(fulfilled.ChannelId, fulfilled.HtlcId);
                payment.Succeed(fulfilled.PaymentPreimage, now);
                RecordFulfillHoldTimes(payment, fulfilled, part?.Hops ?? payment.Route);
            }
            else
            {
                payment = WithPreimage(payment, fulfilled, now);
            }

            await repository.UpdateAsync(payment);
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        part?.Status = PaymentPartStatus.Succeeded;
        LogSucceeded(payment);
        CompleteSession(session);
        return true;
    }

    private async Task<bool> HandleSessionFailureAsync(PaymentSession session, OutgoingHtlcFailed failed)
    {
        var part = session.FindPart(failed.ChannelId, failed.HtlcId);
        if (part is not { Status: PaymentPartStatus.InFlight })
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("The failure of HTLC {HtlcId} on channel {ChannelId} ({PaymentHash}) is not one of the "
                               + "payment's parts in flight", failed.HtlcId, failed.ChannelId, failed.PaymentHash);
            return false;
        }

        part.Status = PaymentPartStatus.Failed;
        var (code, sourceIndex, reason, interpretation, attribution) = DescribeFailure(part.Hops, failed.Removal);
        var (retry, note) = _retryPolicy.Decide(part, failed.Removal.Kind, interpretation, session.Constraints);
        session.LastFailure = (code, sourceIndex, $"{reason} ({note}).");
        session.LastFailureHoldTimes = attribution.IsPresent
                                           ? (failed.ChannelId, failed.HtlcId, ToDurations(attribution))
                                           : null;
        if (!retry)
            session.TerminalReason ??= note;

        _logger.LogWarning("Payment {PaymentHash}: the part of {Amount} msat over {Path} failed: {Reason} ({Note})",
                           session.PaymentHash, part.Route.Amount.MilliSatoshi, part.Description, reason, note);

        if (session.HasPartsInFlight)
        {
            // The row must not keep the route and HTLC of a part that is gone while others are live
            await MovePrimaryPartAsync(session);
            if (retry)
                ScheduleRound(session);
            return true;
        }

        if (GetStopReason(session) is { } stop)
        {
            await FinishFailedAsync(session, stop);
            return true;
        }

        await SaveAttemptFailedAsync(session);
        ScheduleRound(session);
        return true;
    }

    /// <summary>
    /// What an irrevocable failure of an HTLC sent along <paramref name="route"/> means at the origin: (BOLT 4 code,
    /// erring hop index, local description, the interpreted error onion when one was read, what its
    /// <c>attribution_data</c> said).
    /// </summary>
    /// <remarks>
    /// With <c>attribution_data</c> (BOLT 4 attributable failures, NL-326) the return packet is decrypted by
    /// <see cref="IAttributionDataService.DecryptErrorPacket"/>, which also verifies each hop's HMACs up to the erring
    /// hop and yields their hold times. When no hop authenticated the return packet, the first hop whose attribution
    /// HMAC failed is blamed (the source index; it shares the blame with its upstream neighbour).
    /// </remarks>
    private (FailureCode? Code, int? SourceIndex, string Reason, FailureInterpretation? Interpretation,
        AttributionVerification Attribution) DescribeFailure(IReadOnlyList<PaymentHop> route, HtlcRemoval removal)
    {
        var absent = AttributionVerification.Absent;
        if (removal.Kind == HtlcRemovalKind.FailMalformed)
        {
            // BOLT 4: our peer could not parse the onion we built (it is the only hop that can send this to us)
            var malformed = (FailureCode)removal.FailureCode;
            return (malformed, 0, $"Our peer {DescribeHop(route, 0)} rejected the onion as malformed "
                                + $"({malformed}, 0x{removal.FailureCode:X4})", null, absent);
        }

        if (removal.Kind == HtlcRemovalKind.OnchainTimeout)
        {
            // BOLT 5 plan O3-T4: our HTLC was timed out on chain (the channel to our peer was force closed); no hop
            // sent an error, we are the erring node
            return (FailureCode.PermanentChannelFailure, null,
                    $"The channel to our peer {DescribeHop(route, 0)} was closed on chain and the HTLC timed out "
                  + "there (permanent_channel_failure)", null, absent);
        }

        if (route.Count == 0)
            return (null, null, "The HTLC failed and the route's shared secrets were not recorded; the error onion "
                              + "cannot be read", null, absent);

        var secrets = route.Select(h => h.SharedSecret).ToList();
        DecryptedFailure? decrypted;
        var attribution = absent;
        if (!removal.AttributionData.IsEmpty && _attributionDataService is not null)
        {
            var attributed = _attributionDataService.DecryptErrorPacket(secrets, removal.Reason.Span,
                                                                        removal.AttributionData.Span);
            decrypted = attributed.Failure;
            attribution = attributed.Attribution;
        }
        else
        {
            decrypted = _failureOnionService.DecryptErrorPacket(secrets, removal.Reason.Span);
        }

        var interpretation = FailureInterpreter.Interpret(decrypted, route.Count);
        if (!interpretation.IsAttributed)
        {
            if (attribution.InvalidHopIndex is { } blamed)
                return (null, blamed,
                        "The HTLC failed with an error onion no hop of the route authenticated; its attribution_data "
                      + $"blames hop {blamed} ({DescribeHop(route, blamed)}) or its upstream neighbour"
                      + DescribeHoldTimes(attribution), interpretation, attribution);

            return (null, null, "The HTLC failed with an error onion no hop of the route authenticated",
                    interpretation, attribution);
        }

        var index = interpretation.ErringHopIndex!.Value;
        var codeText = interpretation.Code is { } failureCode
                           ? $"{failureCode} (0x{(ushort)failureCode:X4})"
                           : "an unreadable failure";
        var role = interpretation.IsFinalNode ? "the payee" : "hop";
        var detail = interpretation.IsFinalNode
                         ? interpretation.IsPermanent ? "permanent" : "final node"
                         : interpretation.IsNodeFailure ? "node failure" : "channel failure";
        var tampered = attribution.InvalidHopIndex is { } invalid
                           ? $"; the attribution_data of hop {invalid} ({DescribeHop(route, invalid)}) did not verify"
                           : "";
        return (interpretation.Code, index,
                $"{codeText} from {role} {index} ({DescribeHop(route, index)}), {detail}{tampered}"
              + DescribeHoldTimes(attribution), interpretation, attribution);
    }

    /// <summary>
    /// Records on <paramref name="payment"/> the hold times of a fulfill's verified <c>attribution_data</c>
    /// (<see cref="IAttributionDataService.VerifyFulfillment"/> over the hops' shared secrets of
    /// <paramref name="route"/>, the fulfilled part's); nothing without attribution, a route or the service. The hold
    /// times are written by index onto the payment's stored route, so they are recorded only when that route is the
    /// fulfilled part's (<see cref="IsSameRoute"/>): another part's or round's route would pair them with the wrong
    /// nodes.
    /// </summary>
    private void RecordFulfillHoldTimes(PaymentModel payment, OutgoingHtlcFulfilled fulfilled,
                                        IReadOnlyList<PaymentHop> route)
    {
        if (_attributionDataService is null || fulfilled.AttributionData.IsEmpty || route.Count == 0)
            return;

        var verified = _attributionDataService.VerifyFulfillment(route.Select(h => h.SharedSecret).ToList(),
                                                                 fulfilled.AttributionData.Span,
                                                                 fulfilled.FulfillmentPayload.Span);
        if (IsSameRoute(payment.Route, route))
            payment.RecordHoldTimes(ToDurations(verified.Attribution));
        else
            _logger.LogInformation("Payment {PaymentHash}: the fulfilled HTLC {HtlcId} on channel {ChannelId} is not "
                                 + "the stored route's part; its hold times{HoldTimes} are not recorded",
                                   payment.PaymentHash, fulfilled.HtlcId, fulfilled.ChannelId,
                                   DescribeHoldTimes(verified.Attribution));
        if (verified.Attribution.InvalidHopIndex is { } invalid)
            _logger.LogWarning("Payment {PaymentHash}: the fulfill's attribution_data of hop {HopIndex} ({Node}) did not "
                             + "verify", payment.PaymentHash, invalid, DescribeHop(route, invalid));
        else if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Payment {PaymentHash}: hold times{HoldTimes}", payment.PaymentHash,
                             DescribeHoldTimes(verified.Attribution));
    }

    /// <summary>
    /// Records the hold times of the session's last failure on <paramref name="row"/> when the row records that
    /// failure's HTLC.
    /// </summary>
    private static void RecordLastFailureHoldTimes(PaymentModel row, PaymentSession session)
    {
        if (session.LastFailureHoldTimes is { } holdTimes && row.OutgoingChannelId == holdTimes.ChannelId
                                                         && row.OutgoingHtlcId == holdTimes.HtlcId)
            row.RecordHoldTimes(holdTimes.HoldTimes);
    }

    /// <summary>
    /// Whether two routes are the same part's: the same hops (node and channel) with the same Sphinx shared secrets
    /// (unique per onion, so another part or round never matches). Hold times are ignored.
    /// </summary>
    internal static bool IsSameRoute(IReadOnlyList<PaymentHop> stored, IReadOnlyList<PaymentHop> part)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(part);

        if (stored.Count != part.Count)
            return false;

        for (var i = 0; i < stored.Count; i++)
        {
            if (stored[i].NodeId != part[i].NodeId || stored[i].ShortChannelId != part[i].ShortChannelId
                                                   || stored[i].SharedSecret != part[i].SharedSecret)
                return false;
        }

        return true;
    }

    private static List<TimeSpan> ToDurations(AttributionVerification attribution) =>
        attribution.HoldTimes.Select(AttributionHoldTime.ToDuration).ToList();

    private static string DescribeHoldTimes(AttributionVerification attribution) =>
        attribution.IsPresent && attribution.HoldTimes.Count > 0
            ? $"; hold times {string.Join(", ", attribution.HoldTimes.Select(h => $"{AttributionHoldTime.ToDuration(h).TotalMilliseconds} ms"))}"
            : "";

    #endregion

    private static string DescribeHop(IReadOnlyList<PaymentHop> route, int index) =>
        index < route.Count ? route[index].NodeId.ToString() : "unknown node";

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
    /// Refuses a hash whose payment is in flight or succeeded, or still being paid by a call of this process. Call it
    /// under the hash's lock: an in-flight payment without an HTLC id is reconciled first (no offer of this node is
    /// running for the hash then).
    /// </summary>
    private async Task ThrowIfPaymentExistsAsync(Hash paymentHash)
    {
        if (_sessions.ContainsKey(paymentHash))
            throw new InvalidOperationException(
                $"A payment for payment hash {paymentHash} is already {PaymentStatus.InFlight} (being retried).");

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
            if (_sessions.TryGetValue(payment.PaymentHash, out var session) && !session.HasPartsInFlight)
                CompleteSession(session);
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
    /// Our usable channels: <c>Open</c>, with a commitment snapshot and the link up.
    /// </summary>
    private async Task<List<ChannelModel>> GetUsableChannelsAsync(CancellationToken cancellationToken)
    {
        var usable = new List<ChannelModel>();
        var channels = _channelMemoryRepository.FindChannels(c => c is { State: ChannelState.Open, Commitments: not null });
        foreach (var channel in channels)
        {
            if (await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId, cancellationToken))
                usable.Add(channel);
        }

        return usable;
    }

    private static LocalChannelCandidate ToCandidate(ChannelModel channel) =>
        new(channel.ChannelId, channel.RemoteNodeId, channel.ShortChannelId);

    /// <summary>
    /// What each usable channel can send (the engine's dry run), cached for the no-HTLC-planned case.
    /// </summary>
    private static Func<ChannelId, IReadOnlyList<ulong>, ulong> CreateLiquidityProbe(List<ChannelModel> channels,
                                                                                    uint height)
    {
        var byId = channels.ToDictionary(c => c.ChannelId);
        var cache = new Dictionary<ChannelId, ulong>();
        var cltvExpiry = checked(height + 144);
        return (channelId, planned) =>
        {
            if (!byId.TryGetValue(channelId, out var channel) || channel.Commitments is not { } commitments)
                return 0;
            if (planned.Count > 0)
                return LocalLiquidityEstimator.MaxSendableMsat(commitments, planned, cltvExpiry);
            if (!cache.TryGetValue(channelId, out var sendable))
                cache[channelId] = sendable = LocalLiquidityEstimator.MaxSendableMsat(commitments, [], cltvExpiry);
            return sendable;
        };
    }

    /// <summary>
    /// The persisted route: hop <c>i</c> is the node that peels layer <c>i</c>, with the HTLC it receives (ours for hop
    /// 0, then what the previous hop forwards) and the channel that reaches it.
    /// </summary>
    private static List<PaymentHop> BuildHops(PaymentRoute route, IReadOnlyList<Secret> sharedSecrets,
                                              ShortChannelId firstChannel)
    {
        var hops = new List<PaymentHop>(route.Hops.Count);
        for (var i = 0; i < route.Hops.Count; i++)
        {
            var previous = i == 0 ? null : route.Hops[i - 1];
            hops.Add(new PaymentHop(route.Hops[i].NodeId, previous?.OutgoingShortChannelId ?? firstChannel,
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

        // Every attempt and every part of a hash has the same origin: a failure replayed from an earlier attempt, or
        // one part of a split payment, must not fail the payment while another of its HTLCs is still live
        var otherLive = isFailure && FindAttemptHtlcs(payment, matchFirstHop: false)
                           .Any(h => h.ChannelId != channelId || h.HtlcId != htlcId);

        if (payment.OutgoingHtlcId is { } recordedId)
        {
            if (payment.OutgoingChannelId == channelId && recordedId == htlcId)
                return (payment, otherLive ? OutcomeMatch.Pending : OutcomeMatch.Match);

            // Another part of a split payment (its origin says it is ours), or an HTLC we know nothing of
            if (isFailure && origin is not null)
                return (payment, otherLive ? OutcomeMatch.Pending : OutcomeMatch.Match);

            return (payment, OutcomeMatch.Unmatched);
        }

        // No id recorded (a crash or a failed save around the offer). Origins are not stored before NL-250, so the
        // HTLC's record, while channel memory still has it, must match the attempt's first hop
        if (payment.Route.Count > 0 && _channelMemoryRepository.TryGetChannel(channelId, out var channel)
                                    && channel.Commitments?.GetHtlc(HtlcDirection.Outgoing, htlcId) is { } htlc
                                    && !MatchesFirstHop(payment.Route[0], channel, htlc))
            return (payment, OutcomeMatch.Unmatched);

        if (otherLive)
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

    private static async Task WaitAsync(Task outcome, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await outcome.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // The payment stays InFlight; its HTLCs resolve later
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop waiting, as with the timeout
        }
    }

    private void LogSucceeded(PaymentModel payment)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Payment {PaymentHash} succeeded ({Amount} msat, fee {Fee} msat)",
                                   payment.PaymentHash, payment.Amount.MilliSatoshi, payment.Fee.MilliSatoshi);
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