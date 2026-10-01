using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.DualFunding;
using Application.Node.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Moq;

/// <summary>
/// The dual-funded open's drain gate for a peer's RBF (NL-592): while the node drains for its shutdown,
/// <c>tx_init_rbf</c> is answered with <c>tx_abort</c> (BOLT 2 lets the recipient refuse for any reason), like the
/// splice path does. The first pass only refused our own <c>bumpopen</c> (IPC) and a peer's open.
/// </summary>
public sealed class DualFundedOpenServiceDrainTests
{
    [Fact]
    public async Task Given_TheNodeDraining_When_ThePeerRbfsTheOpen_Then_ItIsAborted()
    {
        // Arrange
        var drain = new NodeDrainState();
        Assert.True(drain.TryBeginDrain());
        var services = new ServiceCollection();
        services.AddSingleton<INodeDrainState>(drain);
        var service = CreateService(services.BuildServiceProvider());

        var channelId = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        var negotiation = new DualFundNegotiation(channelId, channelId, Peer(9), true);
        var message = new TxInitRbfMessage(new TxInitRbfPayload(channelId, 300, 120));

        // Act
        var decision = await service.DecideRbfAsync(negotiation, message);

        // Assert: refused with tx_abort, and nothing about the negotiation moved
        Assert.Null(decision.Terms);
        Assert.Contains("shutting down", decision.RejectReason);
    }

    [Fact]
    public async Task Given_TheNodeNotDraining_When_ThePeerRbfsTheOpen_Then_TheRefusalsAreCheckedInstead()
    {
        // Arrange: not draining; the service has no policy acceptance wired, so the refusal comes from further in —
        // the point of the test is that the drain gate is not the one answering
        var services = new ServiceCollection();
        var service = CreateService(services.BuildServiceProvider());

        var channelId = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        var negotiation = new DualFundNegotiation(channelId, channelId, Peer(9), true);
        var message = new TxInitRbfMessage(new TxInitRbfPayload(channelId, 300, 120));

        // Act
        var decision = await service.DecideRbfAsync(negotiation, message);

        // Assert: not the drain refusal
        if (decision.RejectReason is not null)
            Assert.DoesNotContain("shutting down", decision.RejectReason);
    }

    private static DualFundedOpenService CreateService(IServiceProvider serviceProvider)
    {
        return new DualFundedOpenService(new Mock<IChannelLockProvider>().Object,
                                         new Mock<IChannelMemoryRepository>().Object,
                                         new Mock<IChannelOpenValidator>().Object,
                                         new Mock<ICommitmentTransactionBuilder>().Object,
                                         new Mock<ICommitmentTransactionModelFactory>().Object,
                                         new Mock<IFeeService>().Object,
                                         new Mock<ILightningSigner>().Object,
                                         NullLogger<DualFundedOpenService>.Instance,
                                         new Mock<IMessageFactory>().Object, serviceProvider,
                                         new Mock<ISha256>().Object,
                                         Options.Create(new NodeOptions()),
                                         Options.Create(new GossipOptions()),
                                         Options.Create(new DualFundingOptions()));
    }

    private static CompactPubKey Peer(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }
}