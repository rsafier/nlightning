namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

public sealed record SilentPaymentAddressResult(string Address, uint? Label, string? LabelName,
                                                bool RecoverableElsewhere);