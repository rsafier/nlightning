using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Revoked;

using Application.Channels.Services;
using Application.Onchain.Resolvers.Revoked;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Protocol.Services;

/// <summary>
/// NL-479 (SP-I5, SP2-C-T3): the penalty data source finds the funding a revoked commitment spends (a retired one after
/// a splice's lock) and reads that funding's revocation log entry, never another funding's entry of the same number.
/// </summary>
public class RevokedSpliceFundingTests
{
    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    private static readonly TxId s_retiredTxId = new(Enumerable.Repeat((byte)0xD1, 32).ToArray());

    private readonly Mock<IRemoteShachainDbRepository> _shachain = new();
    private readonly Mock<IRevokedCommitmentDbRepository> _log = new();
    private readonly Mock<IChannelFundingDbRepository> _fundings = new();
    private readonly Mock<IBitcoinChainService> _chain = new();

    [Fact]
    public async Task Given_ARevokedCommitmentOfARetiredFunding_When_Loaded_Then_ThatFundingAndItsLogEntry()
    {
        // Arrange: the old funding was replaced by a locked splice; commitment 1 of the old funding is on chain
        using var pair = new RealSigningCommitmentPair(false);
        var channel = pair.Bob.Channel;
        var current = ChannelFunding.FromFundingOutput(channel.FundingOutput!)!;
        var retired = current with
        {
            FundingTxId = s_retiredTxId,
            CapacitySatoshis = 900_000,
            Status = ChannelFundingStatus.Replaced
        };
        _fundings.Setup(f => f.GetByChannelIdAsync(channel.ChannelId)).ReturnsAsync([retired, current]);
        var onRetired = new RevokedCommitmentModel(channel.ChannelId, 1, pair.Bob.State.RemoteCommit.Spec)
        {
            FundingTxId = s_retiredTxId
        };
        var onCurrent = new RevokedCommitmentModel(channel.ChannelId, 1,
                                                   new CommitmentSpec(pair.Bob.State.RemoteCommit.Spec.Holder, 253, 1,
                                                                      2, []));
        _log.Setup(l => l.GetAsync(channel.ChannelId, s_retiredTxId, 1)).ReturnsAsync(onRetired);
        _log.Setup(l => l.GetAsync(channel.ChannelId, 1)).ReturnsAsync(onCurrent);
        var commitment = SpendOf(s_retiredTxId, 0);
        ServeBlock(321, commitment);
        StoreSecrets(pair, 3);

        // Act
        var result = await CreateDataSource(channel).LoadAsync(Close(commitment.GetHash().ToBytes()),
                                                               TestContext.Current.CancellationToken);

        // Assert
        var context = Assert.IsType<RevokedCommitContext>(result.Context);
        Assert.Equal(s_retiredTxId, context.Funding!.FundingTxId);
        Assert.Equal(900_000UL, context.Funding.CapacitySatoshis);
        Assert.Same(onRetired, context.LogEntry);
        _log.Verify(l => l.GetAsync(channel.ChannelId, 1), Times.Never);
    }

    [Fact]
    public async Task Given_ARevokedCommitmentOfTheCurrentFunding_When_Loaded_Then_TheCurrentFundingsEntry()
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(false);
        var channel = pair.Bob.Channel;
        var current = ChannelFunding.FromFundingOutput(channel.FundingOutput!)!;
        _fundings.Setup(f => f.GetByChannelIdAsync(channel.ChannelId))
                 .ReturnsAsync([current, current with { FundingTxId = s_retiredTxId, Status = ChannelFundingStatus.Pending }]);
        var onCurrent = new RevokedCommitmentModel(channel.ChannelId, 1, pair.Bob.State.RemoteCommit.Spec)
        {
            FundingTxId = current.FundingTxId
        };
        _log.Setup(l => l.GetAsync(channel.ChannelId, current.FundingTxId, 1)).ReturnsAsync(onCurrent);
        var commitment = SpendOf(current.FundingTxId, current.OutputIndex);
        ServeBlock(321, commitment);
        StoreSecrets(pair, 3);

        // Act
        var result = await CreateDataSource(channel).LoadAsync(Close(commitment.GetHash().ToBytes()),
                                                               TestContext.Current.CancellationToken);

        // Assert
        var context = Assert.IsType<RevokedCommitContext>(result.Context);
        Assert.Equal(current.FundingTxId, context.Funding!.FundingTxId);
        Assert.Same(onCurrent, context.LogEntry);
    }

    private RevokedCommitDataSource CreateDataSource(ChannelModel inMemory)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(_shachain.Object);
        unitOfWork.SetupGet(u => u.RevokedCommitmentDbRepository).Returns(_log.Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(_fundings.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var provider = services.BuildServiceProvider();
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId _, out ChannelModel? channel) =>
               {
                   channel = inMemory;
                   return true;
               }));
        return new RevokedCommitDataSource(provider.GetRequiredService<IServiceScopeFactory>(), _chain.Object,
                                           new SecretStorageServiceFactory(), new Mock<IFeeService>().Object,
                                           Options.Create(new NodeOptions()),
                                           NullLogger<RevokedCommitDataSource>.Instance, memory.Object);
    }

    private void StoreSecrets(RealSigningCommitmentPair pair, ulong count)
    {
        using var store = new SecretStorageService();
        pair.Alice.Signer.AdvanceLocalCommitment(RealSigningCommitmentPair.ChannelId, count);
        for (ulong n = 0; n < count; n++)
            Assert.True(store.InsertSecret(
                            pair.Alice.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, n),
                            PerCommitmentIndex.From(n)));
        _shachain.Setup(s => s.GetByChannelIdAsync(RealSigningCommitmentPair.ChannelId)).ReturnsAsync(store.Export());
    }

    private static ChannelCloseModel Close(TxId txId) =>
        new(RealSigningCommitmentPair.ChannelId, ChannelCloseKind.RevokedCommitment, txId, 1, 321,
            new Hash(new byte[32]), DateTimeOffset.UtcNow);

    private static Transaction SpendOf(TxId fundingTxId, uint vout)
    {
        var tx = Network.Main.CreateTransaction();
        tx.Inputs.Add(new OutPoint(new uint256((byte[])fundingTxId), vout));
        tx.Outputs.Add(Money.Satoshis(1_000), new Key().PubKey.WitHash);
        return tx;
    }

    private void ServeBlock(uint height, params Transaction[] transactions)
    {
        var block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.AddRange(transactions);
        _chain.Setup(c => c.GetBlockAsync(height)).ReturnsAsync(block);
    }
}