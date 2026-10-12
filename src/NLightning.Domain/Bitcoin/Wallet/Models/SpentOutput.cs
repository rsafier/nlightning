namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Money;
using ValueObjects;

/// <summary>
/// An output a transaction spends that is not the wallet's (an anchor, a commitment's HTLC output), given to
/// <c>ILightningSigner.SignWalletTransaction</c> so it can sign P2TR wallet inputs: a BIP 341 signature commits to every
/// spent output's amount and script.
/// </summary>
public sealed record SpentOutput(TxId TxId, uint Index, LightningMoney Amount, BitcoinScript ScriptPubKey);