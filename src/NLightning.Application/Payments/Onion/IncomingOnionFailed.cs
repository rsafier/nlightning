namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Fail the HTLC with <c>update_fail_htlc</c>: the onion peeled, but its payload is invalid
/// (<c>invalid_onion_payload</c>), it was replayed, or it needs route blinding we do not support. The reason is
/// <c>IFailureOnionService.CreateErrorPacket(SharedSecret, Failure)</c>.
/// </summary>
/// <param name="SharedSecret">The shared secret with the origin.</param>
/// <param name="Failure">The failure to return to the origin.</param>
public sealed record IncomingOnionFailed(Secret SharedSecret, FailureMessage Failure) : IncomingOnionResult
{
    /// <inheritdoc />
    public override Secret? SharedSecretOrNull => SharedSecret;
}