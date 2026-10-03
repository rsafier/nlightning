namespace NLightning.Domain.Bitcoin.Transactions.Extensions;

using Enums;

/// <summary>
/// What a <see cref="CommitmentFormat"/> implies for the commitment and HTLC transactions.
/// </summary>
public static class CommitmentFormatExtensions
{
    /// <summary>
    /// The format of a channel without taproot: <see cref="CommitmentFormat.Anchors"/> with <c>option_anchors</c>, else
    /// <see cref="CommitmentFormat.StaticRemoteKey"/>.
    /// </summary>
    public static CommitmentFormat FromOptionAnchors(bool hasAnchors) =>
        hasAnchors ? CommitmentFormat.Anchors : CommitmentFormat.StaticRemoteKey;

    /// <summary>
    /// True when the commitment has the anchors semantics: two 330 sat anchor outputs, the CSV-1 delays of to_remote and
    /// the HTLC outputs, and zero-fee HTLC transactions (sequence 1, counterparty signature
    /// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c>). Simple taproot channels inherit all of it.
    /// </summary>
    public static bool HasAnchorOutputs(this CommitmentFormat format) => format switch
    {
        CommitmentFormat.StaticRemoteKey => false,
        CommitmentFormat.Anchors or CommitmentFormat.SimpleTaproot => true,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown commitment format")
    };

    /// <summary>True for <see cref="CommitmentFormat.SimpleTaproot"/>: P2TR outputs and tapscript spends.</summary>
    public static bool IsTaproot(this CommitmentFormat format) => format == CommitmentFormat.SimpleTaproot;
}