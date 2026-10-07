namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Money;

/// <summary>A wallet destination and amount; null sends all and requires a single recipient.</summary>
public sealed record WalletRecipient(string Address, LightningMoney? Amount);