namespace NLightning.Domain.Tests.Channels.Splicing;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// Pins the wire numbers and the implemented read-only members of the splicing contracts (splicing plan SP1-0).
/// </summary>
public class SpliceContractTests
{
    private static readonly CompactPubKey s_localKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly CompactPubKey s_remoteKey =
        Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

    private static readonly TxId s_fundingTxId = Enumerable.Repeat((byte)0x11, 32).ToArray();
    private static readonly TxId s_spliceTxId = Enumerable.Repeat((byte)0x22, 32).ToArray();

    [Fact]
    public void Given_SplicingMessageTypes_When_Read_Then_TheyMatchBolt2()
    {
        // Assert (BOLT 2 master, "Channel Splicing" and "Batching channel messages")
        Assert.Equal(77, (ushort)MessageTypes.SpliceLocked);
        Assert.Equal(80, (ushort)MessageTypes.SpliceInit);
        Assert.Equal(81, (ushort)MessageTypes.SpliceAck);
        Assert.Equal(127, (ushort)MessageTypes.StartBatch);
        Assert.Equal(5UL, (ulong)TlvConstants.MyCurrentFundingLocked);
        Assert.Equal(1UL, (ulong)TlvConstants.StartBatchMessageType);
    }

    [Fact]
    public void Given_AFundingOutputWithItsOutpoint_When_Converted_Then_ItIsTheCurrentInitialFunding()
    {
        // Arrange
        var output = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), s_localKey, s_remoteKey, s_fundingTxId,
                                           1);

        // Act
        var funding = ChannelFunding.FromFundingOutput(output);

        // Assert
        Assert.NotNull(funding);
        Assert.Equal(s_fundingTxId, funding.FundingTxId);
        Assert.Equal((ushort)1, funding.OutputIndex);
        Assert.Equal(1_000_000UL, funding.CapacitySatoshis);
        Assert.Equal(1_000_000_000UL, funding.CapacityMsat);
        Assert.Equal(0u, funding.LocalFundingKeyIndex);
        Assert.Equal(ChannelFundingKind.Initial, funding.Kind);
        Assert.Equal(ChannelFundingStatus.Current, funding.Status);
        Assert.True(funding.IsActive);
        Assert.Equal(funding, ChannelFunding.FromFundingOutput(output));
    }

    [Fact]
    public void Given_AFundingOutputWithoutItsOutpoint_When_Converted_Then_Null()
    {
        // Arrange
        var output = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), s_localKey, s_remoteKey);

        // Act & Assert
        Assert.Null(ChannelFunding.FromFundingOutput(output));
    }

    [Fact]
    public void Given_APendingSplice_When_ReadingTheFundingSet_Then_CurrentComesFirst()
    {
        // Arrange
        var current = new ChannelFunding(s_fundingTxId, 0, 1_000_000, s_localKey, s_remoteKey, 0, 0, 0,
                                         ChannelFundingKind.Initial, ChannelFundingStatus.Current);
        var pending = new ChannelFunding(s_spliceTxId, 0, 1_100_000, s_localKey, s_remoteKey, 1, 100_000_000, 0,
                                         ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_500, 120);

        // Act
        var single = FundingSet.Single(current);
        var set = new FundingSet(current, [pending]);

        // Assert
        Assert.False(single.HasPending);
        Assert.Equal(1, single.ActiveCount);
        Assert.True(set.HasPending);
        Assert.Equal(2, set.ActiveCount);
        Assert.Equal([current, pending], set.Active);
        Assert.Same(pending, set.Find(s_spliceTxId));
        Assert.Null(set.Find(TxId.Zero));
    }

    [Fact]
    public void Given_BothReestablishTlvs_When_Built_Then_TheExtensionHoldsBoth()
    {
        // Arrange
        var payload = new ChannelReestablishPayload(new ChannelId(new byte[32]), s_localKey, 1, 0, new byte[32]);

        // Act
        var message = new ChannelReestablishMessage(payload, new NextFundingTlv((byte[])s_spliceTxId, 1),
                                                    new MyCurrentFundingLockedTlv(s_fundingTxId,
                                                                                  MyCurrentFundingLockedTlv
                                                                                     .AnnouncementSignaturesFlag));

        // Assert
        Assert.NotNull(message.Extension);
        Assert.True(message.Extension.TryGetTlv(TlvConstants.NextFunding, out _));
        Assert.True(message.Extension.TryGetTlv(TlvConstants.MyCurrentFundingLocked, out var locked));
        Assert.Equal([.. (byte[])s_fundingTxId, 0x01], locked!.Value);
    }
}