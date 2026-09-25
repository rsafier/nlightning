namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.Transactions.Enums;
using Enums;
using Models;
using Money;
using ValueObjects;

/// <summary>
/// The balances, feerate and HTLC set that one commitment transaction commits to, from the <b>local node's</b> point of
/// view (BOLT 3 "Commitment Transaction Construction" inputs).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToLocalMsat"/> and <see cref="ToRemoteMsat"/> are <b>net</b> balances: every HTLC in <see cref="Htlcs"/>
/// has already been taken out of the balance of the side that offered it. Fees and anchors have not: the commitment
/// factory subtracts them from the funder. This is the shape of the BOLT 3 Appendix C/F vectors
/// (<c>to_local_msat</c>/<c>to_remote_msat</c>), so the vectors feed straight in.
/// </para>
/// <para>
/// An HTLC with <see cref="HtlcDirection.Outgoing"/> was offered by the local node. The commitment factory maps
/// "local"/"remote" to the commitment holder, so the same spec builds both sides' transactions.
/// </para>
/// </remarks>
public sealed class CommitmentTxSpec
{
    /// <summary>The local node's balance in millisatoshis, net of the HTLCs it offered.</summary>
    public ulong ToLocalMsat { get; }

    /// <summary>The remote node's balance in millisatoshis, net of the HTLCs it offered.</summary>
    public ulong ToRemoteMsat { get; }

    /// <summary>The commitment feerate in satoshis per 1000 weight units.</summary>
    public ulong FeeRatePerKw { get; }

    /// <summary>The HTLCs committed to (trimmed ones included; trimming is the factory's job).</summary>
    public IReadOnlyList<Htlc> Htlcs { get; }

    public CommitmentTxSpec(ulong toLocalMsat, ulong toRemoteMsat, ulong feeRatePerKw, IEnumerable<Htlc>? htlcs = null)
    {
        ToLocalMsat = toLocalMsat;
        ToRemoteMsat = toRemoteMsat;
        FeeRatePerKw = feeRatePerKw;
        Htlcs = (htlcs ?? []).ToList();
    }

    /// <summary>
    /// Builds the spec of a channel's current state. <see cref="ChannelModel"/> balances are <b>gross</b> (each side
    /// still holds its own pending offered HTLCs), so each HTLC is taken out of its offerer's balance here, never
    /// below zero.
    /// </summary>
    public static CommitmentTxSpec FromChannel(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var htlcs = new List<Htlc>();
        htlcs.AddRange(channel.LocalOfferedHtlcs ?? []);
        htlcs.AddRange(channel.RemoteOfferedHtlcs ?? []);

        var toLocalMsat = channel.LocalBalance.MilliSatoshi;
        var toRemoteMsat = channel.RemoteBalance.MilliSatoshi;
        foreach (var htlc in htlcs)
        {
            var amountMsat = htlc.Amount.MilliSatoshi;
            if (htlc.Direction == HtlcDirection.Outgoing)
                toLocalMsat = toLocalMsat > amountMsat ? toLocalMsat - amountMsat : 0;
            else
                toRemoteMsat = toRemoteMsat > amountMsat ? toRemoteMsat - amountMsat : 0;
        }

        return new CommitmentTxSpec(toLocalMsat, toRemoteMsat, (ulong)channel.ChannelParams.FeeRateAmountPerKw.Satoshi,
                                  htlcs);
    }

    /// <summary>
    /// Adapts a commitment state machine spec (<see cref="ChannelCommitments.BuildSpec"/>) to the commitment
    /// transaction factory's input (NL-230).
    /// </summary>
    /// <remarks>
    /// Both types are net of the HTLCs (each HTLC is already out of its offerer's balance) and both are from the local
    /// node's point of view (<see cref="HtlcDirection.Outgoing"/> = offered by us), so the balances and HTLCs map one to
    /// one; the holder is not part of <see cref="CommitmentTxSpec"/> and is passed to the factory as the
    /// <see cref="CommitmentSide"/>. Only the amount, payment hash, CLTV expiry, direction and id of each
    /// <see cref="Htlc"/> are set (<see cref="Htlc.AddMessage"/> is null, NL-244): the transaction factory and builders
    /// read nothing else.
    /// </remarks>
    public static CommitmentTxSpec FromCommitmentSpec(CommitmentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var htlcs = spec.Htlcs.Select(h => new Htlc(LightningMoney.MilliSatoshis(h.AmountMsat), null, h.Direction,
                                                    h.CltvExpiry, h.Id, 0, h.PaymentHash,
                                                    h.Direction == HtlcDirection.Outgoing
                                                        ? HtlcState.SentAddAckRevocation
                                                        : HtlcState.RcvdAddAckRevocation));
        return new CommitmentTxSpec(spec.LocalMsat, spec.RemoteMsat, spec.FeeratePerKw, htlcs);
    }
}