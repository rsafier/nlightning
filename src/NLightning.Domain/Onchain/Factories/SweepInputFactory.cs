namespace NLightning.Domain.Onchain.Factories;

using Bitcoin.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using Enums;
using Models;
using Taproot;

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
    public static SweepInput SecondLevelOutput(TxId htlcTxId, ulong amountSat, byte[] witnessScript, ushort toSelfDelay,
                                               CompactPubKey ourPerCommitmentPoint)
    {
        ArgumentNullException.ThrowIfNull(witnessScript);
        return new SweepInput(htlcTxId, 0, amountSat, SweepSpendKind.DelayedOutput, witnessScript, toSelfDelay,
                              PerCommitmentPoint: ourPerCommitmentPoint);
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
        RequireTaprootScriptPath(output);
        var htlc = output.Htlc ?? throw new ArgumentException("An HTLC descriptor has its HTLC", nameof(output));

        // Simple taproot: the accepted HTLC's timeout leaf <remote_htlcpubkey> OP_CHECKSIGVERIFY 1 OP_CSV OP_VERIFY
        // <cltv_expiry> OP_CLTV, spent with <sig> <leaf> <control_block>, nSequence 1 (NL-966)
        return new SweepInput(commitmentTxId, output.Vout, output.AmountSat, SweepSpendKind.HtlcTimeoutClaim,
                              RequireScript(output), output.CsvDelay, htlc.CltvExpiry, remotePerCommitmentPoint,
                              TaprootControlBlock: output.TaprootControlBlock,
                              SpentScriptPubKey: output.IsSimpleTaproot ? output.ScriptPubKey : null);
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
        RequireTaprootScriptPath(output);
        if (preimage is not { Length: CryptoConstants.Sha256HashLen })
            throw new ArgumentException("A preimage claim needs the 32-byte preimage", nameof(preimage));

        // Simple taproot: the offered HTLC's success leaf (preimage check, <remote_htlcpubkey> OP_CHECKSIGVERIFY 1
        // OP_CSV), spent with <sig> <preimage> <leaf> <control_block>, nSequence 1 (NL-966)
        return new SweepInput(commitmentTxId, output.Vout, output.AmountSat, SweepSpendKind.HtlcPreimageClaim,
                              RequireScript(output), output.CsvDelay, output.Htlc?.CltvExpiry ?? 0,
                              remotePerCommitmentPoint, Preimage: preimage,
                              TaprootControlBlock: output.TaprootControlBlock,
                              SpentScriptPubKey: output.IsSimpleTaproot ? output.ScriptPubKey : null);
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
        else if (output is { Kind: OutputDescriptorKind.RevokedHtlc, IsSimpleTaproot: true })
            return TaprootKeyPathPenalty(output.Vout, output.AmountSat, commitmentTxId, SweepSpendKind.RevokedHtlc,
                                         output.ScriptPubKey, output.WitnessScript, output.TaprootControlBlock,
                                         perCommitmentSecret);

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
    /// Simple taproot (NL-966): the output of the peer's HTLC-timeout/success transaction that spent a revoked
    /// commitment's HTLC output, taken by <b>key path</b>: its internal key is the revocation key, tweaked with the merkle
    /// root of its single delay leaf (B5-REV-06).
    /// </summary>
    /// <param name="theirHtlcTxId">The peer's second-level transaction.</param>
    /// <param name="vout">Its output.</param>
    /// <param name="amountSat">The output amount.</param>
    /// <param name="scriptPubKey">The P2TR output script.</param>
    /// <param name="delayLeaf">The output's delay leaf <c>&lt;local_delayedpubkey&gt; OP_CHECKSIGVERIFY
    /// &lt;to_self_delay&gt; OP_CSV</c>.</param>
    /// <param name="controlBlock">The leaf's control block (33 bytes: no other leaf).</param>
    /// <param name="perCommitmentSecret">The peer's secret of the revoked commitment.</param>
    public static SweepInput TaprootSecondLevelPenalty(TxId theirHtlcTxId, uint vout, ulong amountSat,
                                                       byte[] scriptPubKey, byte[] delayLeaf, byte[] controlBlock,
                                                       Secret perCommitmentSecret) =>
        TaprootKeyPathPenalty(vout, amountSat, theirHtlcTxId, SweepSpendKind.RevokedDelayedOutput, scriptPubKey,
                              delayLeaf, controlBlock, perCommitmentSecret);

    /// <summary>
    /// A revocation key-path spend of a simple taproot output (internal key = revocation key): the leaf and control
    /// block recorded for the output only give the merkle root the key is tweaked with; the witness is the signature.
    /// </summary>
    private static SweepInput TaprootKeyPathPenalty(uint vout, ulong amountSat, TxId txId, SweepSpendKind kind,
                                                    byte[] scriptPubKey, byte[]? leaf, byte[]? controlBlock,
                                                    Secret perCommitmentSecret)
    {
        ArgumentNullException.ThrowIfNull(scriptPubKey);
        if (leaf is null || controlBlock is null)
            throw new ArgumentException($"The simple taproot output {vout} has no leaf and control block to derive its "
                                      + "merkle root from", nameof(leaf));

        return new SweepInput(txId, vout, amountSat, kind, null, PerCommitmentSecret: perCommitmentSecret,
                              SpentScriptPubKey: scriptPubKey,
                              TaprootMerkleRoot: TapscriptMerkleRoot.Compute(leaf, controlBlock));
    }

    /// <summary>
    /// A simple taproot output spent by script path needs its leaf and control block (NL-877 T4).
    /// </summary>
    private static void RequireTaprootScriptPath(CommitmentOutputDescriptor output)
    {
        if (output.IsSimpleTaproot && (output.TaprootControlBlock is null || output.WitnessScript is null))
            throw new ArgumentException($"The simple taproot {output.Kind} output {output.Vout} has no leaf and control "
                                      + "block to spend it with", nameof(output));
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