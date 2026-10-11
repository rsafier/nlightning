namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Domain.Bitcoin.Wallet.Models;

/// <summary>Revalidates transient wallet ownership against Core's live mempool before unconfirmed spending.</summary>
public interface IWalletMempoolCatalog
{
    Task<IReadOnlyList<UtxoModel>> RefreshParentsAsync(IReadOnlyCollection<Domain.Bitcoin.ValueObjects.TxId> parents,
        CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);
    Task<IReadOnlyList<UtxoModel>> RefreshAsync(CancellationToken cancellationToken = default);
}