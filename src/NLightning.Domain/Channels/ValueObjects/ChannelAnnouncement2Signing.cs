namespace NLightning.Domain.Channels.ValueObjects;

using Crypto.ValueObjects;

/// <summary>
/// Our two MuSig2 public nonces for a <c>channel_announcement_2</c> session (taproot gossip, BOLTs PR #1059): one for
/// the node-key partial signature, one for the funding-key one. Sent in <c>channel_ready</c> (TLVs 0/2) or
/// <c>channel_reestablish</c> (TLV 7); the secret halves never leave the signer.
/// </summary>
public readonly record struct ChannelAnnouncement2Nonces(MusigPublicNonce NodeNonce, MusigPublicNonce BitcoinNonce);

/// <summary>
/// One side's two MuSig2 partial signatures of a <c>channel_announcement_2</c> (node key, funding key), the
/// <c>partial_signatures</c> record of <c>announcement_signatures_2</c>.
/// </summary>
public readonly record struct ChannelAnnouncement2PartialSignatures(MusigPartialSignature NodeSignature,
                                                                    MusigPartialSignature BitcoinSignature);