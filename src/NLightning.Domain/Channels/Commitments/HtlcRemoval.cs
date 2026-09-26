namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;

/// <summary>
/// How an HTLC was removed, with the data of the removing message.
/// </summary>
/// <param name="Kind">Fulfill, fail or fail-malformed.</param>
/// <param name="PaymentPreimage">The preimage (fulfill only).</param>
/// <param name="Reason">The opaque failure onion (fail only).</param>
/// <param name="FailureCode">The BOLT 4 failure code (fail-malformed only).</param>
/// <param name="Sha256OfOnion">The onion hash (fail-malformed only).</param>
public sealed record HtlcRemoval(
    HtlcRemovalKind Kind,
    Secret? PaymentPreimage = null,
    ReadOnlyMemory<byte> Reason = default,
    ushort FailureCode = 0,
    ReadOnlyMemory<byte> Sha256OfOnion = default)
{
    public static HtlcRemoval Fulfill(Secret paymentPreimage) => new(HtlcRemovalKind.Fulfill, paymentPreimage);

    public static HtlcRemoval Fail(ReadOnlyMemory<byte> reason) => new(HtlcRemovalKind.Fail, Reason: reason);

    public static HtlcRemoval FailMalformed(ushort failureCode, ReadOnlyMemory<byte> sha256OfOnion) =>
        new(HtlcRemovalKind.FailMalformed, FailureCode: failureCode, Sha256OfOnion: sha256OfOnion);

    /// <summary>An HTLC we offered that was settled on chain without a preimage (BOLT 5 plan O3-T4).</summary>
    public static HtlcRemoval OnchainTimeout() => new(HtlcRemovalKind.OnchainTimeout);

    /// <summary>True for a fulfill: the amount goes to the receiver of the HTLC.</summary>
    public bool IsFulfill => Kind == HtlcRemovalKind.Fulfill;
}