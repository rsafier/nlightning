namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Models;

public interface IWalletAccountDbRepository
{
    Task<IReadOnlyList<WalletAccountModel>> ListAsync(CancellationToken ct = default);
    Task<WalletAccountModel?> GetAsync(string name, CancellationToken ct = default);
    Task StageAsync(WalletAccountModel account, CancellationToken ct = default);
}

/// <summary>Compatibility for read-only test wrappers that do not store accounts.</summary>
public sealed class NullWalletAccountDbRepository : IWalletAccountDbRepository
{
    public static NullWalletAccountDbRepository Instance { get; } = new();
    private NullWalletAccountDbRepository() { }
    public Task<IReadOnlyList<WalletAccountModel>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WalletAccountModel>>([]);
    public Task<WalletAccountModel?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<WalletAccountModel?>(null);
    public Task StageAsync(WalletAccountModel account, CancellationToken ct = default) =>
        throw new NotSupportedException("This unit of work does not store wallet accounts.");
}