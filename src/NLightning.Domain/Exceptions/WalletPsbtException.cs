namespace NLightning.Domain.Exceptions;

/// <summary>Why a wallet PSBT or lease request was refused (NL-1184).</summary>
public enum WalletPsbtError
{
    /// <summary>The request itself is wrong.</summary>
    InvalidArgument,

    /// <summary>The wallet's state refuses it (not enough funds, an output leased under another id, ...).</summary>
    FailedPrecondition,

    /// <summary>The output or lease does not exist.</summary>
    NotFound,

    /// <summary>bitcoind refused to publish the transaction (LND answers this with a plain RPC error).</summary>
    PublishRefused
}

/// <summary>A refused wallet PSBT or lease request (NL-1184); nothing was changed.</summary>
public sealed class WalletPsbtException(WalletPsbtError error, string message) : Exception(message)
{
    /// <summary>Why it was refused.</summary>
    public WalletPsbtError Error { get; } = error;
}