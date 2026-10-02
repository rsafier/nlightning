using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Managers;

using Application.Channels.Handlers;
using Application.Channels.Managers;
using Application.Channels.Services;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-617: a channel that failed (or went on chain) before its funding reached its depth still had its funding
/// confirmed; the accounting feed records <c>ChannelFunded</c> for it, once, so its force close leaves a channel
/// bucket that received the balance.
/// </summary>
public class ChannelManagerLateFundingTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<IChannelMemoryRepository> _memoryRepository = new();
    private readonly Mock<IAccountingEventDbRepository> _events = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new() { DefaultValue = DefaultValue.Mock };
    private readonly List<AccountingEventModel> _added = [];
    private readonly HashSet<string> _existing = [];
    private readonly List<IChannelMessage> _raised = [];
    private readonly TaskCompletionSource _saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ChannelModel? _channel;

    public ChannelManagerLateFundingTests()
    {
        _memoryRepository
           .Setup(r => r.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel>.IsAny!))
           .Returns(new TryGetChannelDelegate((ChannelId id, out ChannelModel channel) =>
            {
                channel = _channel is not null && _channel.ChannelId == id ? _channel : null!;
                return channel is not null;
            }));
        _memoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        _events.Setup(e => e.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((string key, CancellationToken _) => _existing.Contains(key));
        _events.Setup(e => e.Add(It.IsAny<AccountingEventModel>())).Callback<AccountingEventModel>(_added.Add);
        _unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(_events.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => _saved.TrySetResult()).Returns(Task.CompletedTask);
    }

    private delegate bool TryGetChannelDelegate(ChannelId channelId, out ChannelModel channel);

    [Theory]
    [InlineData(ChannelState.Failed)]
    [InlineData(ChannelState.OnchainResolving)]
    public async Task Given_AChannelFailedBeforeItsFunding_When_TheFundingConfirms_Then_ChannelFundedIsRecorded(
        ChannelState state)
    {
        // Arrange
        _channel = CreateChannel(state);
        var manager = CreateChannelManager();

        // Act
        RaiseFundingConfirmed(_channel, 812, 7);
        await _saved.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert: the funding's block and position, the channel's state and messages untouched
        var funded = Assert.Single(_added);
        Assert.Equal(AccountingEventKind.ChannelFunded, funded.Kind);
        Assert.Equal(AccountingEventKeys.ChannelFunded(_channel.ChannelId, _channel.FundingOutput!.TransactionId!.Value),
                     funded.EventKey);
        Assert.Equal(812u, funded.BlockHeight);
        Assert.Equal(new ShortChannelId(812, 7, 0), funded.ShortChannelId);
        Assert.Equal(state, _channel.State);
        Assert.Empty(_raised);
        GC.KeepAlive(manager);
    }

    [Fact]
    public async Task Given_TheLateFundingAlreadyRecorded_When_TheConfirmationIsRaisedAgain_Then_NothingIsAdded()
    {
        // Arrange
        _channel = CreateChannel(ChannelState.Failed);
        _existing.Add(AccountingEventKeys.ChannelFunded(_channel.ChannelId, _channel.FundingOutput!.TransactionId!.Value));
        var checkedKey = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _events.Setup(e => e.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .Callback(() => checkedKey.TrySetResult())
               .ReturnsAsync((string key, CancellationToken _) => _existing.Contains(key));
        var manager = CreateChannelManager();

        // Act
        RaiseFundingConfirmed(_channel, 812, 7);
        await checkedKey.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_added);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
        GC.KeepAlive(manager);
    }

    [Fact]
    public async Task Given_AFailedChannel_When_AnotherTransactionOfItConfirms_Then_NothingIsRecorded()
    {
        // Arrange: a confirmation of a transaction that is not the channel's funding
        _channel = CreateChannel(ChannelState.Failed);
        var manager = CreateChannelManager();
        var other = new WatchedTransactionModel(_channel.ChannelId, new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray()),
                                                1);
        other.SetHeightAndIndex(812, 7);
        other.MarkAsCompleted();

        // Act
        _blockchainMonitor.Raise(m => m.OnTransactionConfirmed += null, new TransactionConfirmedEventArgs(other, 812));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_added);
        GC.KeepAlive(manager);
    }

    private void RaiseFundingConfirmed(ChannelModel channel, uint height, uint index)
    {
        var watchedTransaction = new WatchedTransactionModel(channel.ChannelId,
                                                             channel.FundingOutput!.TransactionId!.Value, 3);
        watchedTransaction.SetHeightAndIndex(height, index);
        watchedTransaction.MarkAsCompleted();
        _blockchainMonitor.Raise(m => m.OnTransactionConfirmed += null,
                                 new TransactionConfirmedEventArgs(watchedTransaction, height + 2));
    }

    private ChannelManager CreateChannelManager()
    {
        var signer = new Mock<ILightningSigner>();
        var serviceProvider = new FakeServiceProvider();
        serviceProvider.AddService(typeof(IUnitOfWork), _unitOfWork.Object);
        serviceProvider.AddService(typeof(ChannelDomainEventQueue), new ChannelDomainEventQueue());
        serviceProvider.AddService(typeof(FundingConfirmedMessageHandler),
                                   new FundingConfirmedMessageHandler(_memoryRepository.Object, signer.Object,
                                                                      new Mock<ILogger<FundingConfirmedMessageHandler>>()
                                                                         .Object,
                                                                      new Mock<IMessageFactory>().Object,
                                                                      _unitOfWork.Object));

        var manager = new ChannelManager(_blockchainMonitor.Object, new ChannelLockProvider(), _memoryRepository.Object,
                                         new Mock<ILogger<ChannelManager>>().Object, signer.Object, serviceProvider);
        manager.OnResponseMessageReady += (_, args) => _raised.Add(args.ResponseMessage);
        return manager;
    }

    private static ChannelModel CreateChannel(ChannelState state)
    {
        var fundingAmount = LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(fundingAmount, s_pubKey, s_pubKey)
        {
            TransactionId = new TxId(Enumerable.Repeat((byte)0x42, 32).ToArray()),
            Index = 0
        };
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                                     LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                                     LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        var commitmentNumber = new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256());

        return new ChannelModel(channelConfig, new ChannelId(Enumerable.Repeat((byte)0x41, 32).ToArray()),
                                commitmentNumber, fundingOutput, true, null, null, LightningMoney.Zero, keySet, 0, 0,
                                fundingAmount, keySet, 0, s_pubKey, 0, state, ChannelVersion.V1);
    }
}