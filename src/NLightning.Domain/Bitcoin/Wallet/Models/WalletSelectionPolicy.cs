namespace NLightning.Domain.Bitcoin.Wallet.Models;

/// <summary>Coin and change policy fixed before transaction derivation and signing.</summary>
public sealed record WalletSelectionPolicy(bool SilentPaymentSend = false, bool PreferP2TrChange = false,
                                          bool AvoidMixing = true)
{
    public static WalletSelectionPolicy Default { get; } = new();
}