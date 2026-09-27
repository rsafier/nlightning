using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

/// <summary>
/// Where <see cref="FundingOutputLookup"/> reads the txid at a short channel id's block position when it is not
/// bitcoind's own txid list (BOLT 7 plan D12: nodes without an unpruned bitcoind). The answer must already be proven
/// against our own node's chain (the block hash from <c>getblockhash</c>); the lookup then asks our node's
/// <c>gettxout</c> for the output's script, amount, height and spentness, so the source is never trusted for those.
/// </summary>
/// <remarks>
/// A source throws for anything transient (index down, rate limited, behind our tip, a proof that does not match our
/// node's header): the lookup answers <c>ChainUnavailable</c>, which never counts against the announcing peer. Only a
/// position our own node proves out of range may be <see cref="FundingTxIdStatus.IndexOutOfRange"/>.
/// </remarks>
public interface IFundingTxIdSource
{
    /// <summary>The txid at <paramref name="index"/> in our active chain's block at <paramref name="height"/>.</summary>
    Task<FundingTxIdAtPosition> GetTxIdAsync(uint height, uint index, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of <see cref="IFundingTxIdSource.GetTxIdAsync"/>.</summary>
public enum FundingTxIdStatus : byte
{
    /// <summary>The txid at the position, proven against the block of our active chain.</summary>
    Found = 0,

    /// <summary>The block's data is not available (a pruned bitcoind).</summary>
    BlockUnavailable = 1,

    /// <summary>Our own node says the block has no transaction at that index.</summary>
    IndexOutOfRange = 2
}

/// <summary>A txid at a block position, with the hash of the block it was proven in.</summary>
public readonly record struct FundingTxIdAtPosition(FundingTxIdStatus Status, uint256? BlockHash, uint256? TxId)
{
    public static FundingTxIdAtPosition BlockUnavailable => new(FundingTxIdStatus.BlockUnavailable, null, null);

    public static FundingTxIdAtPosition IndexOutOfRange => new(FundingTxIdStatus.IndexOutOfRange, null, null);

    public static FundingTxIdAtPosition Found(uint256 blockHash, uint256 txId) =>
        new(FundingTxIdStatus.Found, blockHash, txId);
}