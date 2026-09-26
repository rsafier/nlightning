using Microsoft.Extensions.Logging;

namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Models;

/// <summary>
/// The default <see cref="IAnchorFeeInputProvider"/> until the host registers the wallet's: it selects nothing, so an
/// anchors HTLC transaction is not broadcast and its output is retried every block (logged).
/// </summary>
public sealed class UnavailableAnchorFeeInputProvider : IAnchorFeeInputProvider
{
    private readonly ILogger<UnavailableAnchorFeeInputProvider> _logger;

    public UnavailableAnchorFeeInputProvider(ILogger<UnavailableAnchorFeeInputProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<AnchorFeeInputSelection?> SelectAsync(ChannelId channelId, long baseWeight, uint feeratePerKw,
                                                      CancellationToken cancellationToken)
    {
        _logger.LogWarning("No wallet fee inputs are available for the anchors HTLC transaction of channel {ChannelId}: "
                         + "no fee-input provider is registered", channelId);
        return Task.FromResult<AnchorFeeInputSelection?>(null);
    }

    /// <inheritdoc />
    public Task<SignedTransaction> SignAsync(SignedTransaction transaction, IReadOnlyList<AnchorFeeInput> feeInputs,
                                             CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No fee-input provider is registered");

    /// <inheritdoc />
    public Task ReleaseAsync(IReadOnlyList<AnchorFeeInput> feeInputs, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}