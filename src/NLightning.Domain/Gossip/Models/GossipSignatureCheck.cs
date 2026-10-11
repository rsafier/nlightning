namespace NLightning.Domain.Gossip.Models;

using Crypto.ValueObjects;

/// <summary>
/// One BOLT 7 signature to check: <paramref name="Signature"/> (64-byte compact, high-S allowed) over
/// <paramref name="MessageHash"/> (the double-SHA256 of the message's signed range) by <paramref name="PublicKey"/>.
/// </summary>
/// <param name="MessageHash">The 32-byte hash that was signed, used as is.</param>
/// <param name="Signature">The compact signature.</param>
/// <param name="PublicKey">The node id or bitcoin (funding) key that must have signed.</param>
public readonly record struct GossipSignatureCheck(Hash MessageHash, CompactSignature Signature,
                                                   CompactPubKey PublicKey);