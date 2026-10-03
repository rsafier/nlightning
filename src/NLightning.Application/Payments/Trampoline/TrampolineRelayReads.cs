using System.Security.Cryptography;

namespace NLightning.Application.Payments.Trampoline;

using Domain.Channels.Commitments;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Interfaces;
using Domain.Payments.Trampoline;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The reads the HTLC-origin consumers (switch, deadline monitor, BOLT 5 resolvers, accounting) make of the trampoline
/// relay data (NL-875): an outgoing HTLC with <c>HtlcOrigin.Trampoline(paymentHash)</c> answers to every incoming part
/// of the relay with that payment hash, and an incoming HTLC that is a relay part answers to the relay's outgoing
/// HTLCs.
/// </summary>
/// <remarks>
/// A unit of work that stores no relays (a test double: the interface's default member throws
/// <see cref="NotSupportedException"/>, a mock answers null) has no relays: every read then answers "not a relay".
/// </remarks>
internal static class TrampolineRelayReads
{
    /// <summary>The unit of work's relay repository, or null when it stores no relays.</summary>
    public static ITrampolineRelayDbRepository? TryGetRepository(IUnitOfWork unitOfWork)
    {
        try
        {
            return unitOfWork.TrampolineRelayDbRepository;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The relay part whose incoming HTLC is <paramref name="htlcId"/> on <paramref name="channelId"/>, or
    /// null (not a relay part, or no relays stored).</summary>
    public static async Task<TrampolineRelayPartModel?> GetPartAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                     ulong htlcId) =>
        TryGetRepository(unitOfWork) is { } relays ? await relays.GetPartAsync(channelId, htlcId) : null;

    /// <summary>The relay of <paramref name="paymentHash"/> with its parts, or null.</summary>
    public static async Task<(TrampolineRelayModel Relay, IReadOnlyList<TrampolineRelayPartModel> Parts)?> GetAsync(
        IUnitOfWork unitOfWork, Hash paymentHash) =>
        TryGetRepository(unitOfWork) is { } relays ? await relays.GetAsync(paymentHash) : null;

    /// <summary>The stored HTLCs of the relay's outgoing payment (every attempt, archived rows included until
    /// pruned).</summary>
    public static Task<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>> FindOutgoingAsync(IUnitOfWork unitOfWork,
        Hash paymentHash) =>
        unitOfWork.ChannelStateDbRepository.FindHtlcsByOriginAsync(HtlcOrigin.Trampoline(paymentHash));

    /// <summary>The preimage an outgoing record learnt (the peer's fulfill, kept on the record), when it is the
    /// preimage of <paramref name="paymentHash"/>.</summary>
    public static Secret? OutgoingPreimage(HtlcRecord? record, Hash paymentHash)
    {
        // Never a bare null in a conditional here: it would bind through Secret's implicit byte[] operator (NL-369)
        var preimage = record?.KnownPreimage;
        if (preimage is null && record?.Removal is { IsFulfill: true, PaymentPreimage: { } fulfilled })
            preimage = fulfilled;

        if (preimage is { } known && Hashes(known, paymentHash))
            return known;

        return null;
    }

    /// <summary>Whether <paramref name="preimage"/> is the preimage of <paramref name="paymentHash"/>.</summary>
    public static bool Hashes(Secret preimage, Hash paymentHash) =>
        SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])paymentHash);
}