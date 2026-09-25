namespace NLightning.Domain.Protocol.Onion.Enums;

/// <summary>
/// The outcome of checking a received <c>update_fail_malformed_htlc</c> (BOLT 2).
/// </summary>
public enum MalformedHtlcCheckResult
{
    /// <summary>
    /// The failure can be converted and returned upstream (<see cref="Models.FailureMessage.FromMalformed"/>).
    /// </summary>
    Valid,

    /// <summary>
    /// The BADONION bit of <c>failure_code</c> is not set: the receiver MUST send a <c>warning</c> and close the
    /// connection, or send an <c>error</c> and fail the channel.
    /// </summary>
    BadOnionBitNotSet,

    /// <summary>
    /// The <c>sha256_of_onion</c> does not match the onion this node sent and is not all zero: the receiver MAY retry or choose an
    /// alternate error response.
    /// </summary>
    Sha256OfOnionMismatch
}