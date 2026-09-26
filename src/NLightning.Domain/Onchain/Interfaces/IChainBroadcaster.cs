namespace NLightning.Domain.Onchain.Interfaces;

using Models;

/// <summary>
/// Publishes transactions and keeps the ones that are not confirmed yet in the chain (BOLT 5 plan O0-T1, D4).
/// </summary>
/// <remarks>
/// Persist before broadcast: stage the <see cref="BroadcastTransactionModel"/> with
/// <c>IUnitOfWork.BroadcastTransactionDbRepository.Add</c> in the same save as the state change that decided it, then
/// call <see cref="PublishAsync"/> after that save (outside any channel lock). A row saved on its own goes through
/// <see cref="SaveAndPublishAsync"/>. Every stored transaction that is still <c>Pending</c> is sent again after each
/// processed block and at startup, until a processed block holds it; a refused send is therefore never lost.
/// </remarks>
public interface IChainBroadcaster
{
    /// <summary>
    /// Sends a transaction whose row the caller already saved, and keeps it for rebroadcast until it confirms.
    /// </summary>
    /// <returns>
    /// True when the node accepted it or already has it (in its mempool or chain); false when it was refused (it
    /// stays pending and is sent again after the next block). Never throws for a refused send.
    /// </returns>
    Task<bool> PublishAsync(BroadcastTransactionModel transaction);

    /// <summary>
    /// Saves the transaction's row in its own unit of work (skipped when a row for its txid exists), then does
    /// <see cref="PublishAsync"/>.
    /// </summary>
    Task<bool> SaveAndPublishAsync(BroadcastTransactionModel transaction);
}