using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The outpoints our own pending broadcasts spend (a funding transaction not yet mined, whose channel lock is memory
/// only and lost on a restart; a sweep; a CPFP child). The chain monitor removes a wallet output only when it processes
/// the block holding its spend, so until then every new wallet spend must skip these outputs or it conflicts with the
/// pending one. Shared by <see cref="FeeInputSelector"/> and the channel funding selection (NL-385).
/// </summary>
internal static class PendingBroadcastOutpoints
{
    public static async Task<HashSet<(TxId TxId, uint Index)>> GetAsync(IUnitOfWork uow, Network network,
                                                                        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(uow);

        var spent = new HashSet<(TxId, uint)>();
        foreach (var broadcast in await uow.BroadcastTransactionDbRepository.GetPendingAsync())
        {
            Transaction tx;
            try
            {
                tx = Transaction.Load(broadcast.RawTransaction, network);
            }
            catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException)
            {
                logger.LogWarning(e, "Pending broadcast {TxId} does not parse; its inputs are not excluded",
                                  broadcast.TransactionId);
                continue;
            }

            foreach (var input in tx.Inputs)
                spent.Add((new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N));
        }

        return spent;
    }
}