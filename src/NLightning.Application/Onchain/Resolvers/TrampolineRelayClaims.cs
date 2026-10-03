namespace NLightning.Application.Onchain.Resolvers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Payments.Trampoline;

/// <summary>
/// The trampoline side of the preimage claims and upstream checks of the local and remote commitment resolvers
/// (NL-875): an incoming HTLC that is a part of a trampoline relay answers to the relay's outgoing payment, as a
/// forwarded HTLC answers to its outgoing HTLC.
/// </summary>
internal static class TrampolineRelayClaims
{
    /// <summary>
    /// The preimage we may claim the relay part <paramref name="record"/> with: the one on its own record (the relay
    /// committed it to every part, NL-322), the relay's stored preimage, or the one any outgoing HTLC of the relay's
    /// payment learnt downstream (its <c>KnownPreimage</c> or fulfill, live or archived; a preimage seen on the
    /// downstream chain is staged there too). The switch cannot fulfill the part off chain once its channel is closed,
    /// so these are where the preimage is. Null when the HTLC is not a relay part, was failed off chain, or no preimage
    /// is known.
    /// </summary>
    /// <param name="unitOfWork">The round's unit of work.</param>
    /// <param name="channelId">The incoming HTLC's channel.</param>
    /// <param name="record">The incoming HTLC's record.</param>
    /// <param name="findRecord">Looks up an outgoing HTLC's record (live, else archived).</param>
    public static async Task<byte[]?> GetRelayPreimageAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                            HtlcRecord? record,
                                                            Func<ChannelId, HtlcKey, Task<HtlcRecord?>> findRecord)
    {
        if (record is not { Direction: HtlcDirection.Incoming } || record.Removal is { IsFulfill: false }
         || await TrampolineRelayReads.GetPartAsync(unitOfWork, channelId, record.Id) is not { } part
         || part.PaymentHash != record.PaymentHash)
            return null;

        if (record.KnownPreimage is { } own && TrampolineRelayReads.Hashes(own, record.PaymentHash))
            return own;

        if (await TrampolineRelayReads.GetAsync(unitOfWork, part.PaymentHash) is { Relay.Preimage: { } stored }
         && TrampolineRelayReads.Hashes(stored, record.PaymentHash))
            return stored;

        foreach (var (outgoingChannelId, key) in await TrampolineRelayReads.FindOutgoingAsync(unitOfWork,
                     part.PaymentHash))
        {
            if (TrampolineRelayReads.OutgoingPreimage(await findRecord(outgoingChannelId, key), part.PaymentHash) is
                { } learnt)
                return learnt;
        }

        return null;
    }

    /// <summary>
    /// Whether the upstream of an outgoing HTLC of the relay with <paramref name="relayHash"/> no longer needs its
    /// event: every incoming part has our removal, is final or gone, or sits on a channel that is no longer open while
    /// the relay is completed (that channel's own resolver claims it with the relay's preimage, or lets it time out).
    /// False while any part still waits on an open channel, and when the relay is unknown (the events are idempotent,
    /// so raising them again is harmless).
    /// </summary>
    public static async Task<bool> IsUpstreamResolvedAsync(IUnitOfWork unitOfWork, Hash relayHash)
    {
        if (await TrampolineRelayReads.GetAsync(unitOfWork, relayHash) is not { } relay)
            return false;

        foreach (var part in relay.Parts)
        {
            var incomingChannel = await unitOfWork.ChannelDbRepository.GetByIdAsync(part.ChannelId);
            var incoming = incomingChannel?.Commitments?.GetHtlc(HtlcDirection.Incoming, part.HtlcId);
            if (incoming is null || incoming.Removal is not null || HtlcStateTable.IsFinal(incoming.State))
                continue;

            if (incomingChannel!.State == ChannelState.Open || !relay.Relay.IsCompleted)
                return false;
        }

        return true;
    }

    /// <summary>An HTLC record of a channel: the live one, else its archived (settled, unpruned) row.</summary>
    public static async Task<HtlcRecord?> FindRecordAsync(IUnitOfWork unitOfWork, ChannelId channelId, HtlcKey key)
    {
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        if (channel?.Commitments is not { } commitments)
            return null;

        if (commitments.GetHtlc(key.Direction, key.Id) is { } live)
            return live;

        var persisted = await unitOfWork.ChannelStateDbRepository.LoadAsync(channelId, commitments.Params);
        return persisted?.SettledHtlcs.FirstOrDefault(h => h.Key == key);
    }
}