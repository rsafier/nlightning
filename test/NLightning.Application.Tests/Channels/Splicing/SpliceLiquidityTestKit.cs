using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.LiquidityAds;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Interfaces;
using Domain.LiquidityAds.Models;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;

/// <summary>
/// Liquidity ads in <see cref="SpliceHarness"/> (NL-850): each node's <see cref="LiquidityAdsService"/> sees the other
/// node's configured rates as that peer's <c>init</c> rates (through a peer manager that answers only the service), and
/// its purchases live in <see cref="InMemorySpliceLiquidityPurchases"/>, committed with the node's saves.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class SpliceLiquidityTestKit
{
    /// <summary>The seller's one rate in the tests: 10,000-1,000,000 sat, weight 400, 1 % + 1,000 sat.</summary>
    public static readonly FundingRate Rate = new(10_000, 1_000_000, 400, 100, 1_000, 5_000);

    /// <summary>Configures <paramref name="options"/> to sell at <see cref="Rate"/>.</summary>
    public static void Sell(NodeOptions options, params FundingRate[] rates)
    {
        options.LiquidityAds.FundingRates =
        [
            .. (rates.Length == 0 ? [Rate] : rates).Select(r => new FundingRateOptions
            {
                MinAmountSat = r.MinAmountSat,
                MaxAmountSat = r.MaxAmountSat,
                FundingWeight = r.FundingWeight,
                FeeBasis = r.FeeBasis,
                FeeBaseSat = r.FeeBaseSat,
                ChannelCreationFeeSat = r.ChannelCreationFeeSat
            })
        ];
    }

    /// <summary>
    /// Registers the node's <see cref="LiquidityAdsService"/>, whose seller rates are <paramref name="peerRates"/>
    /// (the other node's, read when asked).
    /// </summary>
    public static void AddLiquidityAds(IServiceCollection services, Func<WillFundRates?> peerRates)
    {
        services.AddSingleton(sp => new LiquidityAdsService(sp.GetRequiredService<ILightningSigner>(),
                                                            new PeerRatesServiceProvider(sp, peerRates),
                                                            NullLogger<LiquidityAdsService>.Instance,
                                                            sp.GetRequiredService<IOptions<NodeOptions>>()));
    }

    /// <summary>The node's services, plus a peer manager whose every peer advertises the given rates.</summary>
    private sealed class PeerRatesServiceProvider(IServiceProvider inner, Func<WillFundRates?> rates) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType != typeof(IPeerManager))
                return inner.GetService(serviceType);

            var peerService = new Mock<IPeerService>();
            peerService.SetupGet(p => p.LiquidityRates).Returns(rates);
            var peerManager = new Mock<IPeerManager>();
            peerManager.Setup(m => m.GetPeer(It.IsAny<CompactPubKey>()))
                       .Returns((CompactPubKey id) =>
                        {
                            var peer = new PeerModel(id, "127.0.0.1", 9735, "IPv4");
                            peer.SetPeerService(peerService.Object);
                            return peer;
                        });
            return peerManager.Object;
        }
    }
}

/// <summary>
/// The <c>LiquidityPurchases</c> rows of a harness node: staged writes become visible to reads (and get their id) only
/// when the node's unit of work saves; reads return copies, as a database does.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemorySpliceLiquidityPurchases : ILiquidityPurchaseDbRepository
{
    private readonly List<LiquidityPurchaseModel> _stagedAdds = [];
    private readonly Dictionary<long, LiquidityPurchaseModel> _stagedUpdates = [];
    private readonly Dictionary<long, LiquidityPurchaseModel> _committed = [];
    private long _nextId = 1;

    /// <summary>The saved rows, oldest first (copies).</summary>
    public IReadOnlyList<LiquidityPurchaseModel> Committed => _committed.Values.OrderBy(p => p.Id).Select(Copy).ToList();

    public void Add(LiquidityPurchaseModel purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        if (purchase.Id != 0)
            throw new ArgumentException("A new purchase has no id", nameof(purchase));
        _stagedAdds.Add(purchase);
    }

    public void Update(LiquidityPurchaseModel purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        if (purchase.Id == 0)
        {
            if (!_stagedAdds.Contains(purchase))
                throw new InvalidOperationException("Unknown purchase");
            return;
        }

        _stagedUpdates[purchase.Id] = purchase;
    }

    public void Commit()
    {
        foreach (var added in _stagedAdds)
        {
            if (_committed.Values.Any(p => p.ChannelId == added.ChannelId && p.FundingTxId == added.FundingTxId))
                throw new InvalidOperationException("The (channel, funding txid) of a purchase is unique");
            added.AssignId(_nextId++);
            _committed[added.Id] = Copy(added);
        }

        foreach (var (id, updated) in _stagedUpdates)
            _committed[id] = Copy(updated);

        _stagedAdds.Clear();
        _stagedUpdates.Clear();
    }

    public void DiscardStaged()
    {
        _stagedAdds.Clear();
        _stagedUpdates.Clear();
    }

    public Task<IReadOnlyList<LiquidityPurchaseModel>> GetByChannelIdAsync(ChannelId channelId) =>
        Task.FromResult<IReadOnlyList<LiquidityPurchaseModel>>(
            Committed.Where(p => p.ChannelId == channelId).ToList());

    public Task<LiquidityPurchaseModel?> GetByFundingTxIdAsync(ChannelId channelId, TxId fundingTxId) =>
        Task.FromResult(Committed.FirstOrDefault(p => p.ChannelId == channelId && p.FundingTxId == fundingTxId));

    public Task<IReadOnlyList<LiquidityPurchaseModel>> ListAsync(LiquidityPurchaseRole? role,
                                                                 LiquidityPurchaseStatus? status, int skip, int take) =>
        Task.FromResult<IReadOnlyList<LiquidityPurchaseModel>>(
            Committed.Where(p => (role is null || p.Role == role) && (status is null || p.Status == status))
                     .Reverse()
                     .Skip(skip)
                     .Take(take)
                     .ToList());

    public Task<int> CountPendingSalesAsync() =>
        Task.FromResult(Committed.Count(p => p is
        {
            Role: LiquidityPurchaseRole.Seller, Status: LiquidityPurchaseStatus.Pending
        }));

    public Task<int> CountPendingSalesByPeerAsync(CompactPubKey peerNodeId) =>
        Task.FromResult(Committed.Count(p => p.Role == LiquidityPurchaseRole.Seller
                                          && p.Status == LiquidityPurchaseStatus.Pending
                                          && p.PeerNodeId == peerNodeId));

    public Task<LiquidityPurchaseModel?> GetActiveSaleLeaseAsync(ChannelId channelId, uint currentHeight) =>
        Task.FromResult(Committed.LastOrDefault(p => p.ChannelId == channelId && p.Role == LiquidityPurchaseRole.Seller
                                                  && p.IsLeaseInForce(currentHeight)));

    private static LiquidityPurchaseModel Copy(LiquidityPurchaseModel p) =>
        LiquidityPurchaseModel.Restore(p.Id, p.ChannelId, p.FundingTxId, p.Role, p.Kind, p.RequestedSat,
                                       p.ContributedSat, p.Rate, p.PaymentType, p.MiningFeeSat, p.ServiceFeeSat,
                                       p.Signature, p.FundingScript, p.PeerNodeId, p.LeaseBlocks, p.CreatedAt,
                                       p.Status, p.LeaseStartHeight, p.ClosedAtHeight, p.ClosedEarly, p.MaxFeeSat);
}