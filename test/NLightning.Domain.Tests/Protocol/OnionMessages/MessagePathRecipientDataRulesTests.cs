namespace NLightning.Domain.Tests.Protocol.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

/// <summary>
/// BOLT 4 onion-message rules (OM0-T4): reader rules OM-R-02/03/04/05/07 and creator rules OM-S-04, as tables.
/// </summary>
public class MessagePathRecipientDataRulesTests
{
    private static readonly CompactPubKey s_nodeId =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));

    private static readonly ShortChannelId s_scid = new(700_000, 1, 0);

    public enum Data
    {
        Empty,
        NextNodeId,
        ShortChannelId,
        NextNodeIdAndShortChannelId,
        NextNodeIdAndOverride,
        NextNodeIdAndPadding,
        NextNodeIdAndUnknownOdd,
        NextNodeIdAndPathId,
        PathId,
        EmptyPathId,
        NextNodeIdAndPaymentRelay,
        NextNodeIdAndPaymentConstraints,
        PathIdAndPaymentRelay,
        PathIdAndPaymentConstraints,
        NextNodeIdAndZeroAllowedFeatures,
        NextNodeIdAndEvenAllowedFeature,
        NextNodeIdAndOddAllowedFeature,
        PathIdAndOddAllowedFeature,
        PathIdAndEmptyAllowedFeatures
    }

    private static BlindedRecipientData Build(Data data) => data switch
    {
        Data.Empty => new BlindedRecipientData(),
        Data.NextNodeId => new BlindedRecipientData { NextNodeId = s_nodeId },
        Data.ShortChannelId => new BlindedRecipientData { ShortChannelId = s_scid },
        Data.NextNodeIdAndShortChannelId => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            ShortChannelId = s_scid
        },
        Data.NextNodeIdAndOverride => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            NextPathKeyOverride = s_nodeId
        },
        Data.NextNodeIdAndPadding => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            Padding = new byte[5]
        },
        Data.NextNodeIdAndUnknownOdd => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            UnknownOddRecords = [new KeyValuePair<ulong, ReadOnlyMemory<byte>>(561, new byte[] { 0x12, 0x34, 0x56 })]
        },
        Data.NextNodeIdAndPathId => new BlindedRecipientData { NextNodeId = s_nodeId, PathId = new byte[32] },
        Data.PathId => new BlindedRecipientData { PathId = new byte[32] },
        Data.EmptyPathId => new BlindedRecipientData { PathId = ReadOnlyMemory<byte>.Empty },
        Data.NextNodeIdAndPaymentRelay => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            PaymentRelay = new BlindedPaymentRelay(144, 100, 1000)
        },
        Data.NextNodeIdAndPaymentConstraints => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            PaymentConstraints = new BlindedPaymentConstraints(800_000, 1)
        },
        Data.PathIdAndPaymentRelay => new BlindedRecipientData
        {
            PathId = new byte[32],
            PaymentRelay = new BlindedPaymentRelay(144, 100, 1000)
        },
        Data.PathIdAndPaymentConstraints => new BlindedRecipientData
        {
            PathId = new byte[32],
            PaymentConstraints = new BlindedPaymentConstraints(800_000, 1)
        },
        Data.NextNodeIdAndZeroAllowedFeatures => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            AllowedFeatures = new byte[] { 0x00, 0x00 }
        },
        Data.NextNodeIdAndEvenAllowedFeature => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            AllowedFeatures = new byte[] { 0x01 }
        },
        Data.NextNodeIdAndOddAllowedFeature => new BlindedRecipientData
        {
            NextNodeId = s_nodeId,
            AllowedFeatures = new byte[] { 0x02, 0x00 }
        },
        Data.PathIdAndOddAllowedFeature => new BlindedRecipientData
        {
            PathId = new byte[32],
            AllowedFeatures = new byte[] { 0x80 }
        },
        Data.PathIdAndEmptyAllowedFeatures => new BlindedRecipientData
        {
            PathId = new byte[32],
            AllowedFeatures = ReadOnlyMemory<byte>.Empty
        },
        _ => throw new ArgumentOutOfRangeException(nameof(data))
    };

    // Reader (we received the message): rule, data, final hop?, accepted?
    public static TheoryData<string, Data, bool, bool> ReaderTable => new()
    {
        { "OM-R-05 forward by next_node_id", Data.NextNodeId, false, true },
        { "OM-R-05 forward by short_channel_id", Data.ShortChannelId, false, true },
        { "OM-R-05 both: next_node_id wins, not refused", Data.NextNodeIdAndShortChannelId, false, true },
        { "OM-R-05 next_path_key_override", Data.NextNodeIdAndOverride, false, true },
        { "padding ignored", Data.NextNodeIdAndPadding, false, true },
        { "unknown odd ignored", Data.NextNodeIdAndUnknownOdd, false, true },
        { "OM-R-05 non-final without next hop", Data.Empty, false, false },
        { "OM-R-05 non-final with only path_id", Data.PathId, false, false },
        { "OM-R-04 non-final with path_id", Data.NextNodeIdAndPathId, false, false },
        { "OM-R-04 non-final with empty path_id", Data.EmptyPathId, false, false },
        { "OM-R-03 zero allowed_features is no feature", Data.NextNodeIdAndZeroAllowedFeatures, false, true },
        { "OM-R-03 even allowed feature", Data.NextNodeIdAndEvenAllowedFeature, false, false },
        { "OM-R-03 odd allowed feature", Data.NextNodeIdAndOddAllowedFeature, false, false },
        { "OM-R-03 odd allowed feature, final hop", Data.PathIdAndOddAllowedFeature, true, false },
        { "OM-R-03 empty allowed_features, final hop", Data.PathIdAndEmptyAllowedFeatures, true, true },
        { "final hop with path_id", Data.PathId, true, true },
        { "final hop without path_id", Data.Empty, true, true },
        { "final hop with a next hop", Data.NextNodeId, true, true },
        { "no reader rule on payment_relay", Data.NextNodeIdAndPaymentRelay, false, true },
        { "no reader rule on payment_constraints", Data.PathIdAndPaymentConstraints, true, true }
    };

    [Theory]
    [MemberData(nameof(ReaderTable))]
    public void Given_RecipientData_When_Read_Then_TableOutcome(string rule, Data data, bool isFinalHop,
                                                                  bool accepted)
    {
        // Act
        var ok = MessagePathRecipientDataRules.TryValidateForReader(Build(data), isFinalHop, out var reason);

        // Assert
        Assert.True(accepted == ok, $"{rule}: {reason}");
        Assert.Equal(accepted, reason is null);
    }

    // Creator (we build the path): rule, data, final hop?, accepted?
    public static TheoryData<string, Data, bool, bool> WriterTable => new()
    {
        { "OM-S-04 next_node_id", Data.NextNodeId, false, true },
        { "OM-S-04 short_channel_id", Data.ShortChannelId, false, true },
        { "next_path_key_override (OM-S-05 prefix)", Data.NextNodeIdAndOverride, false, true },
        { "padding (OM-S-08)", Data.NextNodeIdAndPadding, false, true },
        { "OM-S-04 non-final without next hop", Data.Empty, false, false },
        { "OM-S-04 payment_relay", Data.NextNodeIdAndPaymentRelay, false, false },
        { "OM-S-04 payment_constraints", Data.NextNodeIdAndPaymentConstraints, false, false },
        { "OM-S-04 payment_relay, final hop", Data.PathIdAndPaymentRelay, true, false },
        { "OM-S-04 payment_constraints, final hop", Data.PathIdAndPaymentConstraints, true, false },
        { "OM-R-04 path_id on a non-final hop", Data.NextNodeIdAndPathId, false, false },
        { "OM-R-03 allowed feature", Data.NextNodeIdAndOddAllowedFeature, false, false },
        { "zero allowed_features", Data.NextNodeIdAndZeroAllowedFeatures, false, true },
        { "final hop path_id (OM-S-06)", Data.PathId, true, true },
        { "final hop without path_id", Data.Empty, true, true }
    };

    [Theory]
    [MemberData(nameof(WriterTable))]
    public void Given_RecipientData_When_Written_Then_TableOutcome(string rule, Data data, bool isFinalHop,
                                                                     bool accepted)
    {
        // Act
        var ok = MessagePathRecipientDataRules.TryValidateForWriter(Build(data), isFinalHop, out var reason);

        // Assert
        Assert.True(accepted == ok, $"{rule}: {reason}");
        Assert.Equal(accepted, reason is null);
    }

    public enum Payload
    {
        Empty,
        EncryptedRecipientDataOnly,
        WithReplyPath,
        WithOddRecord,
        WithPayloadField,
        WithTwoPayloadFields,
        WithReplyPathAndPayloadField,
        PayloadFieldWithoutEncryptedRecipientData
    }

    private static readonly WireBlindedPath s_replyPath =
        new(SciddirOrPubkey.FromNodeId(s_nodeId), s_nodeId, [new BlindedPathHop(s_nodeId, new byte[16])]);

    private static OnionMessageTlvs Build(Payload payload)
    {
        var erd = (ReadOnlyMemory<byte>?)new byte[] { 0xaa };
        var field64 = new OnionMessageTlvRecord(64, new byte[] { 0x01 });
        var field66 = new OnionMessageTlvRecord(66, new byte[] { 0x02 });
        return payload switch
        {
            Payload.Empty => new OnionMessageTlvs(null, null, []),
            Payload.EncryptedRecipientDataOnly => new OnionMessageTlvs(null, erd, []),
            Payload.WithReplyPath => new OnionMessageTlvs(s_replyPath, erd, []),
            Payload.WithOddRecord => new OnionMessageTlvs(null, erd, [new OnionMessageTlvRecord(1, new byte[5])]),
            Payload.WithPayloadField => new OnionMessageTlvs(null, erd, [field64]),
            Payload.WithTwoPayloadFields => new OnionMessageTlvs(null, erd, [field64, field66]),
            Payload.WithReplyPathAndPayloadField => new OnionMessageTlvs(s_replyPath, erd, [field64]),
            Payload.PayloadFieldWithoutEncryptedRecipientData => new OnionMessageTlvs(null, null, [field64]),
            _ => throw new ArgumentOutOfRangeException(nameof(payload))
        };
    }

    // onionmsg_tlv: rule, payload, accepted as non-final, accepted as final
    public static TheoryData<string, Payload, bool, bool> PayloadTable => new()
    {
        { "OM-R-02 no encrypted_recipient_data", Payload.Empty, false, false },
        { "OM-R-02 final field but no encrypted_recipient_data", Payload.PayloadFieldWithoutEncryptedRecipientData, false, false },
        { "encrypted_recipient_data only", Payload.EncryptedRecipientDataOnly, true, true },
        { "OM-R-04 reply_path on a non-final hop", Payload.WithReplyPath, false, true },
        { "OM-R-04 odd record on a non-final hop (vector: Dave's 'hello')", Payload.WithOddRecord, false, true },
        { "OM-R-04 payload field on a non-final hop", Payload.WithPayloadField, false, true },
        { "OM-R-07 two payload fields", Payload.WithTwoPayloadFields, false, false },
        { "reply_path and one payload field", Payload.WithReplyPathAndPayloadField, false, true }
    };

    [Theory]
    [MemberData(nameof(PayloadTable))]
    public void Given_OnionMessageTlv_When_Validated_Then_TableOutcome(string rule, Payload payload,
                                                                         bool acceptedAsNonFinal,
                                                                         bool acceptedAsFinal)
    {
        // Arrange
        var tlvs = Build(payload);

        // Act
        var nonFinal = MessagePathRecipientDataRules.TryValidateNonFinalPayload(tlvs, out var nonFinalReason);
        var final = MessagePathRecipientDataRules.TryValidateFinalPayload(tlvs, out var finalReason);

        // Assert
        Assert.True(acceptedAsNonFinal == nonFinal, $"{rule} (non-final): {nonFinalReason}");
        Assert.True(acceptedAsFinal == final, $"{rule} (final): {finalReason}");
    }
}