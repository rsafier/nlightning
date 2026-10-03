using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Wallet.Interfaces;

/// <summary>
/// Reads the <c>prevtx</c> of a received <c>tx_add_input</c> (BOLT 2 "The tx_add_input Message", splicing plan IT2-T1,
/// IT-R-01; NL-041) and answers the <c>require_confirmed_inputs</c> check through bitcoind.
/// </summary>
/// <remarks>
/// <para><see cref="Inspect"/> is pure: the bytes must be exactly one transaction with at least one input (trailing bytes
/// are refused), <c>prevtx_vout</c> must be below its output count, and the spent scriptPubKey must be a witness
/// program as BOLT 2 words it: a 1-byte push of 0 to 16 followed by one data push of 2 to 40 bytes, and nothing else
/// (so P2WPKH, P2WSH, P2TR and future witness versions pass; P2PKH, P2SH, P2SH-wrapped segwit, bare multisig and
/// <c>OP_RETURN</c> do not). A non-witness script is reported as invalid too (with the txid, amount and script filled
/// in), so a caller that only reads <see cref="PrevTxInspection.IsValid"/> still fails the negotiation.</para>
/// <para><see cref="IsOutputConfirmedAsync"/> (the <c>require_confirmed_inputs</c> check) asks <c>gettxout</c> without
/// the mempool first: a confirmed unspent output is confirmed, which works on a node without a transaction index. Only
/// when that finds nothing does it fall back to <see cref="IsConfirmedAsync"/>, which asks for the transaction's
/// confirmations (<c>getrawtransaction</c> verbose): without <c>-txindex</c> bitcoind only knows mempool transactions
/// and those of its own wallet, so a confirmed foreign transaction reads as unconfirmed there. Every failure reads as
/// unconfirmed, which fails the negotiation (never the unsafe way).</para>
/// </remarks>
public sealed class PrevTxInspector : IPrevTxInspector
{
    private readonly IBitcoinChainService? _bitcoinChainService;
    private readonly ILogger<PrevTxInspector> _logger;

    public PrevTxInspector(ILogger<PrevTxInspector>? logger = null, IBitcoinChainService? bitcoinChainService = null)
    {
        _logger = logger ?? NullLogger<PrevTxInspector>.Instance;
        _bitcoinChainService = bitcoinChainService;
    }

    /// <inheritdoc />
    public PrevTxInspection Inspect(ReadOnlyMemory<byte> prevTx, uint prevTxVout)
    {
        if (!InteractiveTxTransactionReader.TryReadTransaction(prevTx.Span, out var transaction) || transaction is null)
            return new PrevTxInspection(false, null, 0, null, null, false, "prevtx is not a valid transaction");

        var txId = new TxId(transaction.GetHash().ToBytes());
        var outputCount = transaction.Outputs.Count;
        if (prevTxVout >= outputCount)
            return new PrevTxInspection(false, txId, outputCount, null, null, false,
                                        $"prevtx_vout {prevTxVout} is not below the {outputCount} output(s) of prevtx");

        var output = transaction.Outputs[(int)prevTxVout];
        var script = output.ScriptPubKey.ToBytes();
        var amount = LightningMoney.Satoshis(output.Value.Satoshi);
        if (!IsWitnessProgram(script))
            return new PrevTxInspection(false, txId, outputCount, amount, script, false,
                                        $"the scriptPubKey of output {prevTxVout} of prevtx is not a witness program");

        return new PrevTxInspection(true, txId, outputCount, amount, script, true, null);
    }

    /// <inheritdoc />
    public async Task<bool> IsConfirmedAsync(TxId txId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bitcoinChainService is null)
            return false;

        try
        {
            return await _bitcoinChainService.GetTransactionConfirmationsAsync(new uint256((byte[])txId)) > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unknown: treat as unconfirmed, which fails the negotiation instead of accepting an unconfirmed input
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(e, "Could not read the confirmations of {TxId}; treated as unconfirmed", txId);

            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsOutputConfirmedAsync(TxId txId, uint prevTxVout,
                                                   CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bitcoinChainService is null)
            return false;

        try
        {
            var unspent = await _bitcoinChainService.GetConfirmedUnspentOutputAsync(
                              new OutPoint(new uint256((byte[])txId), prevTxVout));
            if (unspent is { Height: > 0 })
                return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(e, "Could not read output {TxId}:{Vout}; asking for its transaction", txId,
                                   prevTxVout);
        }

        return await IsConfirmedAsync(txId, cancellationToken);
    }

    /// <summary>
    /// BOLT 2 (and BIP 141): exactly a 1-byte push opcode for 0 to 16 (<c>OP_0</c>, <c>OP_1</c>..<c>OP_16</c>) followed by
    /// a single direct data push of 2 to 40 bytes.
    /// </summary>
    internal static bool IsWitnessProgram(ReadOnlySpan<byte> script)
    {
        if (script.Length is < 4 or > 42)
            return false;

        var version = script[0];
        if (version != (byte)OpcodeType.OP_0 && version is < (byte)OpcodeType.OP_1 or > (byte)OpcodeType.OP_16)
            return false;

        var pushLength = script[1];
        return pushLength is >= 2 and <= 40 && pushLength == script.Length - 2;
    }
}