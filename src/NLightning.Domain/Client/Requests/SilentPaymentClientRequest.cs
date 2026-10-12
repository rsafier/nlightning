namespace NLightning.Domain.Client.Requests;

using Enums;

public sealed record SilentPaymentClientRequest(ClientCommand Command, string? Label = null,
    uint? FromHeight = null, uint? RecoveryLabels = null, bool Cancel = false);