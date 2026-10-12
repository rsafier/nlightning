namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

public sealed record SilentPaymentLabelInfo(uint Label, string Name, uint CreatedAtHeight, string Address,
                                            bool IsChange = false);