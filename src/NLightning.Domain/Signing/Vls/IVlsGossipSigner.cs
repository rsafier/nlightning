namespace NLightning.Domain.Signing.Vls;

using Crypto.ValueObjects;

/// <summary>Typed BOLT 7 signing. Payloads exclude the 64-byte signature and the message type.</summary>
public interface IVlsGossipSigner
{
    CompactSignature SignChannelUpdate(byte[] unsignedCanonicalPayload);
    CompactSignature SignNodeAnnouncement(byte[] unsignedCanonicalPayload);
}