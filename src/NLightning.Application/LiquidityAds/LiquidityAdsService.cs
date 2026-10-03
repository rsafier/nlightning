using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.LiquidityAds;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Gossip.Graph.Interfaces;

/// <summary>
/// The node's side of liquidity ads (BOLT PR #1153 as Eclair 0.14.3 speaks it, NL-850), shared by the dual-funded open,
/// the splice and their RBF attempts in both roles. Seller: <see cref="TryStartSale"/> checks a request against our
/// <c>Node:LiquidityAds:FundingRates</c> and the griefing caps (decision D-L5: at most
/// <see cref="LiquidityAdsOptions.MaxConcurrentSales"/> sale negotiations node-wide and
/// <see cref="LiquidityAdsOptions.MaxSalesPerPeer"/> per peer hold our wallet inputs at once) and
/// <see cref="CreateWillFund"/> signs the answer with the node key. Buyer: <see cref="CreateRequest"/> picks the
/// seller's rate (its <c>init</c> first, then its <c>node_announcement</c>) and <see cref="ValidateWillFund"/> checks
/// its answer. Purchases are recorded by the callers (<see cref="CreatePurchase"/>, <c>IUnitOfWork.LiquidityPurchaseDbRepository</c>)
/// in the save that stores the attempt. Thread-safe singleton.
/// </summary>
public sealed class LiquidityAdsService
{
    private readonly ILightningSigner _signer;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LiquidityAdsService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _salesLock = new();
    private readonly Dictionary<CompactPubKey, int> _salesByPeer = [];
    private int _sales;

    public LiquidityAdsService(ILightningSigner signer, IServiceProvider serviceProvider,
                               ILogger<LiquidityAdsService> logger, IOptions<NodeOptions> nodeOptions,
                               TimeProvider? timeProvider = null)
    {
        _signer = signer;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The options (<c>Node:LiquidityAds</c>).</summary>
    public LiquidityAdsOptions Options => _nodeOptions.LiquidityAds;

    /// <summary>Our rates when we sell (decision D-L3: selling is off while no rate is configured), else null.</summary>
    public WillFundRates? OurRates => _nodeOptions.LiquidityAds.GetWillFundRates();

    /// <summary>The sale negotiations holding a slot now (node-wide).</summary>
    public int SalesInProgress
    {
        get
        {
            lock (_salesLock)
                return _sales;
        }
    }

    #region Seller

    /// <summary>
    /// Checks a buyer's <c>request_funding</c> (Eclair <c>validateRequest</c>, and the griefing caps) and, when we sell,
    /// takes a sale slot for the negotiation. Dispose the returned sale when the negotiation ends (signed or aborted).
    /// </summary>
    /// <param name="buyer">The buyer's node id.</param>
    /// <param name="request">Its request.</param>
    /// <param name="sale">The sale, holding a slot, when accepted.</param>
    /// <returns>Why the request is refused, or null when accepted.</returns>
    public string? TryStartSale(CompactPubKey buyer, RequestFunding request, out LiquiditySale? sale)
    {
        ArgumentNullException.ThrowIfNull(request);
        sale = null;
        if (OurRates is not { } rates)
            return "we do not sell liquidity";

        var refusal = LiquidityAdsRules.ValidateRequest(rates, request);
        if (refusal != LiquidityAdsRefusal.None)
            return $"invalid request_funding: {refusal}";

        var options = _nodeOptions.LiquidityAds;
        lock (_salesLock)
        {
            var peerSales = _salesByPeer.GetValueOrDefault(buyer);
            if (_sales >= options.MaxConcurrentSales)
                return "too many liquidity sales in progress";
            if (peerSales >= options.MaxSalesPerPeer)
                return "too many liquidity sales in progress with this peer";

            _sales++;
            _salesByPeer[buyer] = peerSales + 1;
        }

        sale = new LiquiditySale(this, buyer, request);
        return null;
    }

    /// <summary>
    /// Our signed <c>will_fund</c> for <paramref name="rate"/> over the new funding output's script
    /// (<see cref="LiquidityAdsRules.SignedData"/>, node key, ECDSA).
    /// </summary>
    public WillFund CreateWillFund(FundingRate rate, byte[] fundingScript)
    {
        ArgumentNullException.ThrowIfNull(fundingScript);
        var signature = _signer.SignNodeMessage(LiquidityAdsRules.SignedData(rate, fundingScript));
        return new WillFund(rate, fundingScript.ToArray(), signature);
    }

    private void EndSale(CompactPubKey buyer)
    {
        lock (_salesLock)
        {
            if (_sales > 0)
                _sales--;
            if (_salesByPeer.TryGetValue(buyer, out var count))
            {
                if (count <= 1)
                    _salesByPeer.Remove(buyer);
                else
                    _salesByPeer[buyer] = count - 1;
            }
        }
    }

    #endregion

    #region Buyer

    /// <summary>
    /// The seller's rates: from its <c>init</c> on the current connection, else from its <c>node_announcement</c> in our
    /// graph; null when it advertises none.
    /// </summary>
    public WillFundRates? GetSellerRates(CompactPubKey seller)
    {
        var peerManager = _serviceProvider.GetService<IPeerManager>();
        if (peerManager?.GetPeer(seller) is { } peer && peer.TryGetPeerService(out var service)
         && service.LiquidityRates is { } initRates)
            return initRates;

        var graph = _serviceProvider.GetService<IGraphStore>();
        if (graph is not null && graph.GetSnapshot().TryGetNode(seller, out var node)
         && NodeAnnouncementRates.TryReadFromAnnouncement(node.RawAnnouncement.Span, out var announced))
            return announced;

        return null;
    }

    /// <summary>
    /// The <c>request_funding</c> for <paramref name="liquidity"/>: at the given rate when it is one of the seller's,
    /// else at the seller's cheapest rate that sells the amount (lowest total fee for the amount at
    /// <paramref name="fundingFeeratePerKw"/>), paid from the channel balance (D-L2).
    /// </summary>
    /// <exception cref="InvalidOperationException">The seller sells nothing, not this amount, not at that rate, or not
    /// from the channel balance.</exception>
    public RequestFunding CreateRequest(CompactPubKey seller, LiquidityRequest liquidity, uint fundingFeeratePerKw,
                                        bool isChannelCreation)
    {
        ArgumentNullException.ThrowIfNull(liquidity);
        if (liquidity.AmountSat == 0)
            throw new InvalidOperationException("The inbound liquidity to buy must be above 0");

        var rates = GetSellerRates(seller)
                 ?? throw new InvalidOperationException($"{seller} does not advertise liquidity rates");
        if (!rates.Supports(LiquidityPaymentType.FromChannelBalance))
            throw new InvalidOperationException($"{seller} does not sell liquidity paid from the channel balance");

        if (liquidity.Rate is { } wanted)
        {
            if (!rates.Rates.Contains(wanted))
                throw new InvalidOperationException($"{seller} does not offer the requested rate");
            if (!wanted.IsCompatible(liquidity.AmountSat))
                throw new InvalidOperationException(
                    $"The requested rate sells {wanted.MinAmountSat}-{wanted.MaxAmountSat} sat, not {liquidity.AmountSat}");
            return new RequestFunding(liquidity.AmountSat, wanted, LiquidityPaymentDetails.FromChannelBalance);
        }

        var best = rates.Rates.Where(r => r.IsCompatible(liquidity.AmountSat))
                        .Select(r => (Rate: r,
                                      Fee: LiquidityAdsRules.ComputeFees(r, fundingFeeratePerKw, liquidity.AmountSat,
                                                                         liquidity.AmountSat, isChannelCreation)
                                                            .TotalSat))
                        .OrderBy(x => x.Fee)
                        .FirstOrDefault();
        if (best == default)
            throw new InvalidOperationException($"{seller} sells no rate for {liquidity.AmountSat} sat");

        return new RequestFunding(liquidity.AmountSat, best.Rate, LiquidityPaymentDetails.FromChannelBalance);
    }

    /// <summary>
    /// Checks the seller's <c>provide_funding</c> answer (Eclair <c>validateRemoteFunding</c> and our fee limit:
    /// <paramref name="maxFeeSat"/>, else <c>Node:LiquidityAds:MaxFeeSat</c>).
    /// </summary>
    public LiquidityAdsRefusal ValidateWillFund(CompactPubKey seller, RequestFunding request, WillFund? willFund,
                                                ReadOnlySpan<byte> fundingScript, ulong sellerContributionSat,
                                                uint fundingFeeratePerKw, bool isChannelCreation, ulong? maxFeeSat,
                                                out LiquidityFees fees)
    {
        var refusal = LiquidityAdsRules.ValidateWillFund(request, willFund, fundingScript, sellerContributionSat,
                                                         fundingFeeratePerKw, isChannelCreation,
                                                         (hash, signature) =>
                                                             _signer.VerifyNodeMessage(hash, signature, seller),
                                                         maxFeeSat ?? _nodeOptions.LiquidityAds.MaxFeeSat, out fees);
        if (refusal != LiquidityAdsRefusal.None)
            _logger.LogWarning("Refusing the liquidity of {Seller}: {Refusal}", seller, refusal);
        return refusal;
    }

    #endregion

    /// <summary>
    /// A purchase record of a negotiated attempt (status Pending), for the attempt's save.
    /// </summary>
    public LiquidityPurchaseModel CreatePurchase(ChannelId channelId,
                                                 TxId fundingTxId,
                                                 LiquidityPurchaseRole role, LiquidityPurchaseKind kind,
                                                 RequestFunding request, ulong contributedSat, LiquidityFees fees,
                                                 WillFund willFund, CompactPubKey peer) =>
        new(channelId, fundingTxId, role, kind, request.RequestedSat, contributedSat, request.Rate,
            (LiquidityPaymentType)request.PaymentDetails.Type, fees.MiningFeeSat, fees.ServiceFeeSat,
            willFund.Signature, willFund.FundingScript, peer, _nodeOptions.LiquidityAds.LeaseBlocks,
            _timeProvider.GetUtcNow());

    /// <summary>
    /// A sale slot (griefing cap, D-L5) held by one sale negotiation; dispose it once the negotiation ends.
    /// </summary>
    public sealed class LiquiditySale : IDisposable
    {
        private readonly LiquidityAdsService _service;
        private int _disposed;

        internal LiquiditySale(LiquidityAdsService service, CompactPubKey buyer, RequestFunding request)
        {
            _service = service;
            Buyer = buyer;
            Request = request;
        }

        /// <summary>The buyer.</summary>
        public CompactPubKey Buyer { get; }

        /// <summary>What it asked for (we contribute exactly <see cref="RequestFunding.RequestedSat"/>).</summary>
        public RequestFunding Request { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _service.EndSale(Buyer);
        }
    }
}