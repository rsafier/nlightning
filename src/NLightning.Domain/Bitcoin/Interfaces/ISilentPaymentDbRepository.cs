namespace NLightning.Domain.Bitcoin.Interfaces;

using ValueObjects;
using Wallet.Models;

/// <summary>All writes are staged until the owning unit of work saves.</summary>
public interface ISilentPaymentDbRepository
{
    Task<SilentPaymentOutputModel?> GetOutputAsync(TxId transactionId, uint index,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SilentPaymentOutputModel>> GetOutputsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SilentPaymentOutputModel>> GetOutputsAboveHeightAsync(uint height,
        CancellationToken cancellationToken = default);
    Task UpsertOutputAsync(SilentPaymentOutputModel output, CancellationToken cancellationToken = default);
    Task SetSpentAsync(TxId transactionId, uint index, TxId? spentByTransactionId, uint? spentAtHeight,
        CancellationToken cancellationToken = default);
    Task DeleteOutputsAboveHeightAsync(uint height, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SilentPaymentLabelModel>> GetLabelsAsync(CancellationToken cancellationToken = default);
    void AddLabel(SilentPaymentLabelModel label);
    Task<SilentPaymentScanState?> GetScanStateAsync(CancellationToken cancellationToken = default);
    Task SetScanStateAsync(SilentPaymentScanState state, CancellationToken cancellationToken = default);
}