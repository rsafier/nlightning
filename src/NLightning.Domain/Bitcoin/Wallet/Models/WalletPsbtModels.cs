namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Enums;
using Money;
using ValueObjects;

/// <summary>A wallet output that is free to spend (LND's walletrpc <c>Utxo</c>, NL-1184).</summary>
/// <param name="TxId">The transaction of the output.</param>
/// <param name="Index">The output index.</param>
/// <param name="Amount">Its value.</param>
/// <param name="AddressType">Its wallet address type.</param>
/// <param name="Address">Its wallet address.</param>
/// <param name="ScriptPubKey">Its script.</param>
/// <param name="Confirmations">How deep it is (0: unconfirmed).</param>
public sealed record WalletUnspentOutput(TxId TxId, uint Index, LightningMoney Amount, AddressType AddressType,
                                         string Address, BitcoinScript ScriptPubKey, uint Confirmations);

/// <summary>A wallet output leased to an external spender (LND's <c>UtxoLease</c>, NL-1184).</summary>
/// <param name="LockId">The 32-byte lease id the spender chose (LND's <c>id</c>).</param>
/// <param name="TxId">The transaction of the leased output.</param>
/// <param name="Index">The output index.</param>
/// <param name="Expiration">When the lease ends and the output is free again.</param>
/// <param name="Amount">Its value.</param>
/// <param name="ScriptPubKey">Its script.</param>
public sealed record WalletLease(byte[] LockId, TxId TxId, uint Index, DateTimeOffset Expiration,
                                 LightningMoney Amount, BitcoinScript ScriptPubKey);

/// <summary>
/// What <c>FundPsbt</c> funds (NL-1184): the outputs to pay, optionally the wallet inputs to use (no coin selection
/// then), the fee rate and the lease of the inputs.
/// </summary>
/// <param name="Outputs">The outputs to pay, in order (script and value).</param>
/// <param name="Inputs">The wallet inputs to spend, or empty to let the wallet select them.</param>
/// <param name="FeeRatePerKw">The fee rate in sat per kiloweight.</param>
/// <param name="MinConfirmations">The fewest confirmations an input may have.</param>
/// <param name="LockId">The lease id of the inputs.</param>
/// <param name="LockDuration">How long the inputs are leased.</param>
/// <param name="LockTime">The transaction's locktime.</param>
/// <param name="Version">The transaction's version.</param>
public sealed record PsbtFundRequest(IReadOnlyList<(BitcoinScript Script, LightningMoney Amount)> Outputs,
                                     IReadOnlyList<(TxId TxId, uint Index)> Inputs, long FeeRatePerKw,
                                     int MinConfirmations, byte[] LockId, TimeSpan LockDuration, uint LockTime = 0,
                                     int Version = 2);

/// <summary>A funded PSBT (NL-1184).</summary>
/// <param name="Psbt">The serialized PSBT (BIP 174), every input with its witness UTXO.</param>
/// <param name="ChangeOutputIndex">The change output, or -1 without change.</param>
/// <param name="Leases">The leases of its inputs.</param>
/// <param name="Fee">The fee the transaction pays.</param>
public sealed record PsbtFundResult(byte[] Psbt, int ChangeOutputIndex, IReadOnlyList<WalletLease> Leases,
                                    LightningMoney Fee);

/// <summary>A finalized PSBT (NL-1184).</summary>
/// <param name="SignedPsbt">The PSBT with every input's final witness.</param>
/// <param name="RawFinalTx">The signed transaction.</param>
public sealed record PsbtFinalizeResult(byte[] SignedPsbt, byte[] RawFinalTx);