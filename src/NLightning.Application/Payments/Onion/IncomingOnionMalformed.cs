namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// Fail the HTLC with <c>update_fail_malformed_htlc</c> (BOLT 2): the onion could not be peeled (bad version, key or
/// HMAC), or it travels inside a blinded route (<c>path_key</c> in <c>update_add_htlc</c>), where BOLT 4 turns every
/// error into <c>invalid_onion_blinding</c>.
/// </summary>
/// <param name="FailureCode">A BADONION code (<c>invalid_onion_version</c>, <c>invalid_onion_hmac</c>,
/// <c>invalid_onion_key</c> or <c>invalid_onion_blinding</c>).</param>
/// <param name="Sha256OfOnion">SHA256 of the whole received <c>onion_routing_packet</c>.</param>
public sealed record IncomingOnionMalformed(FailureCode FailureCode, ReadOnlyMemory<byte> Sha256OfOnion)
    : IncomingOnionResult
{
    /// <inheritdoc />
    public override Secret? SharedSecretOrNull => null;
}