using System.Text.Json.Nodes;
using NBitcoin;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Transactions.Constants;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Exceptions;
using CompactSignature = NLightning.Domain.Crypto.ValueObjects.CompactSignature;
using LightningMoney = NLightning.Domain.Money.LightningMoney;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>
/// Zero-fee-HTLC anchors channels (VLS <c>AnchorsZeroFeeHtlc</c>): the CPFP child of our commitment. The anchor input is
/// signed by VLS's <c>sign_holder_anchor_input</c> (funding key over the keyed anchor script, 330 sat) and the wallet
/// fee inputs by VLS's onchain policy (<c>check_onchain_tx</c>: every output ours, fee range and fee velocity), which
/// leaves the foreign inputs (the anchor, an HTLC output) unsigned. Only P2WPKH wallet inputs held by a fee reservation
/// are signed; nothing falls back to local keys.
/// </summary>
public sealed partial class VlsLightningSigner
{
    // Gateway operations of the anchors lane (kept apart from VlsOperations, which other lanes extend)
    internal const uint SignHolderAnchorOperation = 2060, WalletSignFeeInputsOperation = 2061;

    public CompactSignature SignAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction, int inputIndex,
                                            LightningMoney amount)
    {
        ArgumentNullException.ThrowIfNull(unsignedTransaction);
        ArgumentNullException.ThrowIfNull(amount);
        var info = Info(channelId);
        if (info.IsSimpleTaproot) throw Unsupported();
        if (amount != TransactionConstants.AnchorOutputAmount)
            throw new SignerException($"An anchor is worth 330 sat, not {amount.Satoshi} sat", channelId, "Internal error");
        var tx = Load(unsignedTransaction);
        if (inputIndex < 0 || inputIndex >= tx.Inputs.Count)
            throw new SignerException($"The anchor child transaction has no input {inputIndex}", channelId, "Internal error");

        var command = Command("sign_holder_anchor", Channel(channelId));
        command["transaction"] = tx.ToHex();
        command["input"] = inputIndex;
        var signature = Signature(connection.Invoke(SignHolderAnchorOperation, command).GetProperty("signature"));

        // The signer is not trusted to have signed what was asked: check it against our keyed anchor script
        var anchorScript = AnchorRedeemScript(new PubKey(info.LocalFundingPubKey));
        var hash = tx.GetSignatureHash(anchorScript, inputIndex, SigHash.All,
                                       new TxOut(Money.Satoshis(amount.Satoshi), anchorScript.WitHash.ScriptPubKey),
                                       HashVersion.WitnessV0);
        var parsed = ParseSignature(signature);
        if (!parsed.IsLowS || !new PubKey(info.LocalFundingPubKey).Verify(hash, parsed))
            throw new SignerException("VLS anchor signature does not verify against our anchor", channelId, "Internal error");
        return signature;
    }

    /// <summary>Reserved fee inputs only (the reclaim of an abandoned anchor child's inputs).</summary>
    public bool SignWalletTransaction(SignedTransaction unsignedTransaction) =>
        SignFeeInputs(unsignedTransaction, null, []);

    public bool SignWalletTransaction(SignedTransaction unsignedTransaction,
                                      IReadOnlyList<SpentOutput> otherSpentOutputs) =>
        SignFeeInputs(unsignedTransaction, null, otherSpentOutputs);

    public bool SignWalletTransaction(SignedTransaction unsignedTransaction, Guid reservationId,
                                      IReadOnlyList<SpentOutput> otherSpentOutputs) =>
        SignFeeInputs(unsignedTransaction, reservationId, otherSpentOutputs);

    private bool SignFeeInputs(SignedTransaction unsignedTransaction, Guid? expectedReservationId,
                               IReadOnlyList<SpentOutput> otherSpentOutputs)
    {
        ArgumentNullException.ThrowIfNull(unsignedTransaction);
        ArgumentNullException.ThrowIfNull(otherSpentOutputs);
        if (wallet is null) throw new SignerException("VLS wallet context unavailable.");
        var tx = Load(unsignedTransaction);
        var paths = new JsonArray();
        var prev = new JsonArray();
        var outputs = new JsonArray();
        var walletInputs = new bool[tx.Inputs.Count];
        var keys = new VlsSecureKeyManager(connection);
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var prevOut = tx.Inputs[i].PrevOut;
            var txId = new TxId(prevOut.Hash.ToBytes());
            if (wallet.TryGetUtxo(txId, prevOut.N, out var utxo))
            {
                if (utxo.LockedToChannelId is { } locked)
                    throw new SignerException($"Wallet input {i} ({prevOut}) is locked to the funding of channel {locked}");
                if (!wallet.TryGetFeeReservation(txId, prevOut.N, out var reservation))
                    throw new SignerException($"Wallet input {i} ({prevOut}) is not reserved for a fee spend");
                if (expectedReservationId is { } expected && reservation != expected)
                    throw new SignerException($"Wallet input {i} ({prevOut}) belongs to fee reservation {reservation}, not {expected}");
                if (utxo.AddressType != AddressType.P2Wpkh || utxo.WalletAddress is null || utxo.WalletAddress.AccountIndex != 0)
                    throw new SignerException($"Wallet input {i} ({prevOut}) is not a VLS P2WPKH wallet output");
                var index = utxo.WalletAddress.DerivationIndex ?? utxo.AddressIndex;
                var script = new PubKey(keys.GetWalletPublicKey(index, utxo.IsAddressChange, utxo.AddressType)).WitHash.ScriptPubKey;
                if (BitcoinAddress.Create(utxo.WalletAddress.Address, Network.RegTest).ScriptPubKey != script)
                    throw new SignerException("Wallet address does not match VLS derivation.");
                paths.Add("m/" + VlsSecureKeyManager.WalletIndex(index, utxo.IsAddressChange));
                prev.Add(new JsonObject { ["value"] = utxo.Amount.Satoshi, ["script_pubkey"] = script.ToHex() });
                walletInputs[i] = true;
                continue;
            }

            // A foreign input (our anchor, an HTLC output) stays unsigned; VLS needs its value for the fee check
            var spent = otherSpentOutputs.FirstOrDefault(o => o.TxId == txId && o.Index == prevOut.N)
                     ?? throw new SignerException($"Input {i} ({prevOut}) is neither a wallet output nor a given spent output");
            paths.Add("m");
            prev.Add(new JsonObject
            {
                ["value"] = spent.Amount.Satoshi,
                ["script_pubkey"] = Convert.ToHexString((byte[])spent.ScriptPubKey).ToLowerInvariant()
            });
        }

        if (!walletInputs.Any(w => w)) return false;
        foreach (var output in tx.Outputs) outputs.Add(FindWalletPath(output.ScriptPubKey) ?? "m");
        // An anchors HTLC transaction's fee inputs: the gateway validates its HTLC pair (lane vls-onchain)
        var htlcCommand = AnchorHtlcFeeInputsCommand(tx);
        var command = htlcCommand ?? new JsonObject { ["op"] = "wallet_sign_fee_inputs" };
        command["transaction"] = tx.ToHex();
        command["input_paths"] = paths;
        command["prev_outputs"] = prev;
        command["output_paths"] = outputs;
        var witnesses = connection.Invoke(htlcCommand is null ? WalletSignFeeInputsOperation
                                                              : VlsOnchainOperations.SignHolderHtlcFeeInputs, command)
                                  .GetProperty("witnesses").EnumerateArray().ToArray();
        if (witnesses.Length != tx.Inputs.Count) throw new SignerException("VLS wallet witness count mismatch.");
        for (var i = 0; i < witnesses.Length; i++)
        {
            var stack = witnesses[i].EnumerateArray().Select(w => Convert.FromHexString(w.GetString()!)).ToArray();
            if (walletInputs[i] != (stack.Length > 0))
                throw new SignerException($"VLS signed the wrong inputs of {unsignedTransaction.TxId}");
            if (walletInputs[i]) tx.Inputs[i].WitScript = new WitScript(stack);
        }

        unsignedTransaction.RawTxBytes = tx.ToBytes();
        return true;
    }

    /// <summary>BOLT 3 <c>to_local_anchor</c>/<c>to_remote_anchor</c> script keyed to a funding pubkey.</summary>
    private static Script AnchorRedeemScript(PubKey fundingPubKey) => new(
        Op.GetPushOp(fundingPubKey.ToBytes()), OpcodeType.OP_CHECKSIG, OpcodeType.OP_IFDUP, OpcodeType.OP_NOTIF,
        OpcodeType.OP_16, OpcodeType.OP_CHECKSEQUENCEVERIFY, OpcodeType.OP_ENDIF);

    private static Transaction Load(SignedTransaction transaction)
    {
        try { return Transaction.Load(transaction.RawTxBytes, Network.RegTest); }
        catch (Exception e) { throw new SignerException($"The transaction {transaction.TxId} does not parse", e); }
    }
}