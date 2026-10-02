using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Channels.Safety;
using Application.Channels.Safety.Interfaces;
using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Local;
using Channels.Harness;
using Channels.Splicing;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-496: <see cref="LocalCommitResolver"/> on our commitment of a <b>splice</b> funding with an HTLC in flight (BOLT 5
/// across fundings, NL-479). Alice splices in, offers an HTLC (signed on both fundings in one batch), the channel fails
/// and the splice confirms instead of the funding it spends; the failure service broadcasts our commitment on the
/// splice funding (SP2-C-T2). The resolver finds that commitment on the pending funding (its stored local slot, as
/// <c>ChannelStateDbRepository</c> writes it per pending funding) and, at the HTLC's expiry, builds an HTLC-timeout
/// transaction with the peer's HTLC signature on that funding: the transaction spends the commitment's HTLC output and
/// its witness verifies. Real engines and signers (<see cref="SpliceHarness"/> with the production splice state port).
/// </summary>
public sealed class LocalCommitSpliceHtlcTests
{
    private const uint CltvExpiry = 700;

    [Fact]
    public async Task Given_OurCommitmentOnAConfirmedSpliceWithAnOfferedHtlc_When_TheHtlcExpires_Then_ItsTimeoutTransactionVerifies()
    {
        // Arrange: a signed, pending splice and an HTLC offered by Alice on both fundings
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var spliceTxId = result.SpliceTxId!.Value;
        await OfferAsync(harness);
        var channel = harness.Alice.Node.Channel;
        Assert.Single(channel.Commitments!.LocalCommit.Spec.Htlcs);
        Assert.NotNull(channel.Commitments.LocalCommit.SignaturesFor(spliceTxId));

        // The channel fails and the splice confirms: our commitment on the splice funding is broadcast
        channel.UpdateState(ChannelState.Failed);
        var outcome = await CreateFailureService(harness.Alice)
                         .BroadcastOnSpliceAsync(channel.ChannelId, spliceTxId, TestContext.Current.CancellationToken);
        var commitmentRow = Assert.Single(harness.Alice.Broadcasts,
                                          b => b.Purpose == BroadcastPurpose.LocalCommitment);
        Assert.Equal(outcome.CommitmentTxId, commitmentRow.TransactionId);
        var commitment = Transaction.Load(commitmentRow.RawTransaction, Network.Main);
        var close = new ChannelCloseModel(channel.ChannelId, ChannelCloseKind.LocalCommitment,
                                          commitmentRow.TransactionId, channel.Commitments.LocalCommit.Number,
                                          TwoNodeHarness.BlockHeight + 2, Hash.Empty, DateTimeOffset.UtcNow);
        using var provider = CreateResolverServices(harness.Alice, channel);
        var resolver = provider.GetRequiredService<LocalCommitResolver>();

        // Act: the first round at the HTLC's expiry
        var actions = await resolver.ResolveAsync(close, [], CltvExpiry, TestContext.Current.CancellationToken);

        // Assert
        AssertHtlcTimeoutVerifies(actions, commitment, commitmentRow.TransactionId);
    }

    /// <summary>
    /// The same after the splice locked (the channel runs on the splice funding, its funding key rotated): our latest
    /// commitment spends the splice output, and the resolver's HTLC-timeout carries the peer's HTLC signature of that
    /// commitment and verifies.
    /// </summary>
    [Fact]
    public async Task Given_OurCommitmentOnALockedSpliceWithAnOfferedHtlc_When_TheHtlcExpires_Then_ItsTimeoutTransactionVerifies()
    {
        // Arrange: a locked splice, then an HTLC offered by Alice on the splice funding alone
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var spliceTxId = result.SpliceTxId!.Value;
        await harness.ConfirmAsync(spliceTxId, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);
        var channel = harness.Alice.Node.Channel;
        Assert.Equal(spliceTxId, channel.FundingOutput!.TransactionId);
        Assert.Empty(channel.Commitments!.PendingFundings);
        await OfferAsync(harness);
        Assert.Single(channel.Commitments!.LocalCommit.Spec.Htlcs);

        // The channel fails: our latest commitment goes on chain
        channel.UpdateState(ChannelState.Failed);
        var services = harness.Alice.Node.Services;
        var signed = new LocalCommitmentBroadcastBuilder(services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                                         services.GetRequiredService<ICommitmentTransactionBuilder>(),
                                                         services.GetRequiredService<ILightningSigner>())
           .Build(channel);
        var commitment = Transaction.Load(signed.Transaction.RawTxBytes, Network.Main);
        Assert.Equal(new uint256((byte[])spliceTxId), Assert.Single(commitment.Inputs).PrevOut.Hash);
        var close = new ChannelCloseModel(channel.ChannelId, ChannelCloseKind.LocalCommitment, signed.Transaction.TxId,
                                          signed.CommitmentNumber, TwoNodeHarness.BlockHeight + 4, Hash.Empty,
                                          DateTimeOffset.UtcNow);
        using var provider = CreateResolverServices(harness.Alice, channel);
        var resolver = provider.GetRequiredService<LocalCommitResolver>();

        // Act
        var actions = await resolver.ResolveAsync(close, [], CltvExpiry, TestContext.Current.CancellationToken);

        // Assert
        AssertHtlcTimeoutVerifies(actions, commitment, signed.Transaction.TxId);
    }

    private static async Task OfferAsync(SpliceHarness harness)
    {
        var hash = TwoNodeHarness.Hash(TwoNodeHarness.Preimage(1));
        await harness.Alice.Node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId,
                                                           LightningMoney.MilliSatoshis(20_000_000), hash, CltvExpiry,
                                                           new(TwoNodeHarness.Onion), null, HtlcOrigin.Local(hash),
                                                           TestContext.Current.CancellationToken);
        await harness.PumpAsync();
    }

    /// <summary>Rows for the commitment's outputs, and one HTLC-timeout spending its HTLC output, which verifies.</summary>
    private static void AssertHtlcTimeoutVerifies(IReadOnlyList<OutputResolverAction> actions, Transaction commitment,
                                                  TxId commitmentTxId)
    {
        var rows = actions.OfType<UpsertOutputAction>().Select(a => a.Output).ToList();
        var htlcRow = Assert.Single(rows, r => r.TransactionId == commitmentTxId
                                            && r.Descriptor == OutputDescriptorKind.LocalOfferedHtlc);
        var timeout = Assert.Single(actions.OfType<BroadcastAction>(),
                                    a => a.Transaction.Purpose == BroadcastPurpose.HtlcTransaction);
        var tx = Transaction.Load(timeout.Transaction.RawTransaction, Network.Main);
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(new OutPoint(commitment.GetHash(), htlcRow.OutputIndex), input.PrevOut);
        Assert.Equal(CltvExpiry, (uint)tx.LockTime.Value);
        var spent = commitment.Outputs[(int)htlcRow.OutputIndex];
        Assert.True(tx.Inputs.AsIndexedInputs().Single().VerifyScript(spent, out var error),
                    $"our HTLC-timeout on the splice commitment does not verify: {error}");
    }

    /// <summary>
    /// The resolver's services over the harness node's signer, with a unit of work that loads Alice's channel and
    /// answers the splice funding's local commitment slot as <c>ChannelStateDbRepository</c> stores it (the commitment
    /// at the current number on that funding, with the peer's signatures from the batch).
    /// </summary>
    private static ServiceProvider CreateResolverServices(SpliceNode node, ChannelModel channel)
    {
        var fundingRows = new Mock<IChannelFundingDbRepository>();
        fundingRows.Setup(f => f.GetByChannelIdAsync(channel.ChannelId))
                   .ReturnsAsync(() => node.FundingRows.Committed.Values.ToList());
        fundingRows.Setup(f => f.GetLocalCommitmentAsync(channel.ChannelId, It.IsAny<TxId>()))
                   .ReturnsAsync((ChannelId _, TxId fundingTxId) =>
                   {
                       var commitments = channel.Commitments!;
                       var funding = commitments.PendingFundings.FirstOrDefault(f => f.FundingTxId == fundingTxId);
                       return funding is null || commitments.LocalCommit.SignaturesFor(fundingTxId) is not { } signatures
                                  ? null
                                  : new LocalCommit(commitments.LocalCommit.Number,
                                                    ChannelCommitments.SpecFor(commitments.LocalCommit.Spec, funding),
                                                    signatures);
                   });
        var channels = new Mock<IChannelDbRepository>();
        channels.Setup(c => c.GetByIdAsync(channel.ChannelId)).ReturnsAsync(channel);
        var unitOfWork = new Mock<IUnitOfWork> { DefaultValue = DefaultValue.Mock };
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channels.Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundingRows.Object);

        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(SpliceHarness.FeeratePerKw));
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(SpliceHarness.FeeratePerKw));
        var destination = new Key().PubKey.WitHash.ScriptPubKey.ToBytes();
        var destinations = new Mock<ISweepDestinationProvider>();
        destinations.Setup(d => d.GetDestinationScriptAsync(It.IsAny<CancellationToken>())).ReturnsAsync(destination);
        destinations.Setup(d => d.GetDestinationScriptAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(destination);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(node.Node.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(feeService.Object);
        services.AddSingleton(destinations.Object);
        services.AddScoped(_ => unitOfWork.Object);
        services.AddLocalCommitResolutionServices();
        return services.BuildServiceProvider();
    }

    private static ChannelFailureService CreateFailureService(SpliceNode node)
    {
        var services = node.Node.Services;
        return new ChannelFailureService(node.Node.ChainMonitor.Object, new Mock<IChannelErrorSender>().Object,
                                         services.GetRequiredService<IChannelLockProvider>(),
                                         services.GetRequiredService<IChannelMemoryRepository>(),
                                         new LocalCommitmentBroadcastBuilder(
                                             services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                             services.GetRequiredService<ICommitmentTransactionBuilder>(),
                                             services.GetRequiredService<ILightningSigner>()),
                                         services.GetRequiredService<ILightningSigner>(),
                                         NullLogger<ChannelFailureService>.Instance,
                                         services.GetRequiredService<IServiceScopeFactory>(), null,
                                         services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                         services.GetRequiredService<ICommitmentTransactionBuilder>());
    }
}