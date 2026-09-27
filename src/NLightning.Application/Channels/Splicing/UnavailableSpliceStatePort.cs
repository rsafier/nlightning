namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;

/// <summary>
/// The default <see cref="ISpliceStatePort"/> until lanes SP1-B (engine) and SP1-C (signer, <c>ChannelFundings</c>)
/// land: a channel was never spliced (<see cref="FundingSet.Single"/> of its funding output), and every step that
/// needs the engine, the per-funding signer or the schema throws <see cref="NotImplementedException"/> naming the lane.
/// A splice started on it stops at its commitment step with our <c>tx_abort</c> (the driver aborts a negotiation whose
/// commitment step throws), before anything is signed.
/// </summary>
public sealed class UnavailableSpliceStatePort : ISpliceStatePort
{
    /// <inheritdoc />
    public FundingSet GetFundings(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var current = channel.FundingOutput is { } output ? ChannelFunding.FromFundingOutput(output) : null;
        return current is null
                   ? throw new InvalidOperationException(
                         $"The funding outpoint of channel {channel.ChannelId} is unknown")
                   : FundingSet.Single(current);
    }

    /// <inheritdoc />
    public Task<CommitmentSignedMessage> SignSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                                                   IUnitOfWork unitOfWork,
                                                                   CancellationToken cancellationToken) =>
        throw new NotImplementedException("Lanes SP1-B (SP1-B-T3) and SP1-C (SP1-C-T1): splice commitment signing");

    /// <inheritdoc />
    public Task ReceiveSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                             CommitmentSignedMessage message, IUnitOfWork unitOfWork,
                                             CancellationToken cancellationToken) =>
        throw new NotImplementedException("Lanes SP1-B (SP1-B-T3) and SP1-C (SP1-C-T1): splice commitment checks");

    /// <inheritdoc />
    public void OnSpliceCommitmentSaved(ChannelModel channel, ChannelFunding funding) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T2): SP-I1 in the signer");

    /// <inheritdoc />
    public FundingSet AddPending(FundingSet fundings, ChannelFunding funding) =>
        throw new NotImplementedException("Lane SP1-B (SP1-B-T1): FundingSet.AddPending");

    /// <inheritdoc />
    public (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Lock(FundingSet fundings, TxId fundingTxId) =>
        throw new NotImplementedException("Lane SP1-B (SP1-B-T3): FundingSet.Lock");

    /// <inheritdoc />
    public Task StageFundingsAsync(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired,
                                   IUnitOfWork unitOfWork, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Lane SP1-C (SP1-C-T4): ChannelFundings rows");

    /// <inheritdoc />
    public void ApplyFundings(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired) =>
        throw new NotImplementedException("Lanes SP1-B and SP1-C: fundings in the engine and the signer");
}