namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Models;

/// <summary>Explicit historical indexing. Never changes wallet custody, accounting or the live chain cursor.</summary>
public interface IWalletHistoryService
{
    Task<WalletHistoryRescanState?> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<WalletHistoryRescanState> StartRescanAsync(uint fromHeight, uint? toHeight = null, bool allowPartial = false,
        uint addressCount = 30, CancellationToken cancellationToken = default);
    Task<WalletHistoryRescanState?> CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>Serializes historical confirmation writes with the live monitor's block and rewind saves.</summary>
public interface IWalletHistoryGate
{
    ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default);
}