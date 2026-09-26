namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Models;

/// <summary>
/// The default <see cref="IAnchorFeeInputProvider"/> until the host registers the wallet's: it selects nothing, so an
/// anchors HTLC transaction is not broadcast and its output is retried every block. It logs nothing itself: the
/// resolver warns once per HTLC output and process.
/// </summary>
public sealed class UnavailableAnchorFeeInputProvider : IAnchorFeeInputProvider
{
    /// <inheritdoc />
    public Task<AnchorFeeInputSelection?> SelectAsync(AnchorFeeInputOwner owner, long baseWeight, uint feeratePerKw,
                                                      CancellationToken cancellationToken) =>
        Task.FromResult<AnchorFeeInputSelection?>(null);

    /// <inheritdoc />
    public Task<SignedTransaction> SignAsync(SignedTransaction transaction, IReadOnlyList<AnchorFeeInput> feeInputs,
                                             CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No fee-input provider is registered");

    /// <inheritdoc />
    public Task ReleaseAsync(AnchorFeeInputOwner owner, CancellationToken cancellationToken) => Task.CompletedTask;
}