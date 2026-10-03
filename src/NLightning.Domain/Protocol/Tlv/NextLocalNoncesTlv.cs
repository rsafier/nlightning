namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Models;

/// <summary>
/// Next Local Nonces TLV.
/// </summary>
/// <remarks>
/// Simple taproot channels <c>next_local_nonces</c>, type 22 of <c>revoke_and_ack</c> and <c>channel_reestablish</c>:
/// [<c>...*nonce_entry</c>], each [<c>32*byte</c>:<c>funding_txid</c>] [<c>66*byte</c>:<c>public_nonce</c>], one per
/// active funding (the txid in internal byte order, entries sorted by it; see <see cref="FundingNonces"/>). LND 0.21
/// sends only this map (never a type 4 nonce) on final taproot channels. Converted by
/// <c>NextLocalNoncesTlvConverter</c>, which refuses a length that is not a multiple of
/// <see cref="FundingNonces.EntryLength"/>, more than <see cref="FundingNonces.MaxEntries"/> entries and a txid twice.
/// </remarks>
public sealed class NextLocalNoncesTlv : BaseTlv
{
    /// <summary>The nonces, by funding txid.</summary>
    public FundingNonces Nonces { get; }

    public NextLocalNoncesTlv(FundingNonces nonces) : base(TaprootTlvConstants.NextLocalNonces)
    {
        ArgumentNullException.ThrowIfNull(nonces);
        Nonces = nonces;

        Value = nonces.ToBytes();
        Length = Value.Length;
    }
}