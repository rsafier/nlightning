namespace NLightning.Domain.Bitcoin.Enums;

/// <summary>
/// Why an on-chain wallet spend (<c>withdraw</c>) was refused before anything was reserved or broadcast. Insufficient
/// funds and the anchors reserve are reported with <see cref="Exceptions.InsufficientFundsException"/> and
/// <see cref="Exceptions.AnchorReserveException"/> instead.
/// </summary>
public enum WalletSpendError
{
    /// <summary>The destination is not a Bitcoin address.</summary>
    InvalidAddress,

    /// <summary>The destination is an address of another network.</summary>
    WrongNetwork,

    /// <summary>The amount is zero or below the destination's dust limit.</summary>
    DustAmount,

    /// <summary>The requested fee rate is below the relay minimum (253 sat/kw) or bitcoind's mempool minimum.</summary>
    FeeRateTooLow,

    /// <summary>The requested fee rate is above the sanity cap.</summary>
    FeeRateTooHigh,

    /// <summary>The chain monitor's processing is halted (NL-216): the wallet's view of the chain is stale.</summary>
    ChainProcessingHalted,

    /// <summary>
    /// The signed transaction's fee is above <see cref="Wallet.Models.WalletWithdrawRequest.MaxFee"/> (NL-997: a Cashu
    /// melt's fee reserve); nothing was stored or broadcast and the inputs were released.
    /// </summary>
    FeeAboveLimit
}