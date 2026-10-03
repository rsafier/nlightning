using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Client.Enums;
using Domain.Client.Responses;
using Domain.LiquidityAds.Enums;
using Transport.Ipc.Responses;

/// <summary>
/// Prints <c>liquidityads rates|sellers|purchases</c> (ClientCommand 46, NL-850) and the purchase that an
/// <c>openchannel</c>, <c>splicein</c> or <c>bumpopen</c> made (<see cref="WritePurchase"/>).
/// </summary>
public sealed class LiquidityAdsPrinter : IPrinter<LiquidityAdsIpcResponse>
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    private readonly TextWriter _output;

    public LiquidityAdsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(LiquidityAdsIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Action)
        {
            case LiquidityAdsAction.Rates:
                PrintRates(item);
                break;
            case LiquidityAdsAction.Sellers:
                PrintSellers(item);
                break;
            case LiquidityAdsAction.Purchases:
                PrintPurchases(item);
                break;
        }
    }

    /// <summary>
    /// Prints a purchase made with a funding attempt (<c>openchannel</c>, <c>splicein</c>, <c>bumpopen</c>): what was
    /// requested and contributed, the fee with its two parts and the lease.
    /// </summary>
    public static void WritePurchase(TextWriter output, LiquidityPurchaseIpcInfo purchase, string indent = "  ")
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(purchase);
        var bought = purchase.Role == LiquidityPurchaseRole.Buyer;
        output.WriteLine(string.Format(s_inv, "{0}{1,-19}{2} sat requested, {3} sat contributed by the {4}", indent,
                                       bought ? "Liquidity bought:" : "Liquidity sold:", purchase.RequestedSat,
                                       purchase.ContributedSat, bought ? "seller" : "us"));
        output.WriteLine(string.Format(s_inv, "{0}{1,-19}{2} sat (mining {3} sat, service {4} sat), {5}", indent,
                                       "Liquidity fee:", purchase.TotalFeeSat, purchase.MiningFeeSat,
                                       purchase.ServiceFeeSat, bought ? "paid from our balance" : "paid to our balance"));
        output.WriteLine(string.Format(s_inv, "{0}{1,-19}{2}", indent, "Liquidity lease:",
                                       DescribeLease(purchase, null)));
    }

    /// <summary>The lease status of <paramref name="purchase"/> at <paramref name="currentHeight"/>, when known.</summary>
    public static string DescribeLease(LiquidityPurchaseIpcInfo purchase, uint? currentHeight)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        switch (purchase.Status)
        {
            case LiquidityPurchaseStatus.Replaced:
                return "none (another attempt of the funding confirmed)";
            case LiquidityPurchaseStatus.Closed:
                var closedAt = purchase.ClosedAtHeight is { } h ? h.ToString(s_inv) : "?";
                return purchase.ClosedEarly
                           ? $"ended early: the channel closed at block {closedAt}, before the lease ended"
                             + (purchase.LeaseEndHeight is { } e ? $" (block {e.ToString(s_inv)})" : string.Empty)
                           : $"completed; the channel closed at block {closedAt}";
        }

        if (purchase.LeaseEndHeight is not { } end)
            return string.Format(s_inv, "{0} blocks from the funding's confirmation", purchase.LeaseBlocks);

        if (currentHeight is not { } height || height == 0)
            return string.Format(s_inv, "until block {0}", end);

        return height < end
                   ? string.Format(s_inv, "until block {0} ({1} blocks left)", end, end - height)
                   : string.Format(s_inv, "ended at block {0}", end);
    }

    private void PrintRates(LiquidityAdsIpcResponse item)
    {
        if (item.OurRates is not { Count: > 0 } rates)
        {
            _output.WriteLine("Not selling liquidity (no Node:LiquidityAds:FundingRates configured).");
            return;
        }

        _output.WriteLine("Selling liquidity:");
        _output.WriteLine(string.Format(s_inv, "  Lease:              {0} blocks", item.LeaseBlocks));
        _output.WriteLine("  Payment types:      " + string.Join(", ", item.OurPaymentTypes ?? []));
        _output.WriteLine(string.Format(s_inv, "  Sales in progress:  {0}", item.SalesInProgress));
        WriteRates(rates, "  ");
    }

    private void PrintSellers(LiquidityAdsIpcResponse item)
    {
        if (item.Sellers.Count == 0)
        {
            _output.WriteLine("No seller found (no connected peer's init and no node_announcement carries rates).");
            return;
        }

        _output.WriteLine(string.Format(s_inv, "Sellers: {0}", item.Sellers.Count));
        foreach (var seller in item.Sellers)
        {
            _output.WriteLine();
            _output.WriteLine("Node ID:        " + seller.NodeId);
            if (seller.Alias is not null)
                _output.WriteLine("  Alias:          " + seller.Alias);
            _output.WriteLine("  Source:         " + (seller.Source == LiquiditySellerSource.Init
                                                          ? "init (connected)"
                                                          : "node_announcement"
                                                          + (seller.IsConnected ? " (connected)" : string.Empty)));
            _output.WriteLine("  Payment types:  " + string.Join(", ", seller.PaymentTypes));
            WriteRates(seller.Rates, "  ");
            WriteOtherSource(seller);
        }
    }

    /// <summary>
    /// The other source of a connected seller (NL-884): for rates read from its <c>init</c>, those of its
    /// <c>node_announcement</c> (they can differ); for a connected seller read from its announcement, that its
    /// <c>init</c> carries none.
    /// </summary>
    private void WriteOtherSource(LiquiditySellerIpcInfo seller)
    {
        if (seller.Source != LiquiditySellerSource.Init)
        {
            if (seller.IsConnected)
                _output.WriteLine("  init:           no rates");
            return;
        }

        if (seller.AnnouncedRates is not { } announced)
        {
            _output.WriteLine("  node_announcement: no rates");
            return;
        }

        var announcedTypes = seller.AnnouncedPaymentTypes ?? [];
        if (announcedTypes.SequenceEqual(seller.PaymentTypes)
         && announced.Select(DescribeRate).SequenceEqual(seller.Rates.Select(DescribeRate)))
        {
            _output.WriteLine("  node_announcement: the same rates");
            return;
        }

        _output.WriteLine("  node_announcement:");
        _output.WriteLine("    Payment types:  " + string.Join(", ", announcedTypes));
        WriteRates(announced, "    ");
    }

    private void PrintPurchases(LiquidityAdsIpcResponse item)
    {
        if (item.Purchases.Count == 0)
        {
            _output.WriteLine("No liquidity purchases.");
            return;
        }

        _output.WriteLine(string.Format(s_inv, "Purchases: {0} (at block {1})", item.Purchases.Count,
                                        item.CurrentHeight));
        foreach (var purchase in item.Purchases)
        {
            _output.WriteLine();
            _output.WriteLine(string.Format(s_inv, "{0} on channel {1}",
                                            purchase.Role == LiquidityPurchaseRole.Buyer ? "Bought" : "Sold",
                                            purchase.ChannelId));
            _output.WriteLine("  Status:         " + purchase.Status);
            _output.WriteLine("  Kind:           " + purchase.Kind);
            _output.WriteLine("  Funding TxId:   " + purchase.FundingTxId);
            _output.WriteLine("  Peer:           " + purchase.PeerNodeId);
            _output.WriteLine(string.Format(s_inv, "  Requested:      {0} sat", purchase.RequestedSat));
            _output.WriteLine(string.Format(s_inv, "  Contributed:    {0} sat", purchase.ContributedSat));
            _output.WriteLine(string.Format(s_inv, "  Fee:            {0} sat (mining {1} sat, service {2} sat)",
                                            purchase.TotalFeeSat, purchase.MiningFeeSat, purchase.ServiceFeeSat));
            _output.WriteLine("  Rate:           " + DescribeRate(purchase.Rate));
            if (purchase.LeaseStartHeight is { } start)
                _output.WriteLine(string.Format(s_inv, "  Lease start:    block {0}", start));
            _output.WriteLine("  Lease:          " + DescribeLease(purchase, item.CurrentHeight));
            _output.WriteLine("  Created:        "
                            + purchase.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", s_inv));
        }
    }

    private void WriteRates(IReadOnlyList<FundingRateIpcInfo> rates, string indent)
    {
        for (var i = 0; i < rates.Count; i++)
            _output.WriteLine(string.Format(s_inv, "{0}Rate {1}:  {2}", indent, i + 1, DescribeRate(rates[i])));
    }

    private static string DescribeRate(FundingRateIpcInfo rate) =>
        string.Format(s_inv,
                      "{0}-{1} sat, fee base {2} sat + {3} basis points, channel creation fee {4} sat, "
                    + "funding weight {5}", rate.MinAmountSat, rate.MaxAmountSat, rate.FeeBaseSat, rate.FeeBasisPoints,
                      rate.ChannelCreationFeeSat, rate.FundingWeight);
}