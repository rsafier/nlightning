namespace NLightning.Domain.Onchain.Factories;

using Bitcoin.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// Turns output descriptors into <see cref="SweepInput"/>s (BOLT 5 plan §3.3 "Resolution" column). Each method checks
/// that the descriptor is of the kind it resolves, so a wrong resolution cannot be built by mistake.
/// </summary>
public static class SweepInputFactory
{
    /// <summary>Our <c>to_local</c> on our commitment, after <c>to_self_delay</c> (B5-LCL-01).</summary>
    /// <param name="output">A <see cref="OutputDescriptorKind.DelayedToLocal"/> descriptor.</param>
    /// <param name="commitmentTxId">The commitment on chain.</param>
    /// <param name="ourPerCommitmentPoint">Our point of that commitment (<see cref="CommitmentOutputMap.PerCommitmentPoint"/>).</param>
    public static SweepInput ToLocal(CommitmentOutputDescriptor output, TxId commitmentTxId,
                                     CompactPubKey ourPerCommitmentPoint)
    {
        RequireKind(output, OutputDescriptorKind.DelayedToLocal);
        RequireTaprootScriptPath(output);
        return new SweepInput(commitmentTxId, output.Vout, output.AmountSat, SweepSpendKind.DelayedOutput,
                              RequireScript(output), output.CsvDelay, PerCommitmentPoint: ourPerCommitmentPoint,
                              TaprootControlBlock: output.TaprootControlBlock, SpentScriptPubKey: output.ScriptPubKey);
    }

    /// <summary>
    /// The output of our confirmed HTLC-timeout/success transaction (always vout 0), after <c>to_self_delay</c>
    /// (B5-LCL-LO-03, B5-LCL-RO-01).
    /// </summary>
    /// <param name="htlcTxId">Our HTLC transaction on chain.</param>
    /// <param name="amountSat">Its output 0's value.</param>
    /// <param name="witnessScript">The P2WSH witness script, or for a simple taproot channel the delay leaf.</param>
    /// <param name="toSelfDelay">The CSV of the output (the peer's <c>to_self_delay</c>).</param>
    /// <param name="ourPerCommitmentPoint">Our point of the commitment the HTLC was on.</param>
    /// <param name="taprootControlBlock">Simple taproot (NL-966): the delay leaf's control block (internal key
    /// <c>revocation_pubkey</c>); null for a P2WSH output.</param>
    /// <param name="spentScriptPubKey">Simple taproot: the P2TR scriptPubKey of the output (the BIP 341 sighash
    /// commits to it).</param>
    public static SweepInput SecondLevelOutput(TxId htlcTxId, ulong amountSat, byte[] witnessScript, ushort toSelfDelay,
                                               CompactPubKey ourPerCommitmentPoint, byte[]? taprootControlBlock = null,
                                               byte[]? spentScriptPubKey = null)
    {
        ArgumentNullException.ThrowIfNull(witnessScript);
        if (taprootControlBlock is not null && spentScriptPubKey is null)
            throw new ArgumentException("A taproot script-path spend needs the spent P2TR scriptPubKey",
                                        nameof(spentScriptPubKey));

        return new SweepInput(htlcTxId, 0, amountSat, SweepSpendKind.DelayedOutput, witnessScript, toSelfDelay,
                              PerCommitmentPoint: ourPerCommitmentPoint, TaprootControlBlock: taprootControlBlock,
                              SpentScriptPubKey: spentScriptPubKey);
    }

    /// <summary>Our <c>to_remote</c> on a peer commitment, current, next or revoked (D5, B5-RMT-02, B5-REV-02).</summary>
    /// <param name="output">A <see cref="OutputDescriptorKind.PaymentToRemote"/> descriptor.</param>
    /// <param name="commitmentTxId">The commitment on chain.</param>
    /// <param name="ourPaymentBasepoint">Our <c>payment_basepoint</c> (the P2WPKH witness pushes it).</param>
    public static SweepInput ToRemote(CommitmentOutputDescriptor output, TxId commitmentTxId,
                                      CompactPubKey ourPaymentBasepoint)
    {
        RequireKind(output, OutputDescriptorKind.PaymentToRemote);
        RequireTaprootScriptPath(output);
        return new SweepInput(commitmentTxId, output.Vout, output.AmountSat, SweepSpendKind.PaymentToRemote,
                              output.WitnessScript, output.CsvDelay, WitnessPubKey: ourPaymentBasepoint,
                              TaprootControlBlock: output.TaprootControlBlock, SpentScriptPubKey: output.ScriptPubKey);
    }

    /// <summary>An HTLC we offered, on the peer's commitment, after <c>cltv_expiry</c> (B5-RMT-LO-02).</summary>
    /// <param name="output">A <see cref="OutputDescriptorKind.RemoteReceivedHtlc"/> descriptor.</param>
    /// <param name="commitmentTxId">The commitment on chain.</param>
    /// <param name="remotePerCommitmentPoint">The peer's point of that commitment.</param>
    public static SweepInput HtlcTimeoutClaim(CommitmentOutputDescriptor output, TxId commitmentTxId,
                                              CompactPubKey remotePerCommitmentPoint)
    {
        RequireKind(output, OutputDescriptorKind.RemoteReceivedHtlc);
        RequireNotTaproot(output);
        var htlc = output.Htlc ?? throw new ArgumentException("An HTLC descriptor has its HTLC", nameof(output));
        return new SweepInput(commitmentTxId, output.Vout, output.AmountSat, SweepSpendKind.HtlcTimeoutClaim,
                              RequireScript(output), output.CsvDelay, htlc.CltvExpiry, remotePerCommitmentPoint);
    }

    /// <summary>An HTLC the peer offered, on its commitment, with the preimage (B5-RMT-RO-01).</summary>
    /// <param name="output">A <see cref="OutputDescriptorKind.RemoteOfferedHtlc"/> descriptor.</param>
    /// <param name="commitmentTxId">The commitment on chain.</param>
    /// <param name="remotePerCommitmentPoint">The peer's point of that commitment.</param>
    /// <param name="preimage">The payment preimage (32 bytes; the caller applies the B5-LCL-RO-02 guard).</param>
    public static SweepInput HtlcPreimageClaim(CommitmentOutputDescriptor output, TxId commitmentTxId,
                                               CompactPubKey remotePerCommitmentPoint, byte[] preimage)
    {
        RequireKind(output, OutputDescriptorKind.RemoteOfferedHtlc);
        RequireNotTaproot(output);
        if (preimage is not { Length: CryptoConstants.Sha256HashLen })
            throw new ArgumentException("A preimage claim needs the 32-byte preimage", nameof(preimage));

        return new SweepInput(commitmentTxId, output.Vout, output.AmountSat, SweepSpendKind.HtlcPreimageClaim,
                              RequireScript(output), output.CsvDelay, output.Htlc?.CltvExpiry ?? 0,
                              remotePerCommitmentPoint, Preimage: preimage);
    }

    /// <summary>
    /// A penalty input on a revoked commitment: its <c>to_local</c> (<c>&lt;revsig&gt; 1</c>, B5-REV-03) or an HTLC
    /// output (<c>&lt;revsig&gt; &lt;revocationpubkey&gt;</c>, B5-REV-04/05).
    /// </summary>
    /// <param name="output">A <see cref="OutputDescriptorKind.RevokedToLocal"/> or
    /// <see cref="OutputDescriptorKind.RevokedHtlc"/> descriptor.</param>
    /// <param name="commitmentTxId">The revoked commitment on chain.</param>
    /// <param name="perCommitmentSecret">The peer's secret of that commitment (from our shachain).</param>
    /// <param name="revocationPubKey">The commitment's <c>revocationpubkey</c> (pushed by an HTLC penalty).</param>
    public static SweepInput Penalty(CommitmentOutputDescriptor output, TxId commitmentTxId, Secret perCommitmentSecret,
                                     CompactPubKey revocationPubKey)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Kind == OutputDescriptorKind.RevokedToLocal)
            RequireTaprootScriptPath(output);
        else
            RequireNotTaproot(output);

        return output.Kind switch
        {
            // Simple taproot: the revocation leaf <local_delayedpubkey> OP_DROP <revocation_pubkey> OP_CHECKSIG, spent
            // with <revocation_sig> <leaf> <control_block>
            OutputDescriptorKind.RevokedToLocal => new SweepInput(commitmentTxId, output.Vout, output.AmountSat,
                                                                  SweepSpendKind.RevokedDelayedOutput,
                                                                  RequireScript(output),
                                                                  PerCommitmentSecret: perCommitmentSecret,
                                                                  TaprootControlBlock: output.TaprootControlBlock,
                                                                  SpentScriptPubKey: output.ScriptPubKey),
            OutputDescriptorKind.RevokedHtlc => new SweepInput(commitmentTxId, output.Vout, output.AmountSat,
                                                               SweepSpendKind.RevokedHtlc, RequireScript(output),
                                                               PerCommitmentSecret: perCommitmentSecret,
                                                               WitnessPubKey: revocationPubKey),
            _ => throw new ArgumentException($"A {output.Kind} output is not penalized", nameof(output))
        };
    }

    /// <summary>
    /// The output of the peer's HTLC-timeout/success transaction that spent a revoked commitment's HTLC output
    /// (always vout 0): <c>&lt;revsig&gt; 1</c> (B5-REV-06).
    /// </summary>
    public static SweepInput SecondLevelPenalty(TxId theirHtlcTxId, ulong amountSat, byte[] witnessScript,
                                                Secret perCommitmentSecret)
    {
        ArgumentNullException.ThrowIfNull(witnessScript);
        return new SweepInput(theirHtlcTxId, 0, amountSat, SweepSpendKind.RevokedDelayedOutput, witnessScript,
                              PerCommitmentSecret: perCommitmentSecret);
    }

    /// <summary>
    /// A simple taproot output is spent here only by script path, with its leaf and control block (NL-877 T4): anything
    /// else (an HTLC output, a key-path penalty) is not built by this factory yet.
    /// </summary>
    private static void RequireTaprootScriptPath(CommitmentOutputDescriptor output)
    {
        if (output.IsSimpleTaproot && (output.TaprootControlBlock is null || output.WitnessScript is null))
            throw new ArgumentException($"The simple taproot {output.Kind} output {output.Vout} has no leaf and control "
                                      + "block to spend it with", nameof(output));
    }

    /// <summary>The spends of simple taproot HTLC outputs are not built yet (NL-966).</summary>
    private static void RequireNotTaproot(CommitmentOutputDescriptor output)
    {
        if (output.IsSimpleTaproot)
            throw new ArgumentException($"Spending the simple taproot {output.Kind} output {output.Vout} is not "
                                      + "supported yet (NL-966)", nameof(output));
    }

    private static void RequireKind(CommitmentOutputDescriptor output, OutputDescriptorKind kind)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Kind != kind)
            throw new ArgumentException($"Expected a {kind} output, got {output.Kind}", nameof(output));
    }

    private static byte[] RequireScript(CommitmentOutputDescriptor output) =>
        output.WitnessScript ?? throw new ArgumentException($"A {output.Kind} output has a witness script",
                                                            nameof(output));
}