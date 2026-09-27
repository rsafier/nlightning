namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;

/// <summary>
/// What a blinded hop learns from its <c>encrypted_recipient_data</c> (BOLT 4, reader of
/// <c>encrypted_recipient_data</c>).
/// </summary>
/// <param name="PathKey">The path_key <c>E_i</c> used: the <c>update_add_htlc</c> path_key or, at the introduction
/// node, the payload's <c>current_path_key</c>.</param>
/// <param name="DecryptedData">The decrypted <c>encrypted_data_tlv</c> bytes.</param>
/// <param name="RecipientData">The decoded records.</param>
/// <param name="NextPathKey">The path_key for the next hop: <c>next_path_key_override</c> when present, else
/// <c>E_{i+1} = SHA256(E_i || ss_i) * E_i</c>.</param>
public sealed record BlindedHopUnblinding(CompactPubKey PathKey, ReadOnlyMemory<byte> DecryptedData,
                                          BlindedRecipientData RecipientData, CompactPubKey NextPathKey);