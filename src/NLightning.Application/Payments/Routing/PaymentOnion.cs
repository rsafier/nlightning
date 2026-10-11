namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// The onion of one of our payments, ready to go into <c>update_add_htlc</c>, and what the origin keeps to decrypt
/// a failure.
/// </summary>
/// <param name="Route">The route the onion encodes.</param>
/// <param name="Packet">The <c>onion_routing_packet</c> for our peer (<see cref="PaymentRoute.FirstHopNodeId"/>).</param>
/// <param name="SharedSecrets">One shared secret per hop, first hop first, for
/// <c>IFailureOnionService.DecryptErrorPacket</c> (the erring hop index is the index into
/// <see cref="PaymentRoute.Hops"/>).</param>
public sealed record PaymentOnion(PaymentRoute Route, OnionPacket Packet, IReadOnlyList<Secret> SharedSecrets);