namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;

/// <summary>
/// One hop of a <see cref="BlindedPath"/> (BOLT 4 <c>blinded_path_hop</c>).
/// </summary>
/// <param name="BlindedNodeId">The hop's blinded node id <c>B_i = HMAC256("blinded_node_id", ss_i) * N_i</c>.</param>
/// <param name="EncryptedRecipientData">Its <c>encrypted_data_tlv</c>, encrypted with <c>rho_i</c>
/// (ChaCha20-Poly1305, all-zero nonce).</param>
public sealed record BlindedPathHop(CompactPubKey BlindedNodeId, ReadOnlyMemory<byte> EncryptedRecipientData);