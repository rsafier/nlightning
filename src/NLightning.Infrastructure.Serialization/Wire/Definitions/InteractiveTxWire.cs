using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Tlv;

namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of BOLT 2 "Interactive Transaction Construction": <c>tx_add_output</c> (67),
/// <c>tx_remove_input</c> (68), <c>tx_remove_output</c> (69), <c>tx_complete</c> (70), <c>tx_signatures</c> (71),
/// <c>tx_init_rbf</c> (72), <c>tx_ack_rbf</c> (73) and <c>tx_abort</c> (74), with their per-message TLV namespaces.
/// </summary>
internal static class TxAddOutputWire
{
    public static readonly MessageWire<TxAddOutputMessage> Def = new(MessageTypes.TxAddOutput, Encode, Decode);

    private static void Encode(ref WireWriter writer, TxAddOutputMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64(payload.SerialId);
        writer.U64((ulong)payload.Amount.Satoshi);
        writer.U16((ushort)payload.Script.Length);
        writer.Bytes(payload.Script);
    }

    private static WireConstruct<TxAddOutputMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var serialId = reader.U64();
        var amount = LightningMoney.Satoshis(reader.U64());
        var script = new BitcoinScript(reader.BytesArray(reader.U16()));

        return tlvs => new TxAddOutputMessage(new TxAddOutputPayload(amount, channelId, script, serialId));
    }
}

internal static class TxRemoveInputWire
{
    public static readonly MessageWire<TxRemoveInputMessage> Def = new(MessageTypes.TxRemoveInput, Encode, Decode);

    private static void Encode(ref WireWriter writer, TxRemoveInputMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.U64(message.Payload.SerialId);
    }

    private static WireConstruct<TxRemoveInputMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var serialId = reader.U64();

        return tlvs => new TxRemoveInputMessage(new TxRemoveInputPayload(channelId, serialId));
    }
}

internal static class TxRemoveOutputWire
{
    public static readonly MessageWire<TxRemoveOutputMessage> Def = new(MessageTypes.TxRemoveOutput, Encode, Decode);

    private static void Encode(ref WireWriter writer, TxRemoveOutputMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.U64(message.Payload.SerialId);
    }

    private static WireConstruct<TxRemoveOutputMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var serialId = reader.U64();

        return tlvs => new TxRemoveOutputMessage(new TxRemoveOutputPayload(channelId, serialId));
    }
}

internal static class TxCompleteWire
{
    public static readonly TlvDef<CommitNoncesTlv> CommitNonces = TlvDef.Typed<CommitNoncesTlv>(TaprootTlvConstants.CommitNonces,
        baseTlv =>
        {
            if (baseTlv.Type != TaprootTlvConstants.CommitNonces)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != CommitNoncesTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
                throw new InvalidCastException(
                    $"Invalid length: commit_nonces holds {CommitNoncesTlv.ValueLength} bytes, not {baseTlv.Value.Length}");

            return new CommitNoncesTlv(new MusigPublicNonce(baseTlv.Value[..MusigConstants.PublicNonceLen]),
                                       new MusigPublicNonce(baseTlv.Value[MusigConstants.PublicNonceLen..]));
        },
        tlv => tlv);

    public static readonly TlvDef<FundingNonceTlv> FundingNonce =
        TlvDefs.PublicNonce(TaprootTlvConstants.FundingNonce, value => new FundingNonceTlv(value));

    public static readonly MessageWire<TxCompleteMessage> Def = new(MessageTypes.TxComplete, Encode, Decode,
        TxCompleteWire.CommitNonces,
        TxCompleteWire.FundingNonce);

    private static void Encode(ref WireWriter writer, TxCompleteMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
    }

    private static WireConstruct<TxCompleteMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();

        return tlvs => new TxCompleteMessage(new TxCompletePayload(channelId),
            tlvs.Get<CommitNoncesTlv>(TaprootTlvConstants.CommitNonces),
            tlvs.Get<FundingNonceTlv>(TaprootTlvConstants.FundingNonce));
    }
}

internal static class TxSignaturesWire
{
    public static readonly TlvDef<SharedInputPartialSignatureTlv> SharedInputPartialSignature =
        TlvDefs.PartialSignature(TaprootTlvConstants.SharedInputPartialSignature, value => new SharedInputPartialSignatureTlv(value));

    public static readonly TlvDef<SharedInputSignatureTlv> SharedInputSignature = TlvDef.Typed<SharedInputSignatureTlv>(InteractiveTxTlvConstants.SharedInputSignature,
        baseTlv =>
        {
            if (baseTlv.Type != InteractiveTxTlvConstants.SharedInputSignature)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != SharedInputSignatureTlv.ValueLength
             || baseTlv.Value.Length != SharedInputSignatureTlv.ValueLength)
                throw new InvalidCastException("Invalid length");

            return new SharedInputSignatureTlv(baseTlv.Value[..SharedInputSignatureTlv.ValueLength]);
        },
        tlv => tlv);

    public static readonly MessageWire<TxSignaturesMessage> Def = new(MessageTypes.TxSignatures, Encode, Decode,
        TxSignaturesWire.SharedInputSignature,
        TxSignaturesWire.SharedInputPartialSignature);

    private static void Encode(ref WireWriter writer, TxSignaturesMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.Bytes(payload.TxId);
        writer.U16((ushort)payload.Witnesses.Count);
        foreach (var witness in payload.Witnesses)
        {
            writer.U16(witness.Length);
            writer.Bytes(witness);
        }
    }

    private static WireConstruct<TxSignaturesMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var txId = reader.BytesArray(CryptoConstants.Sha256HashLen);
        var witnessCount = reader.U16();
        var witnesses = new List<Witness>(witnessCount);
        for (var i = 0; i < witnessCount; i++)
        {
            witnesses.Add(new Witness(reader.BytesArray(reader.U16())));
        }

        return tlvs => new TxSignaturesMessage(new TxSignaturesPayload(channelId, txId, witnesses),
            tlvs.Get<SharedInputSignatureTlv>(InteractiveTxTlvConstants.SharedInputSignature),
            tlvs.Get<SharedInputPartialSignatureTlv>(TaprootTlvConstants.SharedInputPartialSignature));
    }
}

internal static class TxInitRbfWire
{
    public static readonly MessageWire<TxInitRbfMessage> Def = new(MessageTypes.TxInitRbf, Encode, Decode,
        TlvDefs.FundingOutputContribution,
        TlvDefs.RequireConfirmedInputs,
        TlvDefs.RequestFunding);

    private static void Encode(ref WireWriter writer, TxInitRbfMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U32(payload.Locktime);
        writer.U32(payload.Feerate);
    }

    private static WireConstruct<TxInitRbfMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var locktime = reader.U32();
        var feerate = reader.U32();

        return tlvs => new TxInitRbfMessage(new TxInitRbfPayload(channelId, feerate, locktime),
            tlvs.Get<FundingOutputContributionTlv>(TlvConstants.FundingOutputContribution),
            tlvs.Get<RequireConfirmedInputsTlv>(TlvConstants.RequireConfirmedInputs),
            tlvs.Get<RequestFundingTlv>(TlvConstants.LiquidityAds));
    }
}

internal static class TxAckRbfWire
{
    public static readonly MessageWire<TxAckRbfMessage> Def = new(MessageTypes.TxAckRbf, Encode, Decode,
        TlvDefs.FundingOutputContribution,
        TlvDefs.RequireConfirmedInputs,
        TlvDefs.ProvideFunding);

    private static void Encode(ref WireWriter writer, TxAckRbfMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
    }

    private static WireConstruct<TxAckRbfMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();

        return tlvs => new TxAckRbfMessage(new TxAckRbfPayload(channelId),
            tlvs.Get<FundingOutputContributionTlv>(TlvConstants.FundingOutputContribution),
            tlvs.Get<RequireConfirmedInputsTlv>(TlvConstants.RequireConfirmedInputs),
            tlvs.Get<ProvideFundingTlv>(TlvConstants.LiquidityAds));
    }
}

internal static class TxAbortWire
{
    public static readonly MessageWire<TxAbortMessage> Def = new(MessageTypes.TxAbort, Encode, Decode);

    private static void Encode(ref WireWriter writer, TxAbortMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.U16((ushort)message.Payload.Data.Length);
        writer.Bytes(message.Payload.Data);
    }

    private static WireConstruct<TxAbortMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        // BOLT 2: len is advisory — the legacy codec read at most 256 bytes and silently left any surplus unread
        var declared = reader.U16();
        var data = reader.BytesArray(Math.Min(declared, (ushort)256));

        return tlvs => new TxAbortMessage(new TxAbortPayload(channelId, data));
    }
}