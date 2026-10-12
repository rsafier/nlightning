namespace NLightning.Domain.Bitcoin.SilentPayments.Interfaces;

using Models;

public interface ISilentPaymentService
{
    Task<SilentPaymentAddressResult> GetAddressAsync(string? labelName = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SilentPaymentLabelInfo>> ListLabelsAsync(CancellationToken cancellationToken = default);
    Task<SilentPaymentStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<SilentPaymentStatus> StartRescanAsync(uint fromHeight, uint? recoveryLabels = null,
                                              CancellationToken cancellationToken = default);
    Task<SilentPaymentStatus> CancelRescanAsync(CancellationToken cancellationToken = default);
}