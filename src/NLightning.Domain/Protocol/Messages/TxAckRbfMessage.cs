namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a tx_ack_rbf message.
/// </summary>
/// <remarks>
/// The tx_ack_rbf message acknowledges the replacement of the transaction.
/// The message type is 73.
/// </remarks>
public sealed class TxAckRbfMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new TxAckRbfPayload Payload { get => (TxAckRbfPayload)base.Payload; }

    public FundingOutputContributionTlv? FundingOutputContributionTlv { get; }
    public RequireConfirmedInputsTlv? RequireConfirmedInputsTlv { get; }

    /// <summary>Liquidity ads (TLV 1339, NL-850).</summary>
    public ProvideFundingTlv? ProvideFundingTlv { get; }

    public TxAckRbfMessage(TxAckRbfPayload payload, FundingOutputContributionTlv? fundingOutputContributionTlv = null,
                           RequireConfirmedInputsTlv? requireConfirmedInputsTlv = null, ProvideFundingTlv? provideFundingTlv = null)
        : base(MessageTypes.TxAckRbf, payload)
    {
        ProvideFundingTlv = provideFundingTlv;
        FundingOutputContributionTlv = fundingOutputContributionTlv;
        RequireConfirmedInputsTlv = requireConfirmedInputsTlv;

        if (FundingOutputContributionTlv is not null || RequireConfirmedInputsTlv is not null || provideFundingTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(FundingOutputContributionTlv, RequireConfirmedInputsTlv, provideFundingTlv);
        }
    }
}