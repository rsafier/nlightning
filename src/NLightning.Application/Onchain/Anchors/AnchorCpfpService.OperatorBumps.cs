using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Onchain.Anchors;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Fees;

/// <summary>
/// The operator's fee bump of a force close (LND's walletrpc <c>BumpForceCloseFee</c>, and <c>BumpFee</c> on our
/// anchor; NL-1186): parameters for the CPFP child of the channel's pending commitment, ours or the peer's.
/// </summary>
/// <remarks>
/// <para>The request is kept in memory (as LND's sweeper parameters) and read by every round of the channel: its starting
/// fee rate floors the estimate, its confirmation target replaces the one from the deadline, its deadline tightens (never
/// loosens) the commitment's own HTLC deadline, and its budget replaces the fee cap, except that on a commitment with an
/// HTLC deadline it never lowers the node's own cap, which protects those HTLCs. A budget lets a commitment without
/// HTLCs or stake of ours get a child (LND: "this value has to be set"). A fresh request replaces a pending child at
/// once instead of after the RBF interval; with <c>immediate</c> the round runs now, otherwise at the next block. It ends
/// with the channel's last pending commitment.</para>
/// </remarks>
public sealed partial class AnchorCpfpService
{
    /// <inheritdoc />
    public async Task<AnchorBumpOutcome> RequestBumpAsync(ChannelId channelId, OperatorFeeBumpRequest request,
                                                          (TxId TxId, uint Index)? anchor = null,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.Enabled || _operatorFeeBumps is null)
            return AnchorBumpOutcome.Disabled;
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            return AnchorBumpOutcome.UnknownChannel;
        if (!channel.ChannelParams.OptionAnchorOutputs)
            return AnchorBumpOutcome.NoAnchors;
        if (!IsRoundChannel(channel))
            return AnchorBumpOutcome.NotForceClosed;

        var pending = await GetPendingCommitmentsAsync(channelId);
        if (pending.Count == 0)
            return AnchorBumpOutcome.NotForceClosed;

        if (anchor is { } outpoint)
        {
            var commitment = pending.FirstOrDefault(p => p.TxId == outpoint.TxId);
            if (commitment.RawTransaction is null
             || FindOurAnchor(channel, commitment.TxId, commitment.RawTransaction, commitment.IsPeers,
                              commitment.Number)?.OutputIndex != outpoint.Index)
                return AnchorBumpOutcome.NotOurAnchor;
        }

        _operatorFeeBumps.SetAnchor(channelId, request);
        if (request.Immediate)
            ScheduleCommitmentRound(channelId);
        return AnchorBumpOutcome.Registered;
    }

    /// <summary>
    /// The unconfirmed commitments of a channel a child could pay for: our pending <c>LocalCommitment</c> rows, the
    /// peer's handed-over ones (stored and in memory).
    /// </summary>
    private async Task<List<(TxId TxId, byte[] RawTransaction, bool IsPeers, ulong? Number)>>
        GetPendingCommitmentsAsync(ChannelId channelId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository;
        var result = new List<(TxId TxId, byte[] RawTransaction, bool IsPeers, ulong? Number)>();
        foreach (var row in await repository.GetByChannelIdAsync(channelId))
        {
            if (row.State != BroadcastState.Pending)
                continue;

            if (row.Purpose == BroadcastPurpose.LocalCommitment)
                result.Add((row.TransactionId, row.RawTransaction.ToArray(), false, row.CommitmentNumber));
            else if (row.Purpose == BroadcastPurpose.PeerCommitment)
                result.Add((row.TransactionId, row.RawTransaction.ToArray(), true, null));
        }

        if (_peerCommitments.TryGetValue(channelId, out var seen) && result.All(r => r.TxId != seen.TxId))
            result.Add((seen.TxId, seen.RawTransaction, true, null));
        return result;
    }

    /// <summary>
    /// The operator's parameters for a child of <paramref name="parent"/>: the deadline, target, estimate floor and cap
    /// to use (see the class remarks), and whether the request is fresh.
    /// </summary>
    private (OperatorFeeBumpRequest? Request, bool Fresh) GetOperatorBump(ChannelId channelId)
    {
        if (_operatorFeeBumps?.GetAnchor(channelId, out var fresh) is not { } request)
            return (null, false);

        return (request, fresh);
    }

    /// <summary>The fee cap under the operator's budget: the budget, never below the node's own cap on a commitment
    /// with an HTLC deadline.</summary>
    private static ulong ApplyBudget(ulong ownCap, ulong? budgetSat, bool hasHtlcDeadline) =>
        budgetSat is { } budget and > 0
            ? hasHtlcDeadline ? Math.Max(budget, ownCap) : budget
            : ownCap;
}

/// <summary>What became of an operator's fee bump request of a force close (NL-1186).</summary>
public enum AnchorBumpOutcome
{
    /// <summary>The request applies from the channel's next round (now with <c>immediate</c>).</summary>
    Registered,

    /// <summary>The anchor CPFP is off (<c>Node:Onchain:Anchors:Enabled</c>) or has no request store.</summary>
    Disabled,

    /// <summary>No such channel is loaded.</summary>
    UnknownChannel,

    /// <summary>The channel has no anchors: its commitment cannot be fee-bumped.</summary>
    NoAnchors,

    /// <summary>The channel has no unconfirmed commitment to bump.</summary>
    NotForceClosed,

    /// <summary>The outpoint is not our anchor of the channel's unconfirmed commitment.</summary>
    NotOurAnchor
}