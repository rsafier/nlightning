namespace NLightning.Domain.Protocol.Onion.Validators;

using Enums;

/// <summary>
/// BOLT 2 receiver checks for <c>update_fail_malformed_htlc</c>, before the failure is converted with
/// <see cref="Models.FailureMessage.FromMalformed"/> and returned upstream in <c>update_fail_htlc</c>.
/// </summary>
public static class MalformedHtlcValidator
{
    /// <summary>
    /// Checks a received <c>update_fail_malformed_htlc</c> against the onion this node sent in the HTLC.
    /// </summary>
    /// <param name="failureCode">The <c>failure_code</c> of the message.</param>
    /// <param name="sha256OfOnion">The <c>sha256_of_onion</c> of the message.</param>
    /// <param name="sentOnionSha256">SHA256 of the <c>onion_routing_packet</c> this node put in the HTLC.</param>
    /// <returns>
    /// <see cref="MalformedHtlcCheckResult.BadOnionBitNotSet"/> first (a protocol violation), then
    /// <see cref="MalformedHtlcCheckResult.Sha256OfOnionMismatch"/>, else <see cref="MalformedHtlcCheckResult.Valid"/>.
    /// </returns>
    public static MalformedHtlcCheckResult Validate(ushort failureCode, ReadOnlySpan<byte> sha256OfOnion,
                                                    ReadOnlySpan<byte> sentOnionSha256)
    {
        if ((failureCode & (ushort)FailureCodeFlags.BadOnion) == 0)
            return MalformedHtlcCheckResult.BadOnionBitNotSet;

        return sha256OfOnion.SequenceEqual(sentOnionSha256)
                   ? MalformedHtlcCheckResult.Valid
                   : MalformedHtlcCheckResult.Sha256OfOnionMismatch;
    }
}