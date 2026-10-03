namespace NLightning.Domain.Protocol.InteractiveTx.Interfaces;

using Bitcoin.ValueObjects;

/// <summary>
/// The serialized transaction that created one of our wallet outputs: the <c>prevtx</c> of a <c>tx_add_input</c> we
/// send (BOLT 2; splicing plan IT2-T3). The wallet keeps outpoints only, so the transaction is read from bitcoind.
/// Implemented by <c>ChainWalletPrevTxSource</c> in Infrastructure.Bitcoin.
/// </summary>
public interface IWalletPrevTxSource
{
    /// <summary>
    /// The transaction <paramref name="txId"/>, mined at <paramref name="blockHeight"/> (0 when unknown); null when
    /// bitcoind cannot serve it (neither indexed, in the mempool nor in a block it still has).
    /// </summary>
    Task<byte[]?> GetTransactionAsync(TxId txId, uint blockHeight, CancellationToken cancellationToken = default);
}