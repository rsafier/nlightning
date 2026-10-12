using System.Buffers.Binary;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.LiquidityAds;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Models;
using NLightning.Domain.Protocol.Tlv;

namespace NLightning.Infrastructure.Serialization.Wire;

/// <summary>TLV definitions reused by several peer messages.</summary>
internal static class TlvDefs
{
    public static readonly TlvDef<AttributionDataTlv> AttributionData = TlvDef.Typed<AttributionDataTlv>(TlvConstants.AttributionData,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.AttributionData)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length != AttributionDataTlv.ValueLength)
            {
                throw new InvalidCastException("Invalid length");
            }

            return new AttributionDataTlv(baseTlv.Value);
        },
        tlv => tlv);

    public static readonly TlvDef<ChannelTypeTlv> ChannelType = TlvDef.Typed<ChannelTypeTlv>(TlvConstants.ChannelType,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.ChannelType)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length == 0)
            {
                throw new InvalidCastException("Invalid length");
            }

            return new ChannelTypeTlv(baseTlv.Value);
        },
        tlv =>
        {
            tlv.Value = tlv.ChannelType;

            return tlv;
        });

    public static readonly TlvDef<FundingOutputContributionTlv> FundingOutputContribution = TlvDef.Typed<FundingOutputContributionTlv>(TlvConstants.FundingOutputContribution,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.FundingOutputContribution)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length != FundingOutputContributionTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
            {
                throw new InvalidCastException("Invalid length");
            }

            return new FundingOutputContributionTlv(BinaryPrimitives.ReadInt64BigEndian(baseTlv.Value));
        },
        tlv =>
        {
            var value = new byte[FundingOutputContributionTlv.ValueLength];
            BinaryPrimitives.WriteInt64BigEndian(value, tlv.Satoshis);
            return new BaseTlv(tlv.Type, value);
        });

    public static readonly TlvDef<NextLocalNoncesTlv> NextLocalNonces = TlvDef.Typed<NextLocalNoncesTlv>(TaprootTlvConstants.NextLocalNonces,
        baseTlv =>
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
        },
        tlv => tlv);

    public static readonly TlvDef<PartialSignatureWithNonceTlv> PartialSignatureWithNonce =
        TlvDefs.PartialSignature(TaprootTlvConstants.PartialSignatureWithNonce, value => new PartialSignatureWithNonceTlv(value));

    public static readonly TlvDef<PrevTxDetailsTlv> PrevTxDetails = TlvDef.Typed<PrevTxDetailsTlv>(InteractiveTxTlvConstants.PrevTxDetails,
        baseTlv =>
        {
            if (baseTlv.Type != InteractiveTxTlvConstants.PrevTxDetails
                     && baseTlv.Type != InteractiveTxTlvConstants.PrevTxDetailsEclair)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Value.Length < PrevTxDetailsTlv.FixedLength || baseTlv.Length != (ulong)baseTlv.Value.Length)
                throw new InvalidCastException("Invalid length");

            var value = baseTlv.Value;
            var txId = value[..CryptoConstants.Sha256HashLen];
            var amount = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(CryptoConstants.Sha256HashLen, sizeof(ulong)));
            var script = value[PrevTxDetailsTlv.FixedLength..];

            return new PrevTxDetailsTlv(txId, amount, script, baseTlv.Type);
        },
        tlv => tlv);

    public static readonly TlvDef<ProvideFundingTlv> ProvideFunding = TlvDef.Typed<ProvideFundingTlv>(TlvConstants.LiquidityAds,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.LiquidityAds)
                throw new InvalidCastException("Invalid TLV type");

            if (!LiquidityAdsCodec.TryDecodeWillFund(baseTlv.Value, out var willFund) || willFund is null)
                throw new InvalidCastException("Invalid provide_funding value");

            return new ProvideFundingTlv(willFund);
        },
        tlv => new BaseTlv(tlv.Type, tlv.Value));

    public static readonly TlvDef<NextLocalNonceTlv> NextLocalNonce =
        TlvDefs.PublicNonce(TaprootTlvConstants.NextLocalNonce, value => new NextLocalNonceTlv(value));

    public static readonly TlvDef<AnnouncementNodeNonceTlv> AnnouncementNodeNonce =
        TlvDefs.PublicNonce(TaprootTlvConstants.AnnouncementNodeNonce, value => new AnnouncementNodeNonceTlv(value));

    public static readonly TlvDef<AnnouncementBitcoinNonceTlv> AnnouncementBitcoinNonce =
        TlvDefs.PublicNonce(TaprootTlvConstants.AnnouncementBitcoinNonce, value => new AnnouncementBitcoinNonceTlv(value));

    public static readonly TlvDef<RequestFundingTlv> RequestFunding = TlvDef.Typed<RequestFundingTlv>(TlvConstants.LiquidityAds,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.LiquidityAds)
                throw new InvalidCastException("Invalid TLV type");

            if (!LiquidityAdsCodec.TryDecodeRequestFunding(baseTlv.Value, out var request) || request is null)
                throw new InvalidCastException("Invalid request_funding value");

            return new RequestFundingTlv(request);
        },
        tlv => new BaseTlv(tlv.Type, tlv.Value));

    public static readonly TlvDef<RequireConfirmedInputsTlv> RequireConfirmedInputs = TlvDef.Typed<RequireConfirmedInputsTlv>(TlvConstants.RequireConfirmedInputs,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.RequireConfirmedInputs)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length != 0)
            {
                throw new InvalidCastException("Invalid length");
            }

            return new RequireConfirmedInputsTlv();
        },
        tlv => tlv);

    public static readonly TlvDef<UpfrontShutdownScriptTlv> UpfrontShutdownScript = TlvDef.Typed<UpfrontShutdownScriptTlv>(TlvConstants.UpfrontShutdownScript,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.UpfrontShutdownScript)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            return new UpfrontShutdownScriptTlv(new BitcoinScript(baseTlv.Value));
        },
        tlv =>
        {
            tlv.Value = tlv.ShutdownScriptPubkey;

            return tlv;
        });
    /// <summary>Shared validation for the 66-byte MuSig2 public nonce value family.</summary>
    public static TlvDef<TTlv> PublicNonce<TTlv>(Domain.Protocol.ValueObjects.BigSize type,
                                               Func<MusigPublicNonce, TTlv> create) where TTlv : PublicNonceTlv
        => TlvDef.Typed(type, raw =>
        {
            if (raw.Type != type)
                throw new InvalidCastException("Invalid TLV type");
            if (raw.Length != MusigConstants.PublicNonceLen || raw.Value.Length != raw.Length)
                throw new InvalidCastException(
                    $"Invalid length: a public nonce TLV holds {MusigConstants.PublicNonceLen} bytes, not "
                  + $"{raw.Value.Length}");
            return create(new MusigPublicNonce(raw.Value.ToArray()));
        }, tlv => tlv);

    /// <summary>Shared validation for the 98-byte partial signature with nonce family.</summary>
    public static TlvDef<TTlv> PartialSignature<TTlv>(Domain.Protocol.ValueObjects.BigSize type,
                                                   Func<MusigPartialSignatureWithNonce, TTlv> create)
        where TTlv : PartialSignatureWithNonceTlv
        => TlvDef.Typed(type, raw =>
        {
            if (raw.Type != type)
                throw new InvalidCastException("Invalid TLV type");
            if (raw.Length != PartialSignatureWithNonceTlv.ValueLength || raw.Value.Length != raw.Length)
                throw new InvalidCastException(
                    $"Invalid length: a partial signature with nonce holds {PartialSignatureWithNonceTlv.ValueLength} "
                  + $"bytes, not {raw.Value.Length}");
            return create(new MusigPartialSignatureWithNonce(raw.Value));
        }, tlv => tlv);
}