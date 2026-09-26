namespace NLightning.Domain.Onchain.Classifiers;

using Enums;
using Models;

/// <summary>
/// Classifies the transaction that spent a channel's funding output (BOLT 5 plan §3.1/§3.2 step 3, task O2-T3). Pure: no
/// I/O, no crypto; it never throws for a malformed spender (it is <see cref="FundingSpendKind.Unknown"/>).
/// </summary>
/// <remarks>
/// <para>Order of the checks:</para>
/// <list type="number">
/// <item>The spender must spend the funding outpoint (else <see cref="FundingSpendKind.NotFundingSpend"/>).</item>
/// <item>A known closing txid is <see cref="FundingSpendKind.Mutual"/>.</item>
/// <item>A txid equal to a rebuilt candidate is that candidate (local, remote, remote-next), whatever its number.</item>
/// <item>A commitment has exactly one input, and its commitment number is decoded from <c>nLockTime</c> and the input's
/// <c>nSequence</c> (<see cref="Protocol.Models.CommitmentNumber.Decode"/>). Without the commitment form, a spend paying a
/// <c>shutdown</c> script is <see cref="FundingSpendKind.Mutual"/> (only closing transactions carry those scripts, and
/// the 2-of-2 output needs our signature), anything else is <see cref="FundingSpendKind.Unknown"/> (B5-GEN-06).</item>
/// <item>By number, with the txid not matching: the remote-next number is <see cref="FundingSpendKind.RemoteNextCommit"/>,
/// the remote number <see cref="FundingSpendKind.RemoteCommit"/> (our rebuild differs: map its outputs by script),
/// below it <see cref="FundingSpendKind.Revoked"/>, above both <see cref="FundingSpendKind.FutureRemote"/> (data
/// loss).</item>
/// </list>
/// <para>
/// A non-matching commitment is always taken as the peer's: our local commitments carry our funding signature, which
/// the signer only gives for the latest one (I4, S1), and the caller passes that one as
/// <see cref="FundingSpendContext.LocalCommit"/>, so it matches by txid. Local and remote numbers share the obscuring
/// factor, so a number alone never proves a local commitment.
/// </para>
/// </remarks>
public static class FundingSpendClassifier
{
    public static FundingSpendClassification Classify(ChainTx spender, FundingSpendContext context)
    {
        ArgumentNullException.ThrowIfNull(spender);
        ArgumentNullException.ThrowIfNull(context);

        var inputs = spender.Inputs ?? [];
        var fundingInput = -1;
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i] is { } input && input.PreviousVout == context.FundingOutputIndex
                                       && input.PreviousTxId == context.FundingTxId)
            {
                fundingInput = i;
                break;
            }
        }

        if (fundingInput < 0)
            return new FundingSpendClassification(FundingSpendKind.NotFundingSpend, null, false,
                                                  "The transaction does not spend the funding output");

        if (context.MutualCloseTxIds is { } closingTxIds && closingTxIds.Contains(spender.TxId))
            return new FundingSpendClassification(FundingSpendKind.Mutual, null, true,
                                                  "A closing transaction we signed");

        if (Matches(context.LocalCommit, spender))
            return Matched(FundingSpendKind.LocalCommit, context.LocalCommit!.Value, "Our local commitment");

        if (Matches(context.RemoteNextCommit, spender))
            return Matched(FundingSpendKind.RemoteNextCommit, context.RemoteNextCommit!.Value,
                           "The peer's commitment awaiting its revoke_and_ack");

        if (Matches(context.RemoteCommit, spender))
            return Matched(FundingSpendKind.RemoteCommit, context.RemoteCommit!.Value, "The peer's current commitment");

        if (inputs.Count != 1)
            return PaysShutdownScript(spender, context)
                       ? new FundingSpendClassification(FundingSpendKind.Mutual, null, false,
                                                        "A closing transaction paying a shutdown script")
                       : new FundingSpendClassification(FundingSpendKind.Unknown, null, false,
                                                        $"A funding spend with {inputs.Count} inputs is not a "
                                                      + "commitment or closing transaction");

        var number = context.CommitmentNumber.Decode(spender.LockTime, inputs[fundingInput].Sequence);
        if (number is null)
            return PaysShutdownScript(spender, context)
                       ? new FundingSpendClassification(FundingSpendKind.Mutual, null, false,
                                                        "A closing transaction paying a shutdown script")
                       : new FundingSpendClassification(FundingSpendKind.Unknown, null, false,
                                                        "Neither a commitment (locktime/sequence prefixes) nor a "
                                                      + "known closing transaction");

        var n = number.Value;
        if (context.RemoteNextCommit is { } next && n == next.Number)
            return Unmatched(FundingSpendKind.RemoteNextCommit, n,
                             "The number of the peer's commitment awaiting its revoke_and_ack, with another txid");

        if (context.RemoteCommit is { } remote)
        {
            if (n == remote.Number)
                return Unmatched(FundingSpendKind.RemoteCommit, n,
                                 "The number of the peer's current commitment, with another txid");

            if (n < remote.Number)
                return Unmatched(FundingSpendKind.Revoked, n,
                                 $"Peer commitment {n} was revoked (current is {remote.Number})");
        }

        return Unmatched(FundingSpendKind.FutureRemote, n,
                         $"Peer commitment {n} is newer than any we know: we lost data");
    }

    private static bool Matches(CommitmentCandidate? candidate, ChainTx spender) =>
        candidate is { } c && c.TxId == spender.TxId;

    private static FundingSpendClassification Matched(FundingSpendKind kind, CommitmentCandidate candidate,
                                                      string reason) =>
        new(kind, candidate.Number, true, reason);

    private static FundingSpendClassification Unmatched(FundingSpendKind kind, ulong number, string reason) =>
        new(kind, number, false, reason);

    private static bool PaysShutdownScript(ChainTx spender, FundingSpendContext context)
    {
        foreach (var output in spender.Outputs ?? [])
        {
            if (output?.ScriptPubKey is not { } script)
                continue;

            if ((context.LocalShutdownScript is { Length: > 0 } local && script.AsSpan().SequenceEqual(local))
             || (context.RemoteShutdownScript is { Length: > 0 } remote && script.AsSpan().SequenceEqual(remote)))
                return true;
        }

        return false;
    }
}