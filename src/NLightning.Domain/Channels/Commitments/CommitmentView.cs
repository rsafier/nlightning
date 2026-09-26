namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.Transactions.Enums;

/// <summary>
/// A commitment content with signed balances, used while validating updates: a negative balance means the offerer
/// cannot even cover its HTLCs.
/// </summary>
internal readonly record struct CommitmentView(
    CommitmentSide Holder,
    uint FeeratePerKw,
    long LocalMsat,
    long RemoteMsat,
    IReadOnlyList<SpecHtlc> Htlcs)
{
    /// <summary>The balance of the given node (Local = us) in this view.</summary>
    public long BalanceOf(CommitmentSide node) => node == CommitmentSide.Local ? LocalMsat : RemoteMsat;

    /// <summary>Converts to a <see cref="CommitmentSpec"/>; negative balances are clamped to 0 (callers check the
    /// signed balances first).</summary>
    public CommitmentSpec ToSpec(uint? feeratePerKw = null) =>
        new(Holder, feeratePerKw ?? FeeratePerKw, (ulong)Math.Max(0, LocalMsat), (ulong)Math.Max(0, RemoteMsat),
            Htlcs);
}