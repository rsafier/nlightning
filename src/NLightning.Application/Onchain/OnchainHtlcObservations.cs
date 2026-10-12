using System.Buffers.Binary;

namespace NLightning.Application.Onchain;

using Accounting;
using Domain.Accounting.Constants;
using Domain.Bitcoin.Events;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Persistence.Interfaces;

/// <summary>
/// Stages first-known outcome checkpoints under the executor's channel lock. The caller saves them with the
/// resolution, then fans out to readers captured before that save. Switch recovery actions remain untouched.
/// </summary>
internal static class OnchainHtlcObservations
{
    internal static async Task<IReadOnlyList<HtlcActivityEvent>> StageAsync(
        IUnitOfWork unitOfWork, ChannelModel channel, IReadOnlyList<OutputResolverAction> actions,
        IReadOnlyCollection<OutputResolutionModel> outputs, IChannelMemoryRepository channels, DateTimeOffset now,
        ChannelCloseModel close, uint height, uint reasonableDepth, OutpointSpentEventArgs? spent = null)
    {
        var activities = new List<HtlcActivityEvent>();
        var seen = new HashSet<(HtlcDirection, ulong, bool)>();
        foreach (var action in actions.OfType<RaiseChannelEventAction>())
        {
            var (id, settled, preimage) = action.Event switch
            {
                OutgoingHtlcFulfilled fulfill => ((ulong?)fulfill.HtlcId, true, (byte[])fulfill.PaymentPreimage),
                OutgoingHtlcFailed fail => ((ulong?)fail.HtlcId, false, Array.Empty<byte>()),
                _ => ((ulong?)null, false, Array.Empty<byte>())
            };
            if (id is not { } htlcId || !seen.Add((HtlcDirection.Outgoing, htlcId, settled))
             || await unitOfWork.OnchainHtlcObservationDbRepository.ContainsAsync(
                    channel.ChannelId, HtlcDirection.Outgoing, htlcId, settled))
                continue;

            var origin = await unitOfWork.ChannelStateDbRepository.GetHtlcOriginAsync(
                channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, htlcId));
            var circuit = origin is
            {
                Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } input,
                IncomingHtlcId: { } incomingId
            }
                ? await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(input, incomingId) : null;
            var incomingScid = circuit is not null
                ? await ScidAsync(unitOfWork, channels, circuit.IncomingChannelId) : 0;
            var record = channel.Commitments?.GetHtlc(HtlcDirection.Outgoing, htlcId);
            var role = origin?.Kind switch
            {
                HtlcOriginKind.Local => HtlcActivityRole.Send,
                HtlcOriginKind.Forwarded => HtlcActivityRole.Forward,
                _ => HtlcActivityRole.Unknown
            };
            activities.Add(new HtlcActivityEvent(settled ? HtlcActivityKind.Settle : HtlcActivityKind.ForwardFail,
                role, incomingScid, circuit?.IncomingHtlcId ?? 0, Scid(channel), htlcId, now,
                circuit?.IncomingAmount.MilliSatoshi ?? 0, circuit?.IncomingCltvExpiry ?? 0,
                record?.AmountMsat ?? circuit?.OutgoingAmount.MilliSatoshi ?? 0,
                record?.CltvExpiry ?? circuit?.OutgoingCltvExpiry ?? 0, preimage, Settled: settled, Offchain: false));
            unitOfWork.OnchainHtlcObservationDbRepository.Add(new OnchainHtlcObservationModel(
                channel.ChannelId, HtlcDirection.Outgoing, htlcId, settled, now));
        }

        foreach (var output in outputs.Where(output => output.HtlcDirection == HtlcDirection.Incoming
            && output.HtlcId.HasValue
            && output.Descriptor is OutputDescriptorKind.LocalReceivedHtlc or OutputDescriptorKind.RemoteOfferedHtlc
            && output.State is OutputResolutionState.Resolved or OutputResolutionState.Irrevocable
                            or OutputResolutionState.Ignored))
        {
            var htlcId = output.HtlcId!.Value;
            var watch = output.State == OutputResolutionState.Ignored ? null
                : await unitOfWork.WatchedOutpointDbRepository.GetAsync(output.TransactionId, output.OutputIndex);
            // A peer timeout or an output we gave up is failed even when we knew the preimage off chain.
            var spender = spent is not null && spent.SpentTransactionId == output.TransactionId
                       && spent.SpentOutputIndex == output.OutputIndex
                ? spent.SpendingTransaction.TxId : watch?.SpentByTransactionId;
            if (output.State != OutputResolutionState.Ignored && spender is null)
                continue; // No committed spending fact yet; never invent a failure from missing metadata.
            var broadcast = spender is { } txid
                ? await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txid) : null;
            var settled = spender is not null && (output.ResolvingTransactionId == spender
                || broadcast is { Purpose: BroadcastPurpose.HtlcTransaction or BroadcastPurpose.Sweep });
            if (!seen.Add((HtlcDirection.Incoming, htlcId, settled))
             || await unitOfWork.OnchainHtlcObservationDbRepository.ContainsAsync(
                    channel.ChannelId, HtlcDirection.Incoming, htlcId, settled))
                continue;
            activities.Add(new HtlcActivityEvent(HtlcActivityKind.Final, HtlcActivityRole.Unknown,
                Scid(channel), htlcId, 0, 0, now, Settled: settled, Offchain: false));
            unitOfWork.OnchainHtlcObservationDbRepository.Add(new OnchainHtlcObservationModel(
                channel.ChannelId, HtlcDirection.Incoming, htlcId, settled, now));
        }
        // The close writer records positively identified incoming HTLCs omitted by dust trimming. Unknown or
        // unmapped data-loss outputs never enter that metadata, so absence alone cannot turn into a failure.
        if (height >= close.SpentAtHeight && (ulong)height - close.SpentAtHeight + 1 >= reasonableDepth
         && unitOfWork.AccountingEventDbRepository is { } accounting)
        {
            var closeEvent = await OnchainAccounting.FindAsync(accounting,
                AccountingEventKeys.ChannelForceClosed(channel.ChannelId, close.CommitmentTransactionId),
                close.SpentAtHeight, CancellationToken.None);
            foreach (var (htlcId, _) in OnchainAccounting.TrimmedIncomingHtlcsOf(closeEvent))
            {
                if (outputs.Any(output => output.HtlcDirection == HtlcDirection.Incoming && output.HtlcId == htlcId)
                 || !seen.Add((HtlcDirection.Incoming, htlcId, false))
                 || await unitOfWork.OnchainHtlcObservationDbRepository.ContainsAsync(
                        channel.ChannelId, HtlcDirection.Incoming, htlcId, false))
                    continue;
                activities.Add(new HtlcActivityEvent(HtlcActivityKind.Final, HtlcActivityRole.Unknown,
                    Scid(channel), htlcId, 0, 0, now, Settled: false, Offchain: false));
                unitOfWork.OnchainHtlcObservationDbRepository.Add(new OnchainHtlcObservationModel(
                    channel.ChannelId, HtlcDirection.Incoming, htlcId, false, now));
            }
        }
        return activities;
    }

    private static async Task<ulong> ScidAsync(IUnitOfWork unitOfWork, IChannelMemoryRepository channels,
                                              ChannelId channelId)
    {
        if (channels.TryGetChannel(channelId, out var loaded))
            return Scid(loaded);
        var stored = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        return stored is null ? 0 : Scid(stored);
    }

    private static ulong Scid(ChannelModel channel)
    {
        var scid = ToUlong(channel.ShortChannelId);
        return scid != 0 ? scid : ToUlong(channel.LocalAliases?.FirstOrDefault() ?? default);
    }

    private static ulong ToUlong(ShortChannelId scid) =>
        ((byte[])scid) is { Length: 8 } bytes ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : 0;
}