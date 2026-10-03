using Microsoft.Extensions.Logging;

namespace NLightning.Application.Onchain;

using Channels.Services;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;

/// <summary>
/// The fundings of a channel as the BOLT 5 side sees them (splicing plan §3.6, SP2-C): every funding output a commitment
/// of the channel may spend (the current one, pending splices, and retired ones kept for SP-I5), and the rebuild of a
/// commitment on a given funding (its outpoint, capacity and funding keys, which the anchors are keyed to).
/// </summary>
internal static class OnchainFundings
{
    /// <summary>
    /// Every funding of the channel: the current one first (the stored row when there is one, else built from
    /// <see cref="ChannelModel.FundingOutput"/>), then the others in creation order (pending, replaced, discarded),
    /// plus pending fundings the engine holds that are not stored. Empty without a funding outpoint.
    /// </summary>
    /// <remarks>A unit of work that keeps no funding rows (test doubles) gives the current funding and the engine's
    /// pending ones.</remarks>
    public static async Task<IReadOnlyList<ChannelFunding>> GetAllAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                                        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.FundingOutput is not { TransactionId: { } currentTxId } fundingOutput
         || ChannelFunding.FromFundingOutput(fundingOutput) is not { } synthesized)
            return [];

        IReadOnlyList<ChannelFunding> stored = [];
        try
        {
            if (unitOfWork.ChannelFundingDbRepository is { } repository)
                stored = await repository.GetByChannelIdAsync(channel.ChannelId) ?? [];
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            // A unit of work without funding rows: the channel's single funding
        }
        catch (Exception e) when (e is InvalidOperationException)
        {
            logger?.LogWarning(e, "Could not read the fundings of channel {ChannelId}", channel.ChannelId);
        }

        var current = stored.FirstOrDefault(f => f.FundingTxId == currentTxId && f.OutputIndex == fundingOutput.Index)
                   ?? synthesized;
        var all = new List<ChannelFunding> { current with { Status = ChannelFundingStatus.Current } };
        all.AddRange(stored.Where(f => f.FundingTxId != currentTxId));
        if (channel.Commitments?.PendingFundings is { IsEmpty: false } pending)
            all.AddRange(pending.Where(p => all.All(f => f.FundingTxId != p.FundingTxId)));

        return all;
    }

    /// <summary>True when <paramref name="funding"/> is the channel's current funding output.</summary>
    public static bool IsCurrent(ChannelModel channel, ChannelFunding funding) =>
        channel.FundingOutput is { TransactionId: { } txId, Index: { } index } && txId == funding.FundingTxId
                                                                         && index == funding.OutputIndex;

    /// <summary>The funding whose output an input of <paramref name="transaction"/> spends, or null.</summary>
    public static ChannelFunding? FindSpent(IReadOnlyList<ChannelFunding> fundings, ChainTx transaction)
    {
        ArgumentNullException.ThrowIfNull(fundings);
        ArgumentNullException.ThrowIfNull(transaction);
        return fundings.FirstOrDefault(f => transaction.IndexOfInputSpending(f.FundingTxId, f.OutputIndex) >= 0);
    }

    /// <summary>The funding whose outpoint is <paramref name="txId"/>:<paramref name="vout"/>, or null.</summary>
    public static ChannelFunding? Find(IReadOnlyList<ChannelFunding> fundings, TxId txId, uint vout) =>
        fundings.FirstOrDefault(f => f.FundingTxId == txId && f.OutputIndex == vout);

    /// <summary>
    /// The splice transactions of the channel that may spend <paramref name="funding"/>'s output: every splice funding
    /// but itself (a splice spends only the funding that was current when it was negotiated; a txid spends an outpoint
    /// only when it really does, so listing the others is harmless).
    /// </summary>
    public static IReadOnlyCollection<TxId> SpliceTxIdsFor(IReadOnlyList<ChannelFunding> fundings,
                                                           ChannelFunding funding) =>
        fundings.Where(f => f.Kind is ChannelFundingKind.Splice or ChannelFundingKind.SpliceRbf
                         && f.FundingTxId != funding.FundingTxId)
                .Select(f => f.FundingTxId)
                .ToList();

    /// <summary>
    /// <see cref="ICommitmentOutputMapper.Map(ChannelModel, CommitmentTxSpec, CommitmentCase, ulong, CompactPubKey?, ChainTx?)"/>
    /// for the commitment spending <paramref name="funding"/>: the channel's current funding (or null, or no model
    /// factory) maps exactly as before; another funding is rebuilt against its own outpoint, capacity and funding keys
    /// (the anchors are keyed to them), as it was signed (<c>CommitmentSigningService</c>).
    /// </summary>
    public static CommitmentOutputMap Map(ICommitmentOutputMapper mapper, ICommitmentTransactionModelFactory? factory,
                                          ChannelModel channel, ChannelFunding? funding, CommitmentTxSpec spec,
                                          CommitmentCase commitmentCase, ulong number, CompactPubKey? point,
                                          ChainTx? onChain = null)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(channel);
        if (funding is null || factory is null || IsCurrent(channel, funding))
            return mapper.Map(channel, spec, commitmentCase, number, point, onChain);

        var side = commitmentCase == CommitmentCase.Local ? CommitmentSide.Local : CommitmentSide.Remote;
        var model = side == CommitmentSide.Local
                        ? factory.CreateCommitmentTransactionModel(channel, spec, side, number)
                        : factory.CreateCommitmentTransactionModel(
                            channel, spec, side, number,
                            point ?? throw new ArgumentNullException(nameof(point),
                                                                     "A peer commitment needs its per-commitment point"));
        model = CommitmentSigningService.WithFunding(model, funding, side);
        return mapper.Map(model, spec.Htlcs, commitmentCase, onChain);
    }
}