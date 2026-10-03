using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Revoked;

using Application.Onchain.Resolvers.Revoked;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// <see cref="PenaltyTransactionComposer"/> with mocked builders: what happens when a transaction cannot be built or
/// a batch cannot be outbid (the happy paths run with real builders in <see cref="RevokedResolutionTests"/>).
/// </summary>
public class PenaltyTransactionComposerTests
{
    private const uint Height = 1_000;
    private const uint Estimate = 2_500;

    private static readonly ChannelId s_channelId = RealSigningCommitmentPair.ChannelId;
    private static readonly TxId s_commitmentTxId = new(Enumerable.Repeat((byte)0xC0, 32).ToArray());
    private static readonly TxId s_batchTxId = new(Enumerable.Repeat((byte)0xBA, 32).ToArray());

    private static readonly byte[] s_destination =
        new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    private readonly Mock<IPenaltyTransactionBuilder> _penaltyBuilder = new();
    private readonly Mock<ISweepTransactionBuilder> _sweepBuilder = new();

    private PenaltyTransactionComposer CreateComposer() =>
        new(_penaltyBuilder.Object, _sweepBuilder.Object, new Mock<ILightningSigner>().Object, new SweepFeePolicy(),
            NullLogger.Instance);

    private static PenaltyNeed Need(uint vout, ulong amountSat, uint? deadline, TxId? resolvingTxId = null)
    {
        var row = new OutputResolutionModel
        {
            TransactionId = s_commitmentTxId,
            OutputIndex = vout,
            ChannelId = s_channelId,
            Descriptor = OutputDescriptorKind.RevokedToLocal,
            State = resolvingTxId is null ? OutputResolutionState.Pending : OutputResolutionState.Broadcast,
            ResolvingTransactionId = resolvingTxId,
            DeadlineHeight = deadline
        };
        var input = new SweepInput(s_commitmentTxId, vout, amountSat, SweepSpendKind.RevokedDelayedOutput,
                                   new byte[80], 144);
        return new PenaltyNeed(row, input, deadline);
    }

    [Fact]
    public async Task Given_BuilderRefusesAnInput_When_Composed_Then_AlertAndRowKeptForTheNextBlock()
    {
        // Arrange: a non-dust output the builder refuses for another reason (a programming or input error)
        _penaltyBuilder.Setup(b => b.BuildSingle(It.IsAny<SweepInput>(), It.IsAny<byte[]>(), It.IsAny<uint>()))
                       .Throws(new ArgumentException("bad input"));
        var need = Need(0, 500_000, Height + 100);

        // Act
        var actions = await CreateComposer().ComposeAsync(s_channelId, [need], [need.Row],
                                                          new Dictionary<(TxId, uint), TxId>(), Height, Estimate,
                                                          s_destination, _ => Task.FromResult<ulong?>(null));

        // Assert: never Ignored (a final state), only the alert; the row is untouched so the next round retries
        var alert = Assert.IsType<AlertAction>(Assert.Single(actions));
        Assert.Equal("B5-REV-03", alert.RequirementId);
        Assert.Contains("bad input", alert.Message);
    }

    [Fact]
    public async Task Given_DustOutput_When_Composed_Then_Abandoned()
    {
        // Arrange: an output that cannot pay its own fee at the floor
        var need = Need(0, 100, Height + 100);

        // Act
        var actions = await CreateComposer().ComposeAsync(s_channelId, [need], [need.Row],
                                                          new Dictionary<(TxId, uint), TxId>(), Height, Estimate,
                                                          s_destination, _ => Task.FromResult<ulong?>(null));

        // Assert
        var upsert = Assert.Single(actions.OfType<UpsertOutputAction>());
        Assert.Equal(OutputResolutionState.Ignored, upsert.Output.State);
        Assert.Single(actions.OfType<AlertAction>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(400_000UL)]
    public async Task Given_UrgentBatchMember_When_BatchFeeUnknownOrNotOutbiddable_Then_BatchKept(ulong? batchFee)
    {
        // Arrange: a batch of two, one member within security_delay of its deadline; the batch's fee is unknown, or
        // higher than the urgent output (300,000 sat) could pay alone
        var urgent = Need(0, 300_000, Height + 5, s_batchTxId);
        var other = Need(1, 900_000, Height + 500, s_batchTxId);

        // Act
        var actions = await CreateComposer().ComposeAsync(s_channelId, [urgent, other], [urgent.Row, other.Row],
                                                          new Dictionary<(TxId, uint), TxId>(), Height, Estimate,
                                                          s_destination, _ => Task.FromResult(batchFee));

        // Assert: no single is built (each would be refused as a replacement), the rows keep the batch, and an alert
        var alert = Assert.IsType<AlertAction>(Assert.Single(actions));
        Assert.Equal("B5-REV-08", alert.RequirementId);
        _penaltyBuilder.VerifyNoOtherCalls();
        _sweepBuilder.VerifyNoOtherCalls();
    }
}