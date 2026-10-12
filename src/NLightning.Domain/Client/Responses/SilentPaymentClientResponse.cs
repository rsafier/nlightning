namespace NLightning.Domain.Client.Responses;

using Bitcoin.SilentPayments.Models;

public sealed record SilentPaymentClientResponse(SilentPaymentAddressResult? Address = null,
    IReadOnlyList<SilentPaymentLabelInfo>? Labels = null, SilentPaymentStatus? Status = null);