using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Send;

using Blinded;
using Bolt11.Exceptions;
using Bolt11.Models;
using Channels.Interfaces;
using Domain.Accounting.Constants;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Interfaces;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Interpreters;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.Tlv;
using Domain.Routing.Pathfinding;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Keysend;
using Routing;
using Routing.Interfaces;

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
/// without an HTLC. Every offered part is additionally stored as a row of its own (<see cref="IPaymentPartDbRepository"/>,
/// NL-321: route, shared secrets, HTLC id, state), so a part that is not the one the row records can still be resolved
/// and its error onion decrypted after a restart; a retry that replaces the row clears the attempt's part rows. The row
/// always records a part that was offered: when the recorded part is refused by the engine or fails while other parts
/// are in flight, the row is rewritten to one of those (route, shared secrets, HTLC id, the fees in flight). On success
/// the row holds the fulfilled part's route and HTLC and, as its fee, the fees of the parts in flight at the fulfill
/// (the ones the payee settles; failed parts cost nothing). The route and fee change only by replacing the row
/// (<c>AddAsync</c> over the row failed in the same save).</para>
/// <para>Outcome without a session (after a restart, or a hash this process never paid): by the recorded
/// (channel, HTLC id); when no id is recorded, the HTLC must not carry another origin
/// (<c>IChannelStateDbRepository.GetHtlcOriginAsync</c>), its record in channel memory (when still there) must match
/// the stored first hop (peer, amount, CLTV expiry). A failure is applied only when no other non-final outgoing HTLC
/// carries the hash (else it may be an earlier attempt's, replayed on startup, or one part of a split payment whose
/// other parts are live); an HTLC whose stored origin is <c>Local(hash)</c> but that is not the recorded one (another
/// part) then fails the payment without a code. A fulfill whose preimage is right but that matches no in-flight
/// attempt is logged at Error and still recorded: the preimage proves the payment.</para>
/// <para>Reconciliation (<see cref="ReconcileInFlightPaymentsAsync"/> at startup, after the channels are registered in
/// memory, and lazily when a hash is paid again): the stored parts of an <c>InFlight</c> payment are settled first
/// (NL-321) — a part whose HTLC is gone from its channel while the node was down is marked <c>Failed</c> with an
/// unknown outcome, and a payment whose parts all died that way (and whose recorded HTLC is gone too, with no part on
/// a channel that is not in memory) is failed without a code. An <c>InFlight</c> payment without an HTLC id is then
/// attached to its HTLC when exactly one non-final outgoing HTLC in channel memory matches its first hop (or carries
/// its stored <c>HtlcOrigin.Local</c>), failed without a code when none does and no HTLC with its origin sits on a
/// channel that is not in memory, and left alone otherwise.</para>
/// <para>Singleton; thread-safe. The lock is per payment hash (refcounted), so a slow payment never delays the outcome
/// of another hash. Persistence goes through a fresh DI scope per step (scoped <see cref="IPaymentDbRepository"/>
/// sharing the scope's <see cref="IUnitOfWork"/>).</para>
/// </remarks>
public sealed class PaymentService : IPaymentService, IPaymentOutcomeHandler, IRouteQueryService
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
    private readonly GraphPathSource? _graphPathSource;
    private readonly IRouteBlindingService? _routeBlindingService;

    /// <summary>
    /// The engine's sender rules (<c>UpdateValidator.ValidateSendAdd</c>) that a smaller HTLC on the same channel may
    /// pass: our balance above the reserve and the commitment fees (B2-ADD-S01..S04), the peer's
    /// <c>max_htlc_value_in_flight_msat</c> (B2-ADD-S09) and the dust exposure (B2-DUST-03/04).
    /// </summary>
    private static readonly HashSet<string> s_liquidityRules =
        ["B2-ADD-S01", "B2-ADD-S02", "B2-ADD-S03", "B2-ADD-S04", "B2-ADD-S09", "B2-DUST-03", "B2-DUST-04"];

    /// <summary>
    /// BOLT 11's <c>min_final_cltv_expiry_delta</c> when an invoice has no <c>c</c> field (the <c>getroute</c> default).
    /// </summary>
    private const ushort DefaultFinalCltvDelta = 18;

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
                          IAttributionDataService? attributionDataService = null,
                          GraphPathSource? graphPathSource = null, IGossipScidRefresher? scidRefresher = null,
                          IRouteBlindingService? routeBlindingService = null)
    {
        _routeBlindingService = routeBlindingService;
        _attributionDataService = attributionDataService;
        _graphPathSource = graphPathSource;
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
                                              sendOptions.Value.ExpiryTooSoonExtraBlocks,
                                              graphPathSource?.MissionControl, scidRefresher);
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

        var invoice = DecodeInvoice(bolt11);

        // bLIP 39 (NL-440): an invoice with blinded paths names its recipient only through them (it is signed by an
        // ephemeral key and carries no payment secret), so it is paid over the paths
        if (invoice.BlindedPaymentPaths.Count > 0)
            return await PayBlindedInvoiceAsync(invoice, bolt11, amount, options, cancellationToken);

        var target = PaymentTarget.FromInvoice(invoice);
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

        return await RunSessionAsync(session, options.Timeout, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Rounds as for an invoice (<see cref="RunRoundsAsync"/>), each planned over the usable paths
    /// (<see cref="TryPlanBlinded"/>): the cheapest path that carries the whole amount, else, only with
    /// <see cref="PayBlindedRequest.AllowMpp"/> (BOLT 12 "Invoices" reader: <c>basic_mpp</c> in
    /// <c>invoice_features</c>), several parts over the paths with <c>total_amount_msat</c> = the amount. The stored
    /// payee is <see cref="PayBlindedRequest.PayeeNodeId"/> (a BOLT 12 <c>invoice_node_id</c>) or, without it, the
    /// first path's last blinded node id; the stored route's last hop names that payee. A path whose introduction node
    /// is this node is paid from its next hop (BOLT 12 plan B12-PAY-02): our own <c>encrypted_recipient_data</c> is
    /// decrypted and the HTLC goes to the next node with the next path_key in <c>update_add_htlc</c>.
    /// <see cref="PayBlindedRequest.Bolt12"/> is stored with every row of the payment.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Also when no block was processed yet. Nothing is persisted.
    /// </exception>
    public async Task<PayInvoiceResult> PayBlindedAsync(PayBlindedRequest request, PayInvoiceOptions options,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request.Amount);
        if (request.Amount.IsZero)
            throw new ArgumentException("The amount must be positive.", nameof(request));
        if (request.Paths is not { Count: > 0 } paths || paths.Any(p => p?.Path is null || p.PayInfo is null))
            throw new ArgumentException("At least one blinded path with its pay info is required.", nameof(request));
        if (paths.Any(p => p.Path.Hops.Count == 0))
            throw new ArgumentException("A blinded path has no hop.", nameof(request));
        if (options.Timeout <= TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "The timeout must be positive.");
        if (options.MaxParts is < 1 or > PaymentSendOptions.MaxPartsLimit)
            throw new ArgumentOutOfRangeException(nameof(options),
                                                  $"The part limit must be 1 to {PaymentSendOptions.MaxPartsLimit}.");
        if (request.PayeeNodeId is { } payeeNodeId && payeeNodeId == _secureKeyManager.GetNodePubKey())
            throw new ArgumentException("The payee is this node; a node cannot pay itself.", nameof(request));
        if (_blockchainMonitor.LastProcessedBlockHeight == 0)
            throw new InvalidOperationException("No block has been processed yet; cannot set the HTLC expiry.");

        // The recipient hides behind the path: without its real id, its last blinded node id stands for it
        var target = new PaymentTarget(request.PayeeNodeId ?? paths[0].Path.Hops[^1].BlindedNodeId,
                                       request.PaymentHash, new Secret(new byte[32]), request.Amount, 0, [],
                                       SupportsMpp: request.AllowMpp);
        var sendOptions = _sendOptions.Value;
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? deadline = options.Timeout == Timeout.InfiniteTimeSpan ? null : now + options.Timeout;
        var maxParts = request.AllowMpp
                           ? options.MaxParts ?? Math.Clamp(sendOptions.MaxParts, 1, PaymentSendOptions.MaxPartsLimit)
                           : 1;
        var session = new PaymentSession(target, request.Invoice, request.Amount,
                                         options.MaxFee ?? sendOptions.GetMaxFee(request.Amount), maxParts,
                                         Math.Max(1, sendOptions.MaxAttempts), deadline, now)
        {
            BlindedPaths = paths,
            Bolt12 = request.Bolt12
        };

        return await RunSessionAsync(session, options.Timeout, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The target is the destination without route hints or payment secret; the round planner then finds a direct
    /// channel or a graph route as for an invoice. The final CLTV delta is <c>Node:Keysend:FinalCltvExpiryDelta</c>
    /// (<see cref="KeysendOptions.FinalCltvExpiryDelta"/>). One part at a time (<see cref="PayInvoiceOptions.MaxParts"/>
    /// is ignored). The preimage stays in the session (a fulfill proves it, and the stored row gets it then).
    /// </remarks>
    /// <exception cref="InvalidOperationException">Also when no block was processed yet. Nothing is persisted.
    /// </exception>
    public async Task<PayInvoiceResult> PayKeysendAsync(PayKeysendRequest request, PayInvoiceOptions options,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request.Amount);
        if (request.Amount.IsZero)
            throw new ArgumentException("The amount must be positive.", nameof(request));
        if (options.Timeout <= TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "The timeout must be positive.");
        if (request.Destination == _secureKeyManager.GetNodePubKey())
            throw new ArgumentException("The destination is this node; a node cannot pay itself.", nameof(request));

        var customRecords = CustomRecordCodec.Validate(request.CustomRecords);
        if (_blockchainMonitor.LastProcessedBlockHeight == 0)
            throw new InvalidOperationException("No block has been processed yet; cannot set the HTLC expiry.");

        // Our preimage (CSPRNG) and its hash; the payee learns the preimage from the onion and reveals it to settle
        var preimageBytes = RandomNumberGenerator.GetBytes(32);
        var preimage = new Secret(preimageBytes);
        var paymentHash = new Hash(SHA256.HashData(preimageBytes));

        var keysendOptions = _nodeOptions.Value.Keysend;
        var keysend = new KeysendFinalRecords(preimage, customRecords);

        // The payee's layer alone must fit the onion (the least any route takes): refuse oversized records at once
        var finalLayer = await _onionFactory.GetKeysendFinalFramedLengthAsync(
                             request.Amount,
                             _blockchainMonitor.LastProcessedBlockHeight + keysendOptions.FinalCltvExpiryDelta,
                             keysend);
        if (finalLayer > OnionConstants.HopPayloadsLength)
            throw new ArgumentException($"The custom records do not fit the onion: the payee's layer takes {finalLayer} "
                                      + $"of {OnionConstants.HopPayloadsLength} bytes.", nameof(request));

        var target = new PaymentTarget(request.Destination, paymentHash, new Secret(new byte[32]), request.Amount,
                                       keysendOptions.FinalCltvExpiryDelta, [], SupportsMpp: false);
        var sendOptions = _sendOptions.Value;
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? deadline = options.Timeout == Timeout.InfiniteTimeSpan ? null : now + options.Timeout;
        var session = new PaymentSession(target, null, request.Amount,
                                         options.MaxFee ?? sendOptions.GetMaxFee(request.Amount), 1,
                                         Math.Max(1, sendOptions.MaxAttempts), deadline, now)
        {
            Keysend = keysend
        };

        return await RunSessionAsync(session, options.Timeout, cancellationToken);
    }

    private async Task<PayInvoiceResult> RunSessionAsync(PaymentSession session, TimeSpan timeout,
                                                         CancellationToken cancellationToken)
    {
        var paymentHash = session.PaymentHash;
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

        await WaitAsync(session.Completion.Task, timeout, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            session.StopRequested = true;

        var payment = await GetPaymentAsync(paymentHash, CancellationToken.None)
                   ?? throw new InvalidOperationException($"Payment {paymentHash} was not stored.");
        return new PayInvoiceResult(payment, session.Attempts, session.MaxPartsInFlight);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Planned by <see cref="PaymentRoutePlanner"/> as a payment's first round with one part (no shadow CLTV offset, so
    /// the answer is stable), over the same usable channels and the same graph; nothing is stored or sent.
    /// </remarks>
    public async Task<RouteQuote> QuoteRouteAsync(CompactPubKey payee, LightningMoney amount, LightningMoney? maxFee,
                                                  ushort? finalCltvDelta,
                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(amount);
        if (amount.IsZero)
            throw new ArgumentException("The amount must be positive.", nameof(amount));
        var ourNodeId = _secureKeyManager.GetNodePubKey();
        if (payee == ourNodeId)
            throw new ArgumentException("The destination is this node.", nameof(payee));

        var height = _blockchainMonitor.LastProcessedBlockHeight;
        if (height == 0)
            throw new InvalidOperationException("No block has been processed yet; cannot set the HTLC expiry.");

        var channels = await GetUsableChannelsAsync(cancellationToken);
        var graph = _graphPathSource?.CreateContext(0);
        var target = new PaymentTarget(payee, new Hash(new byte[32]), new Secret(new byte[32]), amount,
                                       finalCltvDelta ?? DefaultFinalCltvDelta, []);
        var request = new PaymentPlanRequest(target, amount.MilliSatoshi, amount.MilliSatoshi,
                                             (maxFee ?? _sendOptions.Value.GetMaxFee(amount)).MilliSatoshi, 1, height,
                                             ourNodeId, channels.Select(ToCandidate).ToList(),
                                             CreateLiquidityProbe(channels, height), new RouteConstraints(),
                                             _sendOptions.Value.MinPartMsat, null, graph);
        if (!_planner.TryPlan(request, out var planned, out var reason))
            throw new InvalidOperationException(reason);

        var part = planned[0];
        return new RouteQuote(part.Route, part.Channel, EstimateProbability(part.Route, graph), height,
                              part.Description);
    }

    /// <summary>
    /// The product of the success probabilities of the channels after our first one (see <see cref="RouteQuote"/>).
    /// </summary>
    private static double EstimateProbability(PaymentRoute route, GraphRoutingContext? graph)
    {
        var aprioriProbability = (graph?.CostModel ?? PathCostModel.Default).AprioriProbability;
        var probability = 1.0;
        for (var i = 0; i < route.Hops.Count - 1; i++)
        {
            var hop = route.Hops[i];
            var scid = hop.OutgoingShortChannelId!.Value;
            var capacity = graph?.Graph.TryGetChannel(scid, out var channel) == true ? channel.CapacityMsat : null;
            probability *= graph?.Liquidity.GetSuccessProbability(
                               DirectedChannel.Between(scid, hop.NodeId, route.Hops[i + 1].NodeId),
                               hop.AmountToForward.MilliSatoshi, capacity, graph.NowUnixSeconds, aprioriProbability)
                        ?? aprioriProbability;
        }

        return probability;
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
            AttributionVerification? verification;
            if (match == OutcomeMatch.Match)
            {
                payment.Succeed(fulfilled.PaymentPreimage, now);
                verification = RecordFulfillHoldTimes(payment, fulfilled, payment.Route);
            }
            else
            {
                verification = null;

                // The preimage proves the payment: record it rather than lose it (as LND does)
                _logger.LogError("HTLC {HtlcId} on channel {ChannelId} was fulfilled for payment {PaymentHash}, which is "
                               + "{Status} with HTLC {RecordedHtlcId} on channel {RecordedChannelId}; recording the "
                               + "preimage and marking the payment succeeded", fulfilled.HtlcId, fulfilled.ChannelId,
                                 payment.PaymentHash, payment.Status, payment.OutgoingHtlcId,
                                 payment.OutgoingChannelId);
                payment = WithPreimage(payment, fulfilled, now);
            }

            await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
            var parts = await CountSettledPartsAsync(scope, payment.PaymentHash);
            await UpdateStoredPartAsync(scope, payment.PaymentHash, fulfilled.ChannelId, fulfilled.HtlcId,
                                        PaymentPartState.Succeeded,
                                        verification is { } verified ? ToDurations(verified) : null);
            await StagePaymentSucceededAsync(scope, payment, parts);
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
                // The part is resolved even though the payment is not (its row is settled for the reconciliation)
                try
                {
                    await UpdateStoredPartAsync(scope, failed.PaymentHash, failed.ChannelId, failed.HtlcId,
                                                PaymentPartState.Failed);
                    await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Could not store the resolution of the part of payment {PaymentHash} offered "
                                      + "as HTLC {HtlcId} on channel {ChannelId}", failed.PaymentHash, failed.HtlcId,
                                      failed.ChannelId);
                }

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
            var attribution = AttributionVerification.Absent;
            if (payment.OutgoingChannelId == failed.ChannelId && payment.OutgoingHtlcId == failed.HtlcId)
            {
                (code, sourceIndex, reason, _, attribution) = DescribeFailure(payment.Route, failed.Removal);
                reason += "; not retried.";
                payment.RecordHoldTimes(ToDurations(attribution));
            }
            else
            {
                // NL-321: the part's route is stored with its part row, so its error onion can be read even when it is
                // not the part the payment row records
                try
                {
                    var repository = scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>();
                    var storedPart = await repository.GetByHtlcAsync(payment.PaymentHash, failed.ChannelId,
                                                                     failed.HtlcId);
                    if (storedPart is not null)
                    {
                        (code, sourceIndex, reason, _, attribution) = DescribeFailure(storedPart.Hops,
                                                                                      failed.Removal);
                        reason += "; not retried.";
                        storedPart.State = PaymentPartState.Failed;
                        storedPart.RecordHoldTimes(ToDurations(attribution));
                        await repository.UpdateAsync(storedPart);
                    }
                    else
                    {
                        reason = $"HTLC {failed.HtlcId} on channel {failed.ChannelId}, one part of the payment, "
                               + "failed; its route was not stored, so its error cannot be read; not retried.";
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Could not read the stored part of payment {PaymentHash} offered as HTLC "
                                      + "{HtlcId} on channel {ChannelId}; its error cannot be read",
                                      payment.PaymentHash, failed.HtlcId, failed.ChannelId);
                    reason = $"HTLC {failed.HtlcId} on channel {failed.ChannelId}, one part of the payment, failed; "
                           + "its route was not stored, so its error cannot be read; not retried.";
                }
            }

            payment.Fail(code, sourceIndex, reason, _timeProvider.GetUtcNow());
            await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
            StagePaymentFailed(scope, payment);
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
        foreach (var candidate in inFlight)
        {
            using (await AcquireHashLockAsync(candidate.PaymentHash, cancellationToken))
            {
                // Re-read under the lock: an outcome may have completed it meanwhile
                var payment = await GetPaymentAsync(candidate.PaymentHash, CancellationToken.None);
                if (payment is not { Status: PaymentStatus.InFlight })
                    continue;

                // The stored parts first (NL-321): parts whose HTLC died while the node was down are settled, and a
                // payment with no live part left is failed without a code
                payment = await ReconcileStoredPartsAsync(payment);
                if (payment.Status != PaymentStatus.InFlight)
                {
                    reconciled++;
                    continue;
                }

                if (payment.OutgoingHtlcId is not null)
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
    /// Settles the stored parts of an <c>InFlight</c> payment against channel memory (NL-321), under its hash's lock:
    /// a part that is still <c>InFlight</c> in the table but whose HTLC is gone from (or final on) its channel died
    /// while the node was down and is marked <c>Failed</c> with an unknown outcome. When every part died that way, the
    /// recorded HTLC is gone too and no HTLC of the payment sits on a channel that is not in memory, the payment is
    /// failed without a code; a part on a channel that is not in memory may still resolve (its outcome is replayed
    /// when the channel comes back), so the payment is then left alone.
    /// </summary>
    /// <returns>The payment as stored.</returns>
    private async Task<PaymentModel> ReconcileStoredPartsAsync(PaymentModel payment)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>();
        var parts = await repository.GetForPaymentAsync(payment.PaymentHash);
        var open = parts.Where(p => p.State == PaymentPartState.InFlight).ToList();
        if (open.Count == 0)
            return payment;

        var byOrigin = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelStateDbRepository
                                  .FindHtlcsByOriginAsync(HtlcOrigin.Local(payment.PaymentHash))
                      ?? [];
        var ambiguous = byOrigin.Any(o => !_channelMemoryRepository.TryGetChannel(o.ChannelId, out _));
        var livePart = false;
        foreach (var part in open)
        {
            if (!_channelMemoryRepository.TryGetChannel(part.ChannelId, out var channel)
             || channel.Commitments is not { } commitments)
            {
                ambiguous = true;
                continue;
            }

            var htlc = commitments.GetHtlc(HtlcDirection.Outgoing, part.HtlcId);
            if (htlc is { } record && !HtlcStateTable.IsFinal(record.State))
            {
                livePart = true;
                continue;
            }

            // The part's HTLC is gone: its outcome is unknown (no error onion arrives for a dead HTLC)
            part.State = PaymentPartState.Failed;
            await repository.UpdateAsync(part);
            _logger.LogWarning("Part {PartIndex} of payment {PaymentHash} (HTLC {HtlcId} on channel {ChannelId}) is "
                             + "gone after the restart; its outcome is unknown", part.PartIndex, payment.PaymentHash,
                               part.HtlcId, part.ChannelId);
        }

        if (open.Any(p => p.State == PaymentPartState.Failed))
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        if (livePart || ambiguous)
            return payment;

        // Nothing is live for the payment any more. When the recorded HTLC is not one of the part rows (an older
        // attempt's, or its row's save failed), it must be gone too; a channel that is not in memory stays ambiguous
        if (payment.OutgoingHtlcId is { } recordedId && payment.OutgoingChannelId is { } recordedChannel
         && parts.All(p => p.ChannelId != recordedChannel || p.HtlcId != recordedId))
        {
            if (!_channelMemoryRepository.TryGetChannel(recordedChannel, out var recorded)
             || recorded.Commitments is not { } recordedCommitments)
                return payment;

            var recordedHtlc = recordedCommitments.GetHtlc(HtlcDirection.Outgoing, recordedId);
            if (recordedHtlc is { } live && !HtlcStateTable.IsFinal(live.State))
                return payment;
        }

        payment.Fail(null, null, "No part of the payment was still in flight after the restart; its stored parts' "
                               + "HTLCs are gone and their outcomes are unknown.", _timeProvider.GetUtcNow());
        await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>().UpdateAsync(payment);
        StagePaymentFailed(scope, payment);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        LogFailed(payment);
        if (_sessions.TryGetValue(payment.PaymentHash, out var session) && !session.HasPartsInFlight)
            CompleteSession(session);

        return payment;
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
            GraphRoutingContext? graph = null;
            if (_graphPathSource is { IsAvailable: true })
            {
                // One shadow offset per payment, so its rounds and parts all end at the same payee CLTV
                session.ShadowCltvOffset ??= _graphPathSource.ComputeShadowCltvOffset(session.Target.PayeeNodeId);
                graph = _graphPathSource.CreateContext(session.ShadowCltvOffset.Value);
            }

            IReadOnlyList<PlannedPart>? planned;
            string? noRouteReason;
            if (session.BlindedPaths is { } blindedPaths)
            {
                TryPlanBlinded(session, blindedPaths, remaining, feeLeft, partsAllowed, height, channels, graph,
                               out planned, out noRouteReason);
            }
            else
            {
                var request = new PaymentPlanRequest(session.Target, remaining, session.Amount.MilliSatoshi, feeLeft,
                                                     partsAllowed, height, _secureKeyManager.GetNodePubKey(),
                                                     channels.Select(ToCandidate).ToList(),
                                                     CreateLiquidityProbe(channels, height), session.Constraints,
                                                     _sendOptions.Value.MinPartMsat,
                                                     PaymentRoutePlanner.SumHintForwards(
                                                         session.InFlightParts.Select(p => p.Route)), graph);
                _planner.TryPlan(request, out planned, out noRouteReason);
            }

            if (planned is null)
            {
                noRouteReason ??= "no route";
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

            if (session.Keysend is { } keysend && await FindOversizedKeysendRouteAsync(planned, keysend) is { } tooLong)
            {
                // A keysend's custom records fit the payee's layer (checked up front) but not this longer route: say
                // so instead of letting the onion builder throw (lane lh1-l3 review)
                if (session.HasPartsInFlight)
                {
                    session.TerminalReason ??= tooLong;
                    return;
                }

                await FinishFailedAsync(session, tooLong);
                return;
            }

            var round = new List<(PaymentPart Part, OnionPacket Packet)>(planned.Count);
            foreach (var plannedPart in planned)
            {
                var onion = await _onionFactory.CreateAsync(plannedPart.Route, session.Keysend);
                round.Add((new PaymentPart(plannedPart.Channel, plannedPart.Route,
                                           BuildHops(plannedPart.Route, onion.SharedSecrets,
                                                     plannedPart.Channel.ShortChannelId,
                                                     session.Target.PayeeNodeId), plannedPart.Description),
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

    /// <summary>
    /// Plans a round to the recipient behind the blinded paths (ONION M5; BOLT 12 plan B4-T1): one part over the
    /// cheapest usable path that carries <paramref name="remainingMsat"/> alone, else, when the recipient accepts a
    /// split (<see cref="PaymentTarget.SupportsMpp"/>) and more than one part is allowed, one part per path
    /// (<see cref="TryPlanBlindedSplit"/>).
    /// </summary>
    /// <remarks>
    /// Each part is a route to the path's introduction node for the part's amount plus the path's fee, with the path's
    /// CLTV delta as the final delta and what is left of the fee limit after the path's fee, then the blinded hops
    /// appended by <see cref="BlindedRouteComposer.Compose"/>. A path whose introduction node is this node is decrypted
    /// first (<see cref="SelfIntroducedBlindedPath"/>, B12-PAY-02) and paid from its next hop over our direct channel.
    /// </remarks>
    private bool TryPlanBlinded(PaymentSession session, IReadOnlyList<BlindedPaymentPath> paths, ulong remainingMsat,
                                ulong feeLeftMsat, int partsAllowed, uint height, List<ChannelModel> channels,
                                GraphRoutingContext? graph, out IReadOnlyList<PlannedPart>? planned,
                                out string? reason)
    {
        planned = null;
        var ourNodeId = _secureKeyManager.GetNodePubKey();
        var reasons = new List<string>();
        var candidates = new List<BlindedCandidate>();
        for (var i = 0; i < paths.Count; i++)
        {
            if (session.Constraints.ExcludedBlindedPaths.Contains(i))
            {
                reasons.Add($"blinded path {i}: failed before");
                continue;
            }

            SelfIntroducedBlindedPath? self = null;
            if (paths[i].Path.FirstNodeId == ourNodeId)
            {
                if (_routeBlindingService is null)
                {
                    reasons.Add($"blinded path {i}: we are its introduction node and cannot decrypt our hop (no route "
                              + "blinding service)");
                    continue;
                }

                if (!SelfIntroducedBlindedPath.TryResolve(paths[i], ourNodeId, _routeBlindingService, channels,
                                                          out self, out var why))
                {
                    reasons.Add($"blinded path {i}: {why}");
                    continue;
                }
            }

            candidates.Add(new BlindedCandidate(i, paths[i], self));
        }

        var probe = CreateLiquidityProbe(channels, height);
        foreach (var candidate in candidates.OrderBy(c => SafeFee(c.Path.PayInfo, remainingMsat)))
        {
            if (TryPlanBlindedPart(session, candidate, remainingMsat, feeLeftMsat, height, channels, graph, probe,
                                   out var part, out var why))
            {
                planned = [part];
                reason = null;
                return true;
            }

            reasons.Add($"blinded path {candidate.Index}: {why}");
        }

        if (candidates.Count > 0)
        {
            if (!session.Target.SupportsMpp)
            {
                reasons.Add("the recipient does not accept a split payment");
            }
            else if (partsAllowed < 2)
            {
                reasons.Add("no more parts are allowed");
            }
            else if (TryPlanBlindedSplit(session, candidates, remainingMsat, feeLeftMsat, partsAllowed, height,
                                         channels, graph, probe, out planned, out var splitReason))
            {
                reason = null;
                return true;
            }
            else
            {
                reasons.Add(splitReason);
            }
        }

        reason = reasons.Count > 0 ? string.Join("; ", reasons) : "no usable blinded path";
        return false;
    }

    /// <summary>
    /// Splits <paramref name="remainingMsat"/> over the paths, cheapest proportional fee first, one part per path:
    /// each takes the largest amount (the rest, capped by the path's <c>htlc_maximum_msat</c>, halved down to
    /// <see cref="PaymentSendOptions.MinPartMsat"/> while no route to it carries that much) that fits what is left of
    /// the fee limit. Parts planned earlier in the round count against our channels' liquidity.
    /// </summary>
    private bool TryPlanBlindedSplit(PaymentSession session, IReadOnlyList<BlindedCandidate> candidates,
                                     ulong remainingMsat, ulong feeLeftMsat, int partsAllowed, uint height,
                                     List<ChannelModel> channels, GraphRoutingContext? graph,
                                     Func<ChannelId, IReadOnlyList<ulong>, ulong> probe,
                                     [NotNullWhen(true)] out IReadOnlyList<PlannedPart>? planned,
                                     [NotNullWhen(false)] out string? reason)
    {
        planned = null;
        var minPart = Math.Max(1, _sendOptions.Value.MinPartMsat);
        var parts = new List<PlannedPart>();
        var plannedByChannel = new Dictionary<ChannelId, List<ulong>>();
        var left = remainingMsat;
        var feeLeft = feeLeftMsat;
        foreach (var candidate in candidates.OrderBy(c => c.Path.PayInfo.FeeProportionalMillionths)
                                            .ThenBy(c => c.Path.PayInfo.FeeBaseMsat))
        {
            if (left == 0 || parts.Count >= partsAllowed)
                break;

            var amount = Math.Min(left, MaxRecipientAmount(candidate.Path.PayInfo));
            if (parts.Count == partsAllowed - 1 && amount < left)
                continue; // the last allowed part must take the rest

            while (amount > 0)
            {
                if (TryPlanBlindedPart(session, candidate, amount, feeLeft, height, channels, graph, RoundProbe,
                                       out var part, out _, amount))
                {
                    parts.Add(part);
                    if (!plannedByChannel.TryGetValue(part.Channel.ChannelId, out var amounts))
                        plannedByChannel[part.Channel.ChannelId] = amounts = [];
                    amounts.Add(part.Route.FirstHopAmount.MilliSatoshi);
                    left -= amount;
                    feeLeft -= part.Route.Fee.MilliSatoshi;
                    break;
                }

                amount /= 2;
                if (amount < minPart)
                    break;
            }
        }

        if (left == 0 && parts.Count > 1)
        {
            planned = parts;
            reason = null;
            return true;
        }

        reason = parts.Count == 0
                     ? "split: no path carries a part"
                     : $"split: the paths carry only {remainingMsat - left} of {remainingMsat} msat in {parts.Count} "
                     + "part(s)";
        return false;

        ulong RoundProbe(ChannelId channelId, IReadOnlyList<ulong> amounts) =>
            plannedByChannel.TryGetValue(channelId, out var earlier)
                ? probe(channelId, [.. earlier, .. amounts])
                : probe(channelId, amounts);
    }

    /// <summary>
    /// Plans one part of <paramref name="amountMsat"/> over one path (see <see cref="TryPlanBlinded"/>).
    /// </summary>
    /// <param name="splitAmountMsat">Set for a part of a split, for its description.</param>
    private bool TryPlanBlindedPart(PaymentSession session, BlindedCandidate candidate, ulong amountMsat,
                                    ulong feeBudgetMsat, uint height, List<ChannelModel> channels,
                                    GraphRoutingContext? graph, Func<ChannelId, IReadOnlyList<ulong>, ulong> probe,
                                    [NotNullWhen(true)] out PlannedPart? planned, [NotNullWhen(false)] out string? why,
                                    ulong? splitAmountMsat = null)
    {
        planned = null;
        var path = candidate.Path;
        var index = candidate.Index;
        var amount = LightningMoney.MilliSatoshis(amountMsat);
        if (BlindedRouteComposer.CheckUsable(path, amount) is { } unusable)
        {
            why = unusable;
            return false;
        }

        var pathFee = path.PayInfo.ComputeFeeMsat(amountMsat);
        if (pathFee > feeBudgetMsat)
        {
            why = $"its fee {pathFee} msat is above the fee limit {feeBudgetMsat} msat";
            return false;
        }

        var ourNodeId = _secureKeyManager.GetNodePubKey();
        var introAmount = amountMsat + pathFee;
        var suffix = splitAmountMsat is { } part ? $" ({part} msat part)" : string.Empty;
        if (candidate.Self is not { } self)
        {
            var toIntroduction = new PaymentTarget(path.Path.FirstNodeId, session.PaymentHash,
                                                   session.Target.PaymentSecret,
                                                   LightningMoney.MilliSatoshis(introAmount),
                                                   path.PayInfo.CltvExpiryDelta, []);
            var request = new PaymentPlanRequest(toIntroduction, introAmount, introAmount, feeBudgetMsat - pathFee, 1,
                                                 height, ourNodeId, channels.Select(ToCandidate).ToList(), probe,
                                                 session.Constraints, _sendOptions.Value.MinPartMsat, null, graph);
            if (!_planner.TryPlan(request, out var toIntro, out var noRoute))
            {
                why = $"no route to its introduction node {path.Path.FirstNodeId} ({noRoute})";
                return false;
            }

            var first = toIntro[0];
            var route = BlindedRouteComposer.Compose(first.Route, path, amount, session.Amount, index);
            planned = first with { Route = route, Description = $"{first.Description} + blinded path {index}{suffix}" };
            why = null;
            return true;
        }

        // B12-PAY-02: we are the introduction node; the HTLC goes to the next hop with the next path_key
        var introCltv = checked(height + path.PayInfo.CltvExpiryDelta + HintRouteBuilder.FinalCltvSafetyOffset
                              + session.Constraints.ExtraCltvDelta);
        var finalCltv = introCltv - path.PayInfo.CltvExpiryDelta;
        if (!self.TryComputeFirstHop(introAmount, introCltv, amountMsat, finalCltv, out var firstAmount, out var firstCltv, out why))
            return false;

        var toNext = new PaymentTarget(self.NextNodeId, session.PaymentHash, session.Target.PaymentSecret,
                                       LightningMoney.MilliSatoshis(firstAmount), 0, []);
        var direct = new PaymentPlanRequest(toNext, firstAmount, firstAmount, 0, 1, height, ourNodeId,
                                            channels.Select(ToCandidate).ToList(), probe, session.Constraints,
                                            _sendOptions.Value.MinPartMsat, null, null);
        if (!_planner.TryPlan(direct, out var toPeer, out var noChannel) || toPeer[0].Route.Hops.Count != 1)
        {
            why = $"we introduce it, and no channel of ours to its next node {self.NextNodeId} carries "
                + $"{firstAmount} msat ({noChannel ?? "not a direct channel"})";
            return false;
        }

        planned = new PlannedPart(toPeer[0].Channel,
                                  self.ComposeRoute(amount, session.Amount, firstAmount, firstCltv, finalCltv,
                                                    session.PaymentHash, index),
                                  $"channel {toPeer[0].Channel.ShortChannelId} + blinded path {index} from our hop"
                                + suffix);
        why = null;
        return true;
    }

    /// <summary>
    /// The largest amount a path delivers to its recipient within its <c>htlc_maximum_msat</c> (0: no maximum).
    /// </summary>
    private static ulong MaxRecipientAmount(BlindedPayInfo payInfo)
    {
        if (payInfo.HtlcMaximumMsat == 0)
            return ulong.MaxValue;
        if (payInfo.HtlcMaximumMsat <= payInfo.FeeBaseMsat)
            return 0;

        var amount = (ulong)((UInt128)(payInfo.HtlcMaximumMsat - payInfo.FeeBaseMsat) * 1_000_000
                           / (1_000_000 + payInfo.FeeProportionalMillionths));
        while (amount > 0 && amount + SafeFee(payInfo, amount) > payInfo.HtlcMaximumMsat)
            amount--;
        return amount;
    }

    private static ulong SafeFee(BlindedPayInfo payInfo, ulong amountMsat)
    {
        try
        {
            return payInfo.ComputeFeeMsat(amountMsat);
        }
        catch (OverflowException)
        {
            return ulong.MaxValue;
        }
    }

    /// <summary>
    /// A blinded path that may carry a part: its index in the payment's paths and, when this node introduces it, its
    /// decrypted hops.
    /// </summary>
    private sealed record BlindedCandidate(int Index, BlindedPaymentPath Path, SelfIntroducedBlindedPath? Self);

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
    /// the (failed) row and its part rows; a round while parts are in flight keeps them (the row and parts live on).
    /// </summary>
    private async Task PersistRoundAsync(PaymentSession session, IReadOnlyList<PaymentPart> round)
    {
        if (session.RowCreated && session.HasPartsInFlight)
            return;

        var first = round[0];
        var fee = LightningMoney.MilliSatoshis(round.Aggregate(0UL, (sum, p) => sum + p.Route.Fee.MilliSatoshi));
        var row = new PaymentModel(session.PaymentHash, session.Bolt11, session.Target.PayeeNodeId, session.Amount,
                                   fee, session.CreatedAt, first.Hops, session.Bolt12, session.KeysendDetails);

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

        // The part rows of the replaced attempt (this one, or the failed one a new session retries) are not kept
        await scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>()
                   .DeleteForPaymentAsync(session.PaymentHash);
        await repository.AddAsync(row);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        session.RowCreated = true;
        session.NextPartIndex = 0;
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
                                                             route.FirstHopCltvExpiry, packet,
                                                             route.FirstHopPathKey is { } pathKey
                                                                 ? new BlindedPathTlv(pathKey)
                                                                 : null,
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
        await PersistPartAsync(session, part);
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
    /// Stores a part that was just offered with its route, shared secrets and HTLC id (NL-321), so its error can be
    /// decrypted even when it is not the part the payment row records. A failed save is logged: the HTLC is live and
    /// its outcome still reaches the payment, but the part's error would be unreadable after a restart (as before).
    /// </summary>
    private async Task PersistPartAsync(PaymentSession session, PaymentPart part)
    {
        if (session.NextPartIndex > byte.MaxValue)
        {
            _logger.LogWarning("Payment {PaymentHash}: part {HtlcId} on channel {ChannelId} is beyond the {Limit} part "
                              + "rows of an attempt; it is not stored", session.PaymentHash, part.HtlcId,
                               part.Channel.ChannelId, byte.MaxValue + 1);
            return;
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>();
            await repository.AddAsync(new PaymentPartModel(session.PaymentHash, (byte)session.NextPartIndex++,
                                                           part.Channel.ChannelId, part.HtlcId!.Value,
                                                           PaymentPartState.InFlight, part.Hops));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not store the part of payment {PaymentHash} offered as HTLC {HtlcId} on channel "
                              + "{ChannelId}; its error cannot be read after a restart", session.PaymentHash,
                             part.HtlcId, part.Channel.ChannelId);
        }
    }

    /// <summary>
    /// Stages the resolution of a stored part (NL-321): its state, and the hold times its hops reported in a verified
    /// <c>attribution_data</c>, when the verification was over this part's route. A missing row (a save around the
    /// offer failed) is skipped; a failed save is logged.
    /// </summary>
    private async Task UpdateStoredPartAsync(IServiceScope scope, Hash paymentHash, ChannelId channelId,
                                             ulong htlcId, PaymentPartState state,
                                             IReadOnlyList<TimeSpan>? holdTimes = null)
    {
        try
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>();
            var part = await repository.GetByHtlcAsync(paymentHash, channelId, htlcId);
            if (part is null)
                return;

            part.State = state;
            if (holdTimes is { Count: > 0 })
                part.RecordHoldTimes(holdTimes);

            await repository.UpdateAsync(part);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not store the resolution of the part of payment {PaymentHash} offered as HTLC "
                              + "{HtlcId} on channel {ChannelId}", paymentHash, htlcId, channelId);
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
    /// The failure reason when a planned route's hop payloads, with the keysend payee's custom records, do not fit the
    /// onion; null when every route fits.
    /// </summary>
    private async Task<string?> FindOversizedKeysendRouteAsync(IReadOnlyList<PlannedPart> planned,
                                                               KeysendFinalRecords keysend)
    {
        foreach (var plannedPart in planned)
        {
            var length = await _onionFactory.GetFramedLengthAsync(plannedPart.Route, keysend);
            if (length > OnionConstants.HopPayloadsLength)
                return $"The keysend custom records do not fit the onion over the {plannedPart.Route.Hops.Count}-hop "
                     + $"route found ({length} of {OnionConstants.HopPayloadsLength} bytes); send fewer or smaller "
                     + "records.";
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
            // A row already Failed was failed by an attempt that was retried ("Retrying", no event) or, after a race,
            // for good by another path (its event exists): the final failure is recorded once
            var recordFailure = true;
            if (!session.RowCreated)
            {
                payment = new PaymentModel(session.PaymentHash, session.Bolt11, session.Target.PayeeNodeId,
                                           session.Amount, LightningMoney.Zero, session.CreatedAt,
                                           bolt12: session.Bolt12, keysend: session.KeysendDetails);
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
                                                   sourceIndex, reason, now, stored.Route, stored.Bolt12,
                                                   stored.Keysend);
                    recordFailure = !await PaymentFailedRecordedAsync(scope, payment);
                }

                await repository.UpdateAsync(payment);
            }

            if (recordFailure)
                StagePaymentFailed(scope, payment);

            // No outcome of the payment's remaining parts is waited for any more; their rows are settled here (NL-321)
            var partRepository = scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>();
            foreach (var open in await partRepository.GetForPaymentAsync(session.PaymentHash))
            {
                if (open.State == PaymentPartState.InFlight)
                    await UpdateStoredPartAsync(scope, session.PaymentHash, open.ChannelId, open.HtlcId,
                                                PaymentPartState.Failed);
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
                                           null, null, null, null, next.Hops, stored.Bolt12, stored.Keysend);
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
        AttributionVerification? verification;
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
                                                 fulfilled.PaymentPreimage, null, null, null, now, part.Hops,
                                                 payment.Bolt12, payment.Keysend);
            verification = RecordFulfillHoldTimes(succeeded, fulfilled, part.Hops);
            await StageReplacementAsync(repository, payment, succeeded, "Superseded by the fulfilled part.");
            payment = succeeded;
        }
        else
        {
            verification = null;
            if (payment.Status == PaymentStatus.InFlight)
            {
                if (payment.OutgoingHtlcId is null)
                    payment.AddOutgoingHtlc(fulfilled.ChannelId, fulfilled.HtlcId);
                payment.Succeed(fulfilled.PaymentPreimage, now);
                verification = RecordFulfillHoldTimes(payment, fulfilled, part?.Hops ?? payment.Route);
            }
            else
            {
                payment = WithPreimage(payment, fulfilled, now);
            }

            await repository.UpdateAsync(payment);
        }

        if (part is not null)
            await UpdateStoredPartAsync(scope, session.PaymentHash, fulfilled.ChannelId, fulfilled.HtlcId,
                                        PaymentPartState.Succeeded,
                                        verification is { } verified ? ToDurations(verified) : null);
        // The parts still in flight (this one included) are the ones the payee settles
        await StagePaymentSucceededAsync(scope, payment, session.InFlightParts.Count());
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        part?.Status = PaymentPartStatus.Succeeded;
        if (part is not null)
            _graphPathSource?.MissionControl.RecordSuccess(part.Route);
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
        var (retry, note) = _retryPolicy.Decide(part, failed.Removal.Kind, interpretation, session.Constraints,
                                                attribution.InvalidHopIndex);
        session.LastFailure = (code, sourceIndex, $"{reason} ({note}).");
        session.LastFailureHoldTimes = attribution.IsPresent
                                           ? (failed.ChannelId, failed.HtlcId, ToDurations(attribution))
                                           : null;
        if (!retry)
            session.TerminalReason ??= note;

        _logger.LogWarning("Payment {PaymentHash}: the part of {Amount} msat over {Path} failed: {Reason} ({Note})",
                           session.PaymentHash, part.Route.Amount.MilliSatoshi, part.Description, reason, note);

        // The part's row is resolved (NL-321), with the hold times its hops reported
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            await UpdateStoredPartAsync(scope, session.PaymentHash, failed.ChannelId, failed.HtlcId,
                                        PaymentPartState.Failed,
                                        attribution.IsPresent ? ToDurations(attribution) : null);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

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
    /// <returns>The verification (null when there was nothing to verify), for the fulfilled part's stored row.</returns>
    private AttributionVerification? RecordFulfillHoldTimes(PaymentModel payment, OutgoingHtlcFulfilled fulfilled,
                                                            IReadOnlyList<PaymentHop> route)
    {
        if (_attributionDataService is null || fulfilled.AttributionData.IsEmpty || route.Count == 0)
            return null;

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

        return verified.Attribution;
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

    /// <summary>
    /// Pays a BOLT 11 invoice with bLIP 39 blinded paths through <see cref="PayBlindedAsync"/>: the amount, the
    /// payment hash and the paths from the invoice, several parts only when it sets <c>basic_mpp</c> (BOLT 4: the
    /// payer MUST NOT split otherwise). Its <c>c</c> field is ignored (bLIP 39: the paths' <c>cltv_expiry_delta</c>
    /// already holds the recipient's final delta) and so is its signing key, an ephemeral one that names no node.
    /// </summary>
    private async Task<PayInvoiceResult> PayBlindedInvoiceAsync(Invoice invoice, string bolt11, LightningMoney? amount,
                                                                PayInvoiceOptions options,
                                                                CancellationToken cancellationToken)
    {
        var paymentHash = invoice.PaymentHash
                       ?? throw new ArgumentException("The invoice has no payment hash.", nameof(bolt11));
        var paymentAmount = ResolveAmount(invoice.Amount.IsZero ? null : invoice.Amount, amount);
        var request = new PayBlindedRequest(PaymentTarget.ToWireBytes(paymentHash), paymentAmount,
                                            invoice.BlindedPaymentPaths, bolt11.Trim())
        {
            AllowMpp = invoice.Features?.IsFeatureSet(Feature.BasicMpp) ?? false
        };
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Paying invoice {PaymentHash} over its {Count} blinded path(s) (bLIP 39)",
                                   request.PaymentHash, request.Paths.Count);

        return await PayBlindedAsync(request, options, cancellationToken);
    }

    private Invoice DecodeInvoice(string bolt11)
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

        return invoice;
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
        if (payment.Status == PaymentStatus.Failed)
            StagePaymentFailed(scope, payment);
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
    /// <remarks>
    /// The last hop of a route that ends in a blinded path is stored under <paramref name="payee"/> (the recipient's
    /// real id when the caller knew it, e.g. a BOLT 12 <c>invoice_node_id</c>), not under its blinded id, so the row
    /// names its payee; the shared secret is the one of the blinded hop.
    /// </remarks>
    private static List<PaymentHop> BuildHops(PaymentRoute route, IReadOnlyList<Secret> sharedSecrets,
                                              ShortChannelId firstChannel, CompactPubKey payee)
    {
        var hops = new List<PaymentHop>(route.Hops.Count);
        for (var i = 0; i < route.Hops.Count; i++)
        {
            var previous = i == 0 ? null : route.Hops[i - 1];
            var nodeId = i == route.Hops.Count - 1 && route.BlindedStartIndex is not null ? payee : route.Hops[i].NodeId;
            hops.Add(new PaymentHop(nodeId, previous?.OutgoingShortChannelId ?? firstChannel,
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
                                    fulfilled.PaymentPreimage, null, null, null, completedAt, payment.Route,
                                    payment.Bolt12, payment.Keysend);
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

    /// <summary>
    /// Stages the payment's <c>PaymentSucceeded</c> accounting event (NL-602) on the scope's unit of work, in the save
    /// that marks it <c>Succeeded</c> (the callers return early for a payment already <c>Succeeded</c>). A payment of
    /// one of our own invoices is flagged as a self-payment (a rebalance). Never throws.
    /// </summary>
    private async Task StagePaymentSucceededAsync(IServiceScope scope, PaymentModel payment, int parts)
    {
        try
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var selfPayment = await IsOurInvoiceAsync(unitOfWork, payment.PaymentHash);
            PaymentAccountingEvents.TryStage(unitOfWork, () =>
                                                 PaymentAccountingEvents.PaymentSucceeded(
                                                     payment, parts, selfPayment, DescribeInvoice(payment)), _logger);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not record the accounting event of payment {PaymentHash}", payment.PaymentHash);
        }
    }

    /// <summary>Whether <paramref name="paymentHash"/> is one of our own invoices (a self-payment); false when that
    /// cannot be read.</summary>
    private async Task<bool> IsOurInvoiceAsync(IUnitOfWork unitOfWork, Hash paymentHash)
    {
        try
        {
            return await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(paymentHash) is not null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not check whether payment {PaymentHash} pays one of our invoices",
                               paymentHash);
            return false;
        }
    }

    /// <summary>
    /// Stages the payment's <c>PaymentFailed</c> accounting event (NL-602) in the save that fails it for good (never
    /// for an attempt that is retried). Never throws.
    /// </summary>
    private void StagePaymentFailed(IServiceScope scope, PaymentModel payment)
    {
        try
        {
            PaymentAccountingEvents.TryStage(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                                             () => PaymentAccountingEvents.PaymentFailed(payment), _logger);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not record the accounting event of payment {PaymentHash}", payment.PaymentHash);
        }
    }

    /// <summary>Whether the final failure of <paramref name="payment"/>'s attempt is already in the feed.</summary>
    private async Task<bool> PaymentFailedRecordedAsync(IServiceScope scope, PaymentModel payment)
    {
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository
                              .ExistsAsync(AccountingEventKeys.PaymentFailed(payment.PaymentHash,
                                                                             payment.CreatedAt.UtcTicks));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not check the accounting feed for payment {PaymentHash}", payment.PaymentHash);
            return false;
        }
    }

    /// <summary>The stored parts of a payment that were not failed (at least 1): the parts the payee settles.</summary>
    private async Task<int> CountSettledPartsAsync(IServiceScope scope, Hash paymentHash)
    {
        try
        {
            var parts = await scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>()
                                   .GetForPaymentAsync(paymentHash);
            return Math.Max(1, parts.Count(p => p.State != PaymentPartState.Failed));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not count the parts of payment {PaymentHash}", paymentHash);
            return 1;
        }
    }

    /// <summary>The description of the BOLT 11 invoice a payment paid, when it can be read.</summary>
    private string? DescribeInvoice(PaymentModel payment)
    {
        if (payment.Bolt11 is null)
            return null;

        try
        {
            return Invoice.Decode(payment.Bolt11.Trim(), _nodeOptions.Value.BitcoinNetwork).Description;
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(e, "Could not read the description of the invoice of payment {PaymentHash}",
                                 payment.PaymentHash);
            return null;
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