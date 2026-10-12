using System.Security.Cryptography;

namespace NLightning.Application.Tests.Channels.Fees;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Commitment snapshots for the fee and dust exposure tests, restored with HTLCs in the states a test needs.
/// </summary>
internal static class FeeTestKit
{
    public const ulong Sat = 1_000;
    public const ulong DustLimitSat = 546;

    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x5F, 32).ToArray());

    public static CommitmentParty Party(ulong dustSat = DustLimitSat, ulong reserveSat = 10_000) =>
        new(dustSat, reserveSat, 1_000, 30, ulong.MaxValue);

    /// <summary>
    /// A snapshot with settled balances <paramref name="localSat"/>/<paramref name="remoteSat"/> (HTLCs stay in their
    /// offerer's balance) and the given HTLCs, at <paramref name="feeratePerKw"/> on both commitments.
    /// </summary>
    public static ChannelCommitments Create(ulong localSat, ulong remoteSat, uint feeratePerKw,
                                            bool localIsFunder = true, bool anchors = false,
                                            ulong? maxDustMsat = null, CommitmentParty? local = null,
                                            CommitmentParty? remote = null, IEnumerable<HtlcRecord>? htlcs = null)
    {
        var records = (htlcs ?? []).ToList();
        var @params = new CommitmentParams(localIsFunder, localSat + remoteSat, anchors, local ?? Party(),
                                           remote ?? Party(), maxDustMsat);
        var feeOwner = localIsFunder ? HtlcState.SentAddAckRevocation : HtlcState.RcvdAddAckRevocation;
        var localNext = NextId(records, HtlcDirection.Outgoing);
        var remoteNext = NextId(records, HtlcDirection.Incoming);
        var localSpec = new CommitmentSpec(CommitmentSide.Local, feeratePerKw, localSat * Sat, remoteSat * Sat, []);
        var remoteSpec = new CommitmentSpec(CommitmentSide.Remote, feeratePerKw, localSat * Sat, remoteSat * Sat, []);
        return ChannelCommitments.Restore(ChannelId, @params, localSat * Sat, remoteSat * Sat, records,
                                          [new FeeUpdate(0, feeratePerKw, feeOwner)], localNext, remoteNext,
                                          new LocalCommit(1, localSpec, null),
                                          new RemoteCommit(1, remoteSpec, Point(0x20)), null, Point(0x21));
    }

    /// <summary>An HTLC the peer offered, locked in by default.</summary>
    public static HtlcRecord Incoming(ulong id, ulong amountSat, HtlcState state = HtlcState.RcvdAddAckRevocation) =>
        new(HtlcDirection.Incoming, id, amountSat * Sat, PaymentHash(id), 600, state,
            OnionRoutingPacket: new byte[1366]);

    /// <summary>An HTLC we offered, locked in by default.</summary>
    public static HtlcRecord Outgoing(ulong id, ulong amountSat, HtlcState state = HtlcState.SentAddAckRevocation) =>
        new(HtlcDirection.Outgoing, id, amountSat * Sat, PaymentHash(100 + id), 600, state,
            OnionRoutingPacket: new byte[1366]);

    public static Hash PaymentHash(ulong tag) => new(SHA256.HashData(BitConverter.GetBytes(tag)));

    public static CompactPubKey Point(byte tag)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[1] = tag;
        return bytes;
    }

    private static ulong NextId(IEnumerable<HtlcRecord> records, HtlcDirection direction) =>
        records.Where(r => r.Direction == direction).Select(r => r.Id + 1).DefaultIfEmpty(0UL).Max();
}