namespace NLightning.Domain.Bitcoin.Wallet.Models;

using ValueObjects;

/// <summary>Coin and change policy fixed before transaction derivation and signing.</summary>
public sealed record WalletSelectionPolicy(bool SilentPaymentSend = false, bool PreferP2TrChange = false,
                                          bool AvoidMixing = true, bool ChangeToSilentPayment = false,
                                          long MinimumSilentChangeSat = 1_000)
{
    public static WalletSelectionPolicy Default { get; } = new();

    /// <summary>
    /// The exact wallet outputs to spend (NL-1296: the operator's explicit choice, e.g. <c>withdraw --utxo</c>), or
    /// null to let the selector choose. When set, every listed output is spent and no other; silent payment coins
    /// among them are spent too (the operator opted in, so <see cref="AvoidMixing"/> does not apply).
    /// </summary>
    public IReadOnlyList<(TxId TxId, uint Index)>? Inputs { get; init; }
}