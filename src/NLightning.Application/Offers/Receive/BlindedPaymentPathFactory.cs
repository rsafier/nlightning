using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers.Receive;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Payments.Invoices;

/// <summary>
/// The blinded payment paths of one BOLT 12 invoice (plan B3-T3, §3.7 step 3).
/// </summary>
public interface IBlindedPaymentPathSource
{
    /// <summary>
    /// Paths to us for the invoice with <paramref name="preimage"/> (our hop's <c>path_id</c> is
    /// <see cref="BlindedPathId"/> of it), able to carry <paramref name="amount"/>, usable for at least
    /// <paramref name="relativeExpirySeconds"/>.
    /// </summary>
    /// <returns>The paths, most preferred first; empty when none can be made.</returns>
    Task<IReadOnlyList<BlindedPaymentPath>> CreateAsync(Secret preimage, LightningMoney amount,
                                                        uint relativeExpirySeconds,
                                                        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IBlindedPaymentPathSource"/> over M5's <see cref="BlindedPathBuilder"/>: one two-hop path per usable
/// channel (announced or not: the payer usually reaches the introduction node, our peer, through the graph or is that
/// peer itself), its peer's <c>channel_update</c> policy as <c>payment_relay</c>, <c>payment_constraints</c> for the
/// invoice's lifetime, and <c>blinded_payinfo</c> aggregated with BOLT 4's rounding plus our final CLTV delta
/// (<see cref="RoutingOptions.InvoiceMinFinalCltvExpiry"/>, which the final hop checks).
/// </summary>
/// <remarks>
/// Lifetime in blocks: the relative expiry at one block per 10 minutes, rounded up, plus
/// <see cref="OfferOptions.PathLifetimeMarginBlocks"/>. No path without a processed block (height 0).
/// </remarks>
public sealed class BlindedPaymentPathFactory : IBlindedPaymentPathSource
{
    private const uint SecondsPerBlock = 600;

    private readonly BlindedPathBuilder _builder;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly OfferOptions _offerOptions;
    private readonly IBlockchainMonitor? _blockchainMonitor;

    public BlindedPaymentPathFactory(BlindedPathBuilder builder, IOptions<NodeOptions> nodeOptions,
                                     IOptions<OfferOptions>? offerOptions = null,
                                     IBlockchainMonitor? blockchainMonitor = null)
    {
        _builder = builder;
        _nodeOptions = nodeOptions;
        _offerOptions = offerOptions?.Value ?? new OfferOptions();
        _blockchainMonitor = blockchainMonitor;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BlindedPaymentPath>> CreateAsync(Secret preimage, LightningMoney amount,
                                                                     uint relativeExpirySeconds,
                                                                     CancellationToken cancellationToken = default)
    {
        var height = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        if (height == 0)
            return [];

        var lifetime = checked((relativeExpirySeconds + SecondsPerBlock - 1) / SecondsPerBlock
                             + _offerOptions.PathLifetimeMarginBlocks);
        var request = new BlindedPathRequest(preimage, amount,
                                             _nodeOptions.Value.Routing.InvoiceMinFinalCltvExpiry, height,
                                             Math.Max(lifetime, 1), _offerOptions.MaxPaymentPaths,
                                             IncludePrivateChannels: true);
        return await _builder.BuildAsync(request, cancellationToken);
    }
}