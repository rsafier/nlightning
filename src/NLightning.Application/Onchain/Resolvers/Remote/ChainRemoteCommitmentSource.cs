using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Reads the commitment from the block the close record names (<see cref="ChannelCloseModel.SpentAtHeight"/>), so no
/// transaction index is needed. When the block at that height no longer holds it (a reorg the rewind has not applied
/// yet) it gives nothing.
/// </summary>
public sealed class ChainRemoteCommitmentSource : IRemoteCommitmentSource
{
    private readonly IBitcoinChainService _chainService;
    private readonly ILogger<ChainRemoteCommitmentSource> _logger;

    public ChainRemoteCommitmentSource(IBitcoinChainService chainService, ILogger<ChainRemoteCommitmentSource> logger)
    {
        _chainService = chainService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChainTx?> GetCommitmentAsync(ChannelCloseModel close, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(close);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var block = await _chainService.GetBlockAsync(close.SpentAtHeight);
            if (block is null)
                return null;

            var txId = new uint256((byte[])close.CommitmentTransactionId);
            var transaction = block.Transactions.FirstOrDefault(t => t.GetHash() == txId);
            return transaction is null ? null : ChainTxMapper.FromTransaction(transaction);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Cannot read the commitment of channel {ChannelId} from block {Height}",
                               close.ChannelId, close.SpentAtHeight);
            return null;
        }
    }
}