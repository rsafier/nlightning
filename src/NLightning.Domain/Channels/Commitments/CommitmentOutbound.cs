namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;

/// <summary>
/// A message the engine asks the caller to send, after the transition that produced it is persisted (invariant I1).
/// </summary>
/// <remarks>The engine stays free of wire types; the Application layer turns these into BOLT 2 messages.</remarks>
public abstract record CommitmentOutbound;

/// <summary>Send <c>update_add_htlc</c> for <paramref name="Htlc"/>.</summary>
public sealed record OutboundAddHtlc(HtlcRecord Htlc) : CommitmentOutbound;

/// <summary>Send <c>update_fulfill_htlc</c>, with its <c>attribution_data</c> and <c>fulfillment_payload</c> TLVs when
/// they are not empty.</summary>
public sealed record OutboundFulfillHtlc(
    ulong Id,
    Secret PaymentPreimage,
    ReadOnlyMemory<byte> AttributionData = default,
    ReadOnlyMemory<byte> FulfillmentPayload = default) : CommitmentOutbound;

/// <summary>Send <c>update_fail_htlc</c> with the opaque <paramref name="Reason"/>, and its <c>attribution_data</c>
/// TLV when <paramref name="AttributionData"/> is not empty.</summary>
public sealed record OutboundFailHtlc(
    ulong Id,
    ReadOnlyMemory<byte> Reason,
    ReadOnlyMemory<byte> AttributionData = default) : CommitmentOutbound;

/// <summary>Send <c>update_fail_malformed_htlc</c>.</summary>
public sealed record OutboundFailMalformedHtlc(ulong Id, ushort FailureCode, ReadOnlyMemory<byte> Sha256OfOnion)
    : CommitmentOutbound;

/// <summary>Send <c>update_fee</c>.</summary>
public sealed record OutboundUpdateFee(uint FeeratePerKw) : CommitmentOutbound;

/// <summary>Send <c>commitment_signed</c> for the peer's commitment <paramref name="RemoteCommitmentNumber"/>.</summary>
public sealed record OutboundCommitmentSigned(ulong RemoteCommitmentNumber, CommitmentSignatures Signatures)
    : CommitmentOutbound;

/// <summary>
/// Send <c>revoke_and_ack</c>: <c>per_commitment_secret</c> of our commitment <paramref name="RevokedCommitmentNumber"/>
/// and <c>next_per_commitment_point</c> of our commitment <paramref name="NextCommitmentNumber"/>.
/// </summary>
/// <remarks>A placeholder: the secret is derived by the caller only <b>after</b> the new local commitment is
/// persisted (decision D3, invariant I3), so the engine never touches it.</remarks>
public sealed record OutboundRevokeAndAck(ulong RevokedCommitmentNumber, ulong NextCommitmentNumber)
    : CommitmentOutbound;