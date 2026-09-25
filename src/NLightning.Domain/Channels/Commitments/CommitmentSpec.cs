namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// The content of one commitment transaction before BOLT 3 fees, trimming and anchors: what both sides must agree on.
/// </summary>
/// <remarks>
/// Balances are from our perspective (<see cref="LocalMsat"/> is ours whichever side holds the commitment); use
/// <see cref="ToHolderMsat"/>/<see cref="ToCounterpartyMsat"/> for BOLT 3's <c>to_local</c>/<c>to_remote</c>. Every HTLC
/// in <see cref="Htlcs"/> is already debited from its offerer, so <c>LocalMsat + RemoteMsat + sum(Htlcs) ==
/// funding_msat</c> (invariant I6). Built by <see cref="ChannelCommitments.BuildSpec"/>.
/// </remarks>
public sealed class CommitmentSpec : IEquatable<CommitmentSpec>
{
    public CommitmentSide Holder { get; }
    public uint FeeratePerKw { get; }
    public ulong LocalMsat { get; }
    public ulong RemoteMsat { get; }

    /// <summary>The HTLCs, ordered by (direction, id). BOLT 3 output order is the transaction builder's job.</summary>
    public IReadOnlyList<SpecHtlc> Htlcs { get; }

    public CommitmentSpec(CommitmentSide holder, uint feeratePerKw, ulong localMsat, ulong remoteMsat,
                          IEnumerable<SpecHtlc> htlcs)
    {
        Holder = holder;
        FeeratePerKw = feeratePerKw;
        LocalMsat = localMsat;
        RemoteMsat = remoteMsat;
        Htlcs = htlcs.OrderBy(h => h.Direction).ThenBy(h => h.Id).ToArray();
    }

    /// <summary>BOLT 3 <c>to_local</c> amount (the holder's balance) before fees.</summary>
    public ulong ToHolderMsat => Holder == CommitmentSide.Local ? LocalMsat : RemoteMsat;

    /// <summary>BOLT 3 <c>to_remote</c> amount (the counterparty's balance) before fees.</summary>
    public ulong ToCounterpartyMsat => Holder == CommitmentSide.Local ? RemoteMsat : LocalMsat;

    /// <summary>Sum of all HTLC amounts in this commitment.</summary>
    public ulong HtlcTotalMsat => Htlcs.Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));

    /// <summary><c>LocalMsat + RemoteMsat + HtlcTotalMsat</c>: equals the funding amount in msat (I6).</summary>
    public ulong TotalMsat => checked(LocalMsat + RemoteMsat + HtlcTotalMsat);

    public bool Equals(CommitmentSpec? other)
    {
        if (other is null)
            return false;

        return ReferenceEquals(this, other)
            || (Holder == other.Holder && FeeratePerKw == other.FeeratePerKw && LocalMsat == other.LocalMsat
             && RemoteMsat == other.RemoteMsat && Htlcs.SequenceEqual(other.Htlcs));
    }

    public override bool Equals(object? obj) => Equals(obj as CommitmentSpec);

    public override int GetHashCode() => HashCode.Combine(Holder, FeeratePerKw, LocalMsat, RemoteMsat, Htlcs.Count);

    public override string ToString() =>
        $"{Holder} commitment: feerate {FeeratePerKw}, local {LocalMsat} msat, remote {RemoteMsat} msat, {Htlcs.Count} HTLCs";
}