using NBitcoin;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Outputs;
using Models;
using Splicing;

/// <summary>
/// The pure half of following a backed-up channel through its splices (splicing plan lane SP2-E, NL-478): whether a
/// transaction spending a funding output is a commitment (the end of the chain) and, when it is not, which of its
/// outputs is the channel's next funding output.
/// </summary>
/// <remarks>
/// <para>A splice spends the channel's funding output with the 2-of-2 witness a commitment also uses, so the two are
/// told apart by BOLT 3's commitment shape: one input, <c>nLockTime</c> with 0x20 in its upper byte and the input's
/// <c>nSequence</c> with 0x80 in its upper byte. A splice (or a mutual close) has neither.</para>
/// <para>The next funding output is recognized, in order, from a pending splice the backup named (its outpoint and
/// both keys), else by trying our funding keys at the current key index and the next
/// <see cref="MaxKeyRotationLookahead"/> indexes (a splice rotates ours deterministically, D5) against the peer's
/// current funding key (and the pending splices' keys): the P2WSH 2-of-2 whose script matches is the new funding. A
/// peer that rotated its own key too is recognized later, from the witness of the output's spend
/// (<see cref="TryParseFundingWitness"/>, which needs only our key). A spend matching none of them (a mutual close,
/// an unknown shape) ends the chain there.</para>
/// <para>A simple taproot channel (NL-1059) has a MuSig2 P2TR funding output spent by key path, so its candidates are
/// the P2TR outputs, a match is the BIP 86 output of <c>KeyAgg(our key, the peer's key)</c>
/// (<see cref="SpliceFundingScripts.CreateScriptPubKey"/>), and no witness ever names a key: a splice whose peer key
/// the backup does not name is recognized only from the far end of the chain, a commitment of the peer that pays our
/// taproot <c>to_remote</c> or our anchor (both keyed to our payment basepoint, <see cref="PaysUsOnTaprootCommitment"/>),
/// or from the peer (its <c>peer_storage_retrieval</c> naming the channel's funding outpoint).</para>
/// </remarks>
public static class SpliceSpendFollower
{
    /// <summary>How many funding key indexes past the current one are tried for a splice's output.</summary>
    public const uint MaxKeyRotationLookahead = 8;

    /// <summary>How many splices in a row a restore follows before it gives up (a guard against a loop).</summary>
    public const int MaxSplices = 64;

    /// <summary>Whether <paramref name="transaction"/> has the shape of a BOLT 3 commitment transaction.</summary>
    public static bool IsCommitment(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Inputs.Count == 1
            && transaction.LockTime.Value >> 24 == 0x20
            && transaction.Inputs[0].Sequence.Value >> 24 == 0x80;
    }

    /// <summary>
    /// The outputs of <paramref name="transaction"/> that may be a channel's funding output: its P2WSH outputs, or its
    /// P2TR outputs for a simple taproot channel, in order.
    /// </summary>
    public static IReadOnlyList<ushort> GetCandidateOutputs(Transaction transaction, bool simpleTaproot = false)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var outputs = new List<ushort>();
        for (var vout = 0; vout < transaction.Outputs.Count && vout <= ushort.MaxValue; vout++)
            if (IsCandidate(transaction.Outputs[vout], simpleTaproot))
                outputs.Add((ushort)vout);

        return outputs;
    }

    /// <summary>Whether <paramref name="output"/> has the shape of a funding output (P2WSH, or P2TR for taproot).</summary>
    public static bool IsCandidate(TxOut output, bool simpleTaproot)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.ScriptPubKey.IsScriptType(simpleTaproot ? ScriptType.Taproot : ScriptType.P2WSH);
    }

    /// <summary>
    /// Whether <paramref name="transaction"/> is a commitment (BOLT 3 shape) that pays a simple taproot channel's
    /// <c>to_remote</c> or <c>to_remote_anchor</c> to us: both are keyed to our <paramref name="paymentBasepoint"/>
    /// (bolt-simple-taproot.md: the NUMS internal key with the <c>&lt;payment_basepoint&gt; OP_CHECKSIGVERIFY 1
    /// OP_CSV</c> leaf, and the 330-sat anchor with that internal key), which no other channel shares, so the peer's
    /// commitment proves the funding output it spends is this channel's (NL-1059).
    /// </summary>
    public static bool PaysUsOnTaprootCommitment(Transaction transaction, CompactPubKey paymentBasepoint)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!IsCommitment(transaction))
            return false;

        byte[] toRemote = new TaprootToRemoteOutput(Domain.Money.LightningMoney.Zero, new PubKey(paymentBasepoint))
                         .BitcoinScriptPubKey;
        return transaction.Outputs.Any(o => o.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(toRemote))
            || new AnchorChildTransactionBuilder().FindTaprootAnchorOutput(transaction.ToBytes(), paymentBasepoint)
                   is not null;
    }

    /// <summary>
    /// For the serialized transaction <paramref name="rawTransaction"/> that spent a channel's funding output: null when
    /// it can't be read or has the shape of a commitment (a close to resolve), else the outputs that may be the
    /// channel's next funding output (<see cref="GetCandidateOutputs"/>; none: no splice, a close of another shape).
    /// </summary>
    public static IReadOnlyList<ushort>? GetSpliceCandidateOutputs(byte[] rawTransaction, bool simpleTaproot = false)
    {
        ArgumentNullException.ThrowIfNull(rawTransaction);
        Transaction transaction;
        try
        {
            transaction = Transaction.Load(rawTransaction, Network.Main);
        }
        catch (Exception)
        {
            return null;
        }

        return IsCommitment(transaction) ? null : GetCandidateOutputs(transaction, simpleTaproot);
    }

    /// <summary>
    /// The local funding key indexes tried for a splice of <paramref name="current"/>: the current one, the next
    /// <see cref="MaxKeyRotationLookahead"/>, and those of the pending splices.
    /// </summary>
    public static IReadOnlyList<uint> CandidateKeyIndexes(ChannelBackupEntry current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var indexes = new List<uint>();
        for (var i = 0u; i <= MaxKeyRotationLookahead && current.LocalFundingKeyIndex <= uint.MaxValue - i; i++)
            indexes.Add(current.LocalFundingKeyIndex + i);

        foreach (var pending in current.PendingFundings)
            if (!indexes.Contains(pending.LocalFundingKeyIndex))
                indexes.Add(pending.LocalFundingKeyIndex);

        return indexes;
    }

    /// <summary>
    /// The entry moved to the funding output of <paramref name="spend"/> (a splice of <paramref name="current"/>'s
    /// funding output mined at <paramref name="height"/>, index <paramref name="transactionIndex"/> of its block), or
    /// null when no output of it is recognized as the channel's next funding output.
    /// </summary>
    /// <param name="current">The channel at the funding the transaction spends.</param>
    /// <param name="spend">The spending transaction (not a commitment).</param>
    /// <param name="height">Its block height.</param>
    /// <param name="transactionIndex">Its index in the block.</param>
    /// <param name="deriveLocalFundingKey">Our funding key of this channel at a funding key index (null when it can't
    /// be derived).</param>
    /// <param name="musig2">The MuSig2 service a simple taproot channel's outputs are matched with (none: no taproot
    /// output matches).</param>
    public static ChannelBackupEntry? TryIdentifyNextFunding(ChannelBackupEntry current, Transaction spend,
                                                             uint height, uint transactionIndex,
                                                             Func<uint, CompactPubKey?> deriveLocalFundingKey,
                                                             IMusig2Service? musig2 = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(spend);
        ArgumentNullException.ThrowIfNull(deriveLocalFundingKey);

        var txId = new TxId(spend.GetHash().ToBytes());

        // A splice the backup named: its outpoint and keys are known (checked against the output)
        foreach (var pending in current.PendingFundings.Where(p => p.FundingTxId == txId))
        {
            if (pending.FundingOutputIndex < spend.Outputs.Count
             && Matches(spend.Outputs[pending.FundingOutputIndex], pending.LocalFundingPubKey,
                        pending.RemoteFundingPubKey, current.OptionSimpleTaproot, musig2))
                return MoveTo(current, txId, pending.FundingOutputIndex,
                              (ulong)spend.Outputs[pending.FundingOutputIndex].Value.Satoshi,
                              pending.LocalFundingKeyIndex, pending.LocalFundingPubKey, pending.RemoteFundingPubKey,
                              height, transactionIndex);
        }

        // Otherwise our deterministic next keys against the peer's known keys
        var remoteKeys = current.PendingFundings.Select(p => p.RemoteFundingPubKey)
                                .Prepend(current.RemoteFundingPubKey)
                                .Distinct()
                                .ToList();
        var localKeys = CandidateKeyIndexes(current)
                       .Select(index => (Index: index, Key: deriveLocalFundingKey(index)))
                       .Where(k => k.Key is not null)
                       .Select(k => (k.Index, Key: k.Key!.Value))
                       .ToList();
        for (var vout = 0; vout < spend.Outputs.Count && vout <= ushort.MaxValue; vout++)
        {
            var output = spend.Outputs[vout];
            if (!IsCandidate(output, current.OptionSimpleTaproot))
                continue;

            foreach (var (index, localKey) in localKeys)
                foreach (var remoteKey in remoteKeys)
                    if (Matches(output, localKey, remoteKey, current.OptionSimpleTaproot, musig2))
                        return MoveTo(current, txId, (ushort)vout, (ulong)output.Value.Satoshi, index, localKey,
                                      remoteKey, height, transactionIndex);
        }

        return null;
    }

    /// <summary>
    /// Reads the witness of an input spending a P2WSH 2-of-2 funding output (BOLT 3: <c>0 &lt;sig1&gt; &lt;sig2&gt;
    /// 2 &lt;key1&gt; &lt;key2&gt; 2 OP_CHECKMULTISIG</c>) and, when one of its keys is one of ours
    /// (<paramref name="deriveLocalFundingKey"/> over the candidate indexes of <paramref name="current"/>), returns our
    /// key index and key and the peer's key. Null for any other witness.
    /// </summary>
    public static (uint Index, CompactPubKey Local, CompactPubKey Remote)? TryParseFundingWitness(
        ChannelBackupEntry current, WitScript witness, Func<uint, CompactPubKey?> deriveLocalFundingKey)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(witness);
        ArgumentNullException.ThrowIfNull(deriveLocalFundingKey);
        if (witness.PushCount != 4)
            return null;

        var parameters = PayToMultiSigTemplate.Instance.ExtractScriptPubKeyParameters(new Script(witness[3]));
        if (parameters is not { SignatureCount: 2, PubKeys.Length: 2 })
            return null;

        var keys = parameters.PubKeys.Select(k => new CompactPubKey(k.Compress().ToBytes())).ToArray();
        foreach (var index in CandidateKeyIndexes(current))
        {
            if (deriveLocalFundingKey(index) is not { } ours)
                continue;

            if (keys[0] == ours && keys[1] != ours)
                return (index, ours, keys[1]);
            if (keys[1] == ours && keys[0] != ours)
                return (index, ours, keys[0]);
        }

        return null;
    }

    /// <summary>
    /// <paramref name="current"/> moved to the funding output <paramref name="txId"/>:<paramref name="outputIndex"/>
    /// mined at <paramref name="height"/>: its outpoint, capacity, both keys, our key index, the funding height and the
    /// short channel id of that output, and no pending splice (a lock discards the others).
    /// </summary>
    public static ChannelBackupEntry MoveTo(ChannelBackupEntry current, TxId txId, ushort outputIndex,
                                            ulong capacitySat, uint localFundingKeyIndex,
                                            CompactPubKey localFundingPubKey, CompactPubKey remoteFundingPubKey,
                                            uint height, uint transactionIndex)
    {
        ArgumentNullException.ThrowIfNull(current);

        // No conditional with a bare null: it would convert through ShortChannelId's implicit byte[] operator
        ShortChannelId? shortChannelId = null;
        if (height is > 0 and <= 0xFFFFFF && transactionIndex <= 0xFFFFFF)
            shortChannelId = new ShortChannelId(height, transactionIndex, outputIndex);

        return current with
        {
            FundingTxId = txId,
            FundingOutputIndex = outputIndex,
            CapacitySat = capacitySat,
            LocalFundingKeyIndex = localFundingKeyIndex,
            LocalFundingPubKey = localFundingPubKey,
            RemoteFundingPubKey = remoteFundingPubKey,
            FundingHeight = height,
            ShortChannelId = shortChannelId,
            PendingFundings = []
        };
    }

    /// <summary>
    /// Whether <paramref name="output"/> is the funding output of the two funding keys: the P2WSH 2-of-2, or for a
    /// simple taproot channel the MuSig2 P2TR output (false without <paramref name="musig2"/>).
    /// </summary>
    public static bool Matches(TxOut output, CompactPubKey localFundingPubKey, CompactPubKey remoteFundingPubKey,
                               bool simpleTaproot = false, IMusig2Service? musig2 = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (localFundingPubKey == remoteFundingPubKey || (simpleTaproot && musig2 is null))
            return false;

        try
        {
            var scriptPubKey = SpliceFundingScripts.CreateScriptPubKey(localFundingPubKey, remoteFundingPubKey,
                                                                       simpleTaproot, musig2);
            return output.ScriptPubKey.ToBytes().AsSpan().SequenceEqual((byte[])scriptPubKey);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or Domain.Exceptions.MusigException)
        {
            // A key that is no valid point matches nothing
            return false;
        }
    }
}