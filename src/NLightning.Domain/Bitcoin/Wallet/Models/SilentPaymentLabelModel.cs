namespace NLightning.Domain.Bitcoin.Wallet.Models;

public sealed record SilentPaymentLabelModel(uint M, string Name, uint CreatedAtHeight);