using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.DualFunding;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-138: the dual-funded open's commitment step moves the registered channel to V1FundingSigned with the funding
/// outpoint, and the open's client subscription only learns of it through OnChannelUpdated — so the step must publish
/// the channel with UpdateChannel.
/// </summary>
public sealed class DualFundedOpenServiceCommitmentSignedTests
{
    [Fact]
    public async Task Given_TheFirstAttempt_When_OurCommitmentSignedIsCreated_Then_TheChannelIsPublished()
    {
        // Arrange: the channel is registered (accept_channel2 added it) and the guard is on, so an unpublished
        // mutation would make the lookup at the end throw
        var memory = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance)
        {
            DetectUnpublishedMutations = true
        };
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x0b, 32).ToArray());
        var peer = Peer(9);
        var channel = CreateOpenChannel(channelId, peer);
        memory.AddChannel(channel);
        var updates = new List<ChannelModel>();
        memory.OnChannelUpdated += (_, args) => updates.Add(args.Channel);

        var negotiation = new DualFundNegotiation(channelId, channelId, peer, true)
        {
            Channel = channel,
            LocalShare = LightningMoney.Satoshis(60_000),
            RemoteShare = LightningMoney.Satoshis(40_000)
        };
        var txId = TxIdOf(0x01);
        var session = new InteractiveTxSessionModel
        {
            ChannelId = channelId,
            SessionId = Guid.NewGuid(),
            Purpose = InteractiveTxPurpose.DualFund,
            IsInitiator = true,
            FeeratePerKw = 300,
            Locktime = 0,
            Inputs = [],
            Outputs =
            [
                new InteractiveTxOutput(1, InteractiveTxParty.Local, LightningMoney.Satoshis(100_000),
                                        DualFundedOpenService.GetFundingScript(channel), true)
            ],
            LocalContribution = InteractiveTxContribution.Empty,
            State = InteractiveTxSessionState.AwaitingCommitmentSigned,
            CreatedAt = DateTimeOffset.UtcNow,
            ConstructedTx = new ConstructedInteractiveTx(txId, [], 0, [],
                                                         [
                                                             new InteractiveTxOutput(
                                                                 1, InteractiveTxParty.Local,
                                                                 LightningMoney.Satoshis(100_000),
                                                                 DualFundedOpenService.GetFundingScript(channel), true)
                                                         ], 1000, 0)
        };

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);
        unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository)
                  .Returns(WatchedTransactionsOf(returningStored: false));
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(new Mock<IWatchedOutpointDbRepository>().Object);

        var signer = new Mock<ILightningSigner>();
        signer.Setup(s => s.SignChannelTransaction(channelId, txId, It.IsAny<SignedTransaction>()))
              .Returns(new CompactSignature(new byte[64]));

        var service = CreateService(memory, unitOfWork.Object, signer.Object);

        // Act
        await service.CreateCommitmentSignedAsync(negotiation, session, unitOfWork.Object);

        // Assert: the channel reached V1FundingSigned with the funding outpoint and the subscribers were told
        Assert.Equal(ChannelState.V1FundingSigned, channel.State);
        Assert.Equal(txId, channel.FundingOutput!.TransactionId);
        var update = Assert.Single(updates);
        Assert.Same(channel, update);
        Assert.True(memory.TryGetChannel(channelId, out _));
    }

    private static DualFundedOpenService CreateService(IChannelMemoryRepository memory, IUnitOfWork unitOfWork,
                                                       ILightningSigner signer)
    {
        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork);
        return new DualFundedOpenService(new Mock<IChannelLockProvider>().Object, memory,
                                         new Mock<IChannelOpenValidator>().Object,
                                         new Mock<ICommitmentTransactionBuilder>().Object,
                                         new Mock<ICommitmentTransactionModelFactory>().Object,
                                         new Mock<IFeeService>().Object, signer,
                                         NullLogger<DualFundedOpenService>.Instance,
                                         new Mock<IMessageFactory>().Object, services.BuildServiceProvider(),
                                         new Mock<ISha256>().Object, Options.Create(new NodeOptions()),
                                         Options.Create(new GossipOptions()),
                                         Options.Create(new DualFundingOptions()));
    }

    private static IWatchedTransactionDbRepository WatchedTransactionsOf(bool returningStored)
    {
        var repository = new Mock<IWatchedTransactionDbRepository>();
        repository.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync(returningStored
                                    ? new WatchedTransactionModel(new ChannelId(new byte[32]), TxIdOf(0xff), 6)
                                    : null);
        return repository.Object;
    }

    private static ChannelModel CreateOpenChannel(ChannelId channelId, CompactPubKey peer)
    {
        var localKey = Peer(3);
        var remoteKey = Peer(4);
        var keySet = new ChannelKeySetModel(0, localKey, localKey, localKey, localKey, localKey, localKey);
        return new ChannelModel(new ChannelParams(), channelId, null,
                                new FundingOutputInfo(LightningMoney.Satoshis(100_000), localKey, remoteKey), true,
                                null, null, LightningMoney.Satoshis(60_000), keySet, 0, 0,
                                LightningMoney.Satoshis(40_000), keySet, 0, peer, 0, ChannelState.V1Opening,
                                ChannelVersion.V1);
    }

    private static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static CompactPubKey Peer(byte seed)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = seed;
        return new CompactPubKey(bytes);
    }
}