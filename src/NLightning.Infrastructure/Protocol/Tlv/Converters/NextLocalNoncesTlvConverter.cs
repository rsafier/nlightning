using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>next_local_nonces</c> (type 22 of revoke_and_ack and channel_reestablish): entries of a 32-byte funding
/// txid (internal byte order) and a 66-byte nonce.
/// </summary>
/// <remarks>
/// Refuses a length that is not a multiple of <see cref="FundingNonces.EntryLength"/> and more than
/// <see cref="FundingNonces.MaxEntries"/> entries (as LND 0.21 does) and a txid twice; accepts the entries in any order
/// (the spec does not order them) and an empty map.
/// </remarks>
public class NextLocalNoncesTlvConverter : ITlvConverter<NextLocalNoncesTlv>
{
    public BaseTlv ConvertToBase(NextLocalNoncesTlv tlv)
    {
        return tlv;
    }

    public NextLocalNoncesTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TaprootTlvConstants.NextLocalNonces)
            throw new InvalidCastException("Invalid TLV type");

        var value = baseTlv.Value;
        if (baseTlv.Length != (ulong)value.Length || value.Length % FundingNonces.EntryLength != 0)
            throw new InvalidCastException(
                $"Invalid length: next_local_nonces holds {value.Length} bytes, not a multiple of "
              + $"{FundingNonces.EntryLength}");

        var count = value.Length / FundingNonces.EntryLength;
        if (count > FundingNonces.MaxEntries)
            throw new InvalidCastException(
                $"next_local_nonces holds {count} entries, more than {FundingNonces.MaxEntries}");

        var entries = new (TxId, MusigPublicNonce)[count];
        for (var i = 0; i < count; i++)
        {
            var entry = value.AsSpan(i * FundingNonces.EntryLength, FundingNonces.EntryLength);
            entries[i] = (new TxId(entry[..CryptoConstants.Sha256HashLen].ToArray()),
                          new MusigPublicNonce(entry[CryptoConstants.Sha256HashLen..].ToArray()));
        }

        try
        {
            return new NextLocalNoncesTlv(new FundingNonces(entries));
        }
        catch (ArgumentException e)
        {
            throw new InvalidCastException(e.Message, e);
        }
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as NextLocalNoncesTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(NextLocalNoncesTlv)}"));
    }
}