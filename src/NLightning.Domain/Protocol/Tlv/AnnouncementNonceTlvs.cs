namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.Constants;
using Crypto.ValueObjects;

/// <summary>
/// <c>announcement_node_pubnonce</c> (type 0 of <c>channel_ready</c> and <c>splice_locked</c>, taproot gossip,
/// BOLTs PR #1059): [<c>66*byte</c>:<c>public_nonce</c>], the nonce of the sender's node-key partial signature of the
/// <c>channel_announcement_2</c>. Converted by <c>AnnouncementNodeNonceTlvConverter</c>.
/// </summary>
public sealed class AnnouncementNodeNonceTlv(MusigPublicNonce nonce)
    : PublicNonceTlv(TaprootTlvConstants.AnnouncementNodeNonce, nonce);

/// <summary>
/// <c>announcement_bitcoin_pubnonce</c> (type 2 of <c>channel_ready</c> and <c>splice_locked</c>, BOLTs PR #1059):
/// the nonce of the sender's funding-key partial signature. Converted by
/// <c>AnnouncementBitcoinNonceTlvConverter</c>.
/// </summary>
public sealed class AnnouncementBitcoinNonceTlv(MusigPublicNonce nonce)
    : PublicNonceTlv(TaprootTlvConstants.AnnouncementBitcoinNonce, nonce);

/// <summary>
/// <c>announcement_nonces</c> (type 7 of <c>channel_reestablish</c>, BOLTs PR #1059):
/// [<c>66*byte</c>:<c>announcement_node_pubnonce</c>] [<c>66*byte</c>:<c>announcement_bitcoin_pubnonce</c>], fresh
/// nonces for the <c>announcement_signatures_2</c> of the funding named by <c>my_current_funding_locked</c>.
/// </summary>
public sealed class AnnouncementNoncesTlv : BaseTlv
{
    /// <summary>The value length: two public nonces.</summary>
    public const int ValueLength = 2 * MusigConstants.PublicNonceLen;

    public MusigPublicNonce NodeNonce { get; }

    public MusigPublicNonce BitcoinNonce { get; }

    public AnnouncementNoncesTlv(MusigPublicNonce nodeNonce, MusigPublicNonce bitcoinNonce)
        : base(TaprootTlvConstants.AnnouncementNonces)
    {
        NodeNonce = nodeNonce;
        BitcoinNonce = bitcoinNonce;
        Value = [.. (byte[])nodeNonce, .. (byte[])bitcoinNonce];
        Length = Value.Length;
    }
}