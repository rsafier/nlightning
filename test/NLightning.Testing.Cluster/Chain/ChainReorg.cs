namespace NLightning.Testing.Cluster.Chain;

/// <summary>What the competing branch of a <see cref="RegtestChain.ReorgAsync"/> holds.</summary>
public enum ReorgTransactions
{
    /// <summary>
    /// The disconnected blocks' transactions are mined again on the new branch (with the rest of the mempool): the
    /// usual "the funding transaction confirms at another height" case.
    /// </summary>
    Remine,

    /// <summary>
    /// The new branch's blocks are empty (<c>generateblock</c> with no transactions): the disconnected transactions stay
    /// unconfirmed in the mempool.
    /// </summary>
    /// <remarks>
    /// Only while they are final at the fork: bitcoind's wallet sets a send's lock time to the tip height at the time
    /// (anti fee sniping), so a fork below that height drops the transaction from the mempool (it is not final there),
    /// and a coinbase spend that is immature at the fork goes too.
    /// </remarks>
    Drop
}

/// <summary>How <see cref="RegtestChain.ReorgAsync"/> builds the competing branch.</summary>
public sealed record ReorgOptions
{
    /// <summary>The competing branch's length; null is the disconnected depth + 1 (a longer branch, as a real reorg).</summary>
    public int? NewBlocks { get; init; }

    public ReorgTransactions Transactions { get; init; } = ReorgTransactions.Remine;

    /// <summary>
    /// Transactions (mempool txids or raw hex) for the first block of the new branch, mined with exactly these
    /// (overrides <see cref="Transactions"/> for that block); null for none.
    /// </summary>
    public IReadOnlyList<string>? FirstBlockTransactions { get; init; }
}

/// <summary>
/// A reorg <see cref="RegtestChain.ReorgAsync"/> made: the fork point, the blocks it disconnected (the first of them is
/// invalidated), other branches it had to invalidate as well, and the new branch.
/// <see cref="RegtestChain.ReconsiderAsync"/> brings the invalidated branches back; one becomes active again only when
/// it has more work than the new one.
/// </summary>
/// <param name="ForkHeight">The last block both branches share.</param>
/// <param name="OldTip">The tip before.</param>
/// <param name="NewTip">The tip after.</param>
/// <param name="DisconnectedHashes">The old branch's blocks above the fork, lowest first.</param>
/// <param name="NewHashes">The new branch's blocks, lowest first.</param>
/// <param name="OtherInvalidatedHashes">
/// First blocks above the fork of other valid branches (an earlier reorg's branch brought back by
/// <c>reconsiderblock</c>) that became active when the old branch was invalidated and were invalidated too, so the new
/// branch grows on the fork block.
/// </param>
public sealed record ChainReorg(long ForkHeight, ChainTip OldTip, ChainTip NewTip,
                                IReadOnlyList<string> DisconnectedHashes, IReadOnlyList<string> NewHashes,
                                IReadOnlyList<string> OtherInvalidatedHashes)
{
    /// <summary>The block that was invalidated (the old branch's first block above the fork).</summary>
    public string InvalidatedHash => DisconnectedHashes[0];

    /// <summary>How many blocks were disconnected.</summary>
    public int Depth => DisconnectedHashes.Count;
}