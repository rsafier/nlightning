using System.Text.Json.Nodes;
using NBitcoin;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Onchain.Enums;
using NLightning.Domain.Onchain.Models;
using CompactSignature = NLightning.Domain.Crypto.ValueObjects.CompactSignature;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>
/// BOLT 5 on-chain resolution through VLS's semantic signers (static_remotekey channels): our HTLC transactions
/// (<c>sign_holder_htlc_tx</c>), delayed sweeps (<c>sign_delayed_sweep</c>), claims on the peer's commitment
/// (<c>sign_counterparty_htlc_sweep</c>), penalties (<c>sign_justice_sweep</c>) and our <c>to_remote</c> (VLS's
/// unilateral-close key and on-chain signer). The gateway signs HTLC transactions and delayed sweeps only for the
/// commitment it signed for broadcast, and VLS's sweep policy only pays this node's VLS wallet; there is no local-key
/// fallback.
/// </summary>
public sealed partial class VlsLightningSigner
{
    public CompactSignature SignLocalHtlcTransaction(ChannelId channelId, HtlcSigningContext htlcTransaction)
    {
        ArgumentNullException.ThrowIfNull(htlcTransaction);
        var built = htlcTransaction.HtlcTransaction;
        if (htlcTransaction.HasAnchors || built.IsTaproot || built.FeeInputSpentOutputs is not null)
            throw Unsupported();
        var command = Command("sign_holder_htlc", Channel(channelId));
        command["transaction"] = Hex(built.Transaction.RawTxBytes);
        command["point"] = htlcTransaction.PerCommitmentPoint.ToString();
        command["redeemscript"] = Hex((byte[])built.SpentWitnessScript);
        command["amount_sat"] = built.SpentAmount.Satoshi;
        return Signature(connection.Invoke(VlsOnchainOperations.SignHolderHtlc, command).GetProperty("signature"));
    }

    public CompactSignature SignSweepInput(ChannelId channelId, SweepSigningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TaprootSpentOutputs is not null || context.TaprootMerkleRoot is not null)
            throw Unsupported();
        var tx = Transaction.Load(context.UnsignedTransaction, Network.RegTest);
        if (context.InputIndex < 0 || context.InputIndex >= tx.Inputs.Count)
            throw new SignerException("Sweep input index is out of range.");
        // VLS's sweep policy checks every output against one wallet path
        var paths = tx.Outputs.Select(o => FindWalletPath(o.ScriptPubKey)).Distinct().ToArray();
        if (paths is not [{ } walletPath])
            throw new SignerException("A VLS sweep must pay exactly one address of the VLS wallet.");

        uint operation;
        JsonObject command;
        switch (context.KeyKind)
        {
            case SweepKeyKind.DelayedPayment:
                operation = VlsOnchainOperations.SignDelayedSweep;
                command = Command("sign_delayed_sweep", Channel(channelId));
                command["point"] = RequirePoint(context).ToString();
                command["redeemscript"] = Hex(RequireScript(context));
                break;
            case SweepKeyKind.HtlcRemotePoint:
                operation = VlsOnchainOperations.SignCounterpartyHtlcSweep;
                command = Command("sign_counterparty_htlc_sweep", Channel(channelId));
                command["point"] = RequirePoint(context).ToString();
                command["redeemscript"] = Hex(RequireScript(context));
                break;
            case SweepKeyKind.Revocation:
                var secret = context.PerCommitmentSecret
                          ?? throw new SignerException("A penalty needs the peer's revealed per-commitment secret.");
                using (var key = new Key((byte[])secret))
                    if (context.PerCommitmentPoint is { } point && key.PubKey != new PubKey(point))
                        throw new SignerException("The revealed secret does not match the per-commitment point.");
                operation = VlsOnchainOperations.SignJusticeSweep;
                command = Command("sign_justice_sweep", Channel(channelId));
                command["secret"] = Hex((byte[])secret);
                command["redeemscript"] = Hex(RequireScript(context));
                break;
            case SweepKeyKind.Payment:
                // A P2WSH to_remote is the anchors one (1-block CSV): outside this adapter
                if (context.WitnessScript is not null)
                    throw Unsupported();
                operation = VlsOnchainOperations.SignToRemoteSweep;
                command = Command("sign_to_remote_sweep", Channel(channelId));
                break;
            default:
                throw Unsupported();
        }

        command["transaction"] = Hex(context.UnsignedTransaction);
        command["input"] = context.InputIndex;
        command["amount_sat"] = context.AmountSat;
        command["wallet_path"] = walletPath;
        return Signature(connection.Invoke(operation, command).GetProperty("signature"));
    }

    private static NLightning.Domain.Crypto.ValueObjects.CompactPubKey RequirePoint(SweepSigningContext context) =>
        context.PerCommitmentPoint ?? throw new SignerException("The sweep needs its per-commitment point.");

    private static byte[] RequireScript(SweepSigningContext context) =>
        context.WitnessScript ?? throw new SignerException("The sweep needs the spent output's witness script.");
}

/// <summary>Gateway operation codes of the on-chain resolution commands (request capture and recovery scope).</summary>
public static class VlsOnchainOperations
{
    public const uint SignHolderHtlc = 2100, SignDelayedSweep = 2101, SignCounterpartyHtlcSweep = 2102,
        SignJusticeSweep = 2103, SignToRemoteSweep = 2104;
}