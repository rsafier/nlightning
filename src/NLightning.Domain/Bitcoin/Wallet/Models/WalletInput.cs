namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Enums;
using Money;
using ValueObjects;

/// <summary>
/// One confirmed wallet output handed out as a fee input (BOLT 5 plan O7-T1): what a transaction builder needs to spend
/// it and to charge its weight. The wallet signs it with <c>ILightningSigner.SignWalletTransaction</c>; its key never
/// leaves the signer.
/// </summary>
/// <param name="TxId">The transaction that created the output.</param>
/// <param name="Index">The output index in that transaction.</param>
/// <param name="Amount">The output's value.</param>
/// <param name="AddressType">P2WPKH or P2TR (key path).</param>
/// <param name="ScriptPubKey">The output's scriptPubKey.</param>
/// <param name="InputWeight">The weight the input adds to a transaction once signed, witness included (P2WPKH 272,
/// P2TR key path 231).</param>
public sealed record WalletInput(
    TxId TxId,
    uint Index,
    LightningMoney Amount,
    AddressType AddressType,
    BitcoinScript ScriptPubKey,
    int InputWeight);