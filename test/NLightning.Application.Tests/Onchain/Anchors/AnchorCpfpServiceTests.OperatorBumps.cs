using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Onchain.Anchors;

using Application.Onchain.Anchors;
using Application.Onchain.Fees;
using Domain.Channels.Enums;
using Domain.Onchain.Enums;

/// <summary>
/// The operator's fee bump of a force close (LND's walletrpc <c>BumpForceCloseFee</c>/<c>BumpFee</c>, NL-1186): the
/// request steers the CPFP child of the unconfirmed commitment and a fresh one replaces at once.
/// </summary>
public sealed partial class AnchorCpfpServiceTests
{
    private OperatorFeeBumps Bumps => _provider.GetRequiredService<OperatorFeeBumps>();

    [Fact]
    public async Task Given_APendingChild_When_TheOperatorAsksForAHigherRate_Then_ReplacedBeforeTheRbfInterval()
    {
        // Arrange: a first child at the 10,000 sat/kw estimate
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var first = Assert.Single(_store.Children);

        // Act: the operator asks for 30,000 sat/kw; the next block is before the RBF interval
        var outcome = await Service.RequestBumpAsync(_channel.ChannelId,
                                                     new OperatorFeeBumpRequest(30_000, null, null, null, false),
                                                     cancellationToken: TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert: replaced at once at the operator's rate, then not again at the next block (the request was applied)
        Assert.Equal(AnchorBumpOutcome.Registered, outcome);
        Assert.Equal(BroadcastState.Replaced, first.State);
        Assert.Equal(2, _store.Children.Count);
        var replacement = _store.Children.Single(c => c.TransactionId != first.TransactionId);
        Assert.Equal(501u, replacement.FirstBroadcastHeight);
        Assert.True(PackageFeerate(Load(commitment), Load(replacement)) >= 30_000);
        AnchorTx.AssertScriptsValid(Load(replacement), Load(commitment), _wallet);
    }

    [Fact]
    public async Task Given_ACommitmentPayingTheEstimate_When_TheOperatorAsksForMore_Then_AChildIsMade()
    {
        // Arrange: the commitment pays 2,500 sat/kw against a 2,000 sat/kw estimate: no child of its own
        var commitment = BroadcastCommitment();
        _estimate = 2_000;
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        Assert.Empty(_store.Children);

        // Act
        await Service.RequestBumpAsync(_channel.ChannelId,
                                       new OperatorFeeBumpRequest(8_000, null, null, null, false),
                                       cancellationToken: TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert
        var child = Assert.Single(_store.Children);
        Assert.InRange(PackageFeerate(Load(commitment), Load(child)), 8_000UL, 8_050UL);
    }

    [Fact]
    public async Task Given_AWrongOutpoint_When_BumpingByAnchor_Then_RefusedAndTheRightOneRegisters()
    {
        // Arrange
        var commitment = BroadcastCommitment();
        var commitmentTx = Load(commitment);
        var anchor = FindOurAnchor(commitmentTx);
        var request = new OperatorFeeBumpRequest(null, 20_000, null, null, false);

        // Act
        var wrong = await Service.RequestBumpAsync(_channel.ChannelId, request,
                                                   (commitment.TransactionId, anchor + 1),
                                                   TestContext.Current.CancellationToken);
        var right = await Service.RequestBumpAsync(_channel.ChannelId, request, (commitment.TransactionId, anchor),
                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AnchorBumpOutcome.NotOurAnchor, wrong);
        Assert.Equal(AnchorBumpOutcome.Registered, right);
        Assert.NotNull(Bumps.GetAnchor(_channel.ChannelId, out var fresh));
        Assert.True(fresh);
    }

    [Fact]
    public async Task Given_AnOpenChannel_When_BumpingItsForceClose_Then_NotForceClosed()
    {
        // Arrange: nothing broadcast, the channel still open
        Assert.Equal(ChannelState.Open, _channel.State);

        // Act
        var outcome = await Service.RequestBumpAsync(_channel.ChannelId,
                                                     new OperatorFeeBumpRequest(20_000, null, null, null, false),
                                                     cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AnchorBumpOutcome.NotForceClosed, outcome);
        Assert.Null(Bumps.GetAnchor(_channel.ChannelId, out _));
    }

    [Fact]
    public async Task Given_AnOperatorBudgetBelowTheOwnCap_When_TheCommitmentHasAnHtlcDeadline_Then_TheOwnCapStays()
    {
        // Arrange: the in-flight HTLC gives the commitment a deadline; the operator's budget is 1 sat
        var commitment = BroadcastCommitment();
        await Service.RequestBumpAsync(_channel.ChannelId, new OperatorFeeBumpRequest(null, 1, null, null, false),
                                       cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert: the child still pays the estimate (the budget never lowers the cap that protects HTLCs)
        var child = Assert.Single(_store.Children);
        Assert.True(PackageFeerate(Load(commitment), Load(child)) >= _estimate);
    }
}