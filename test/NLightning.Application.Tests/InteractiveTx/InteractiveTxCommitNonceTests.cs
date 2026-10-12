using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.InteractiveTx;

using Application.InteractiveTx;
using Application.InteractiveTx.Interfaces;
using Application.InteractiveTx.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using TestDoubles;

public sealed class InteractiveTxCommitNonceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Given_AHostNoncePreference_When_TxCompleteIsSent_Then_OnlyOptedInHostsBuildAndFundingNonceIsKept(
        bool wantsNonces, bool incompleteTransaction)
    {
        // Arrange: the first tx_complete can precede sufficient inputs/outputs to build a transaction.
        var channelId = InteractiveTxHarness.ChannelId;
        var negotiation = new Mock<IInteractiveTxNegotiation>();
        negotiation.SetupGet(n => n.Inputs).Returns(Array.Empty<InteractiveTxInput>());
        negotiation.SetupGet(n => n.Outputs).Returns(Array.Empty<InteractiveTxOutput>());
        negotiation.Setup(n => n.Start()).Returns(new InteractiveTxNegotiationStep(negotiation.Object,
                                              [new TxCompleteMessage(new TxCompletePayload(channelId))], false));
        var engine = new Mock<IInteractiveTxEngine>();
        engine.Setup(e => e.Create(It.IsAny<InteractiveTxSessionParameters>())).Returns(negotiation.Object);
        var builder = new Mock<IInteractiveTxBuilder>();
        var transaction = new ConstructedInteractiveTx(new TxId(new byte[32]), [], 0, [], [], 0, null);
        var build = builder.Setup(b => b.Build(It.IsAny<uint>(), It.IsAny<IReadOnlyList<InteractiveTxInput>>(),
                                               It.IsAny<IReadOnlyList<InteractiveTxOutput>>()));
        if (incompleteTransaction)
            build.Throws(new ArgumentException("No input yet"));
        else
            build.Returns(transaction);
        var fundingNonce = new FundingNonceTlv(new MusigPublicNonce(new byte[66]));
        var commitNonces = new CommitNoncesTlv(new MusigPublicNonce(new byte[66]),
                                               new MusigPublicNonce(new byte[66]));
        var host = new Mock<IInteractiveTxHost>();
        host.SetupGet(h => h.WantsCommitNonces).Returns(wantsNonces);
        host.Setup(h => h.GetSharedFundingAsync(It.IsAny<InteractiveTxTerms>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SharedFundingSpec?)null);
        host.Setup(h => h.GetLocalFundingNonce()).Returns(fundingNonce);
        host.Setup(h => h.GetLocalCommitNonces(transaction.TxId)).Returns(commitNonces);
        var node = InteractiveTxTestNode.Create("local", 0x11, engine.Object, 100_000, 50_000);
        var peer = InteractiveTxTestNode.Create("remote", 0x22, engine.Object, 100_000, 50_000);
        var driver = new InteractiveTxDriver(engine.Object, builder.Object, node.Contributor, node.Inspector,
                                              NullLogger<InteractiveTxDriver>.Instance);

        // Act: only the initiator emits the first negotiation turn from StartAsync.
        var outbound = await driver.StartAsync(node.Terms(channelId, peer, true, 1_000, false), host.Object,
                                                TestContext.Current.CancellationToken);

        // Assert: funding_nonce decoration is independent of the commitment-nonce build gate.
        var complete = Assert.IsType<TxCompleteMessage>(Assert.Single(outbound));
        Assert.Same(fundingNonce, complete.FundingNonceTlv);
        if (wantsNonces && !incompleteTransaction)
            Assert.Same(commitNonces, complete.CommitNoncesTlv);
        else
            Assert.Null(complete.CommitNoncesTlv);
        builder.Verify(b => b.Build(It.IsAny<uint>(), It.IsAny<IReadOnlyList<InteractiveTxInput>>(),
                                     It.IsAny<IReadOnlyList<InteractiveTxOutput>>()),
                       wantsNonces ? Times.Once() : Times.Never());
        host.Verify(h => h.GetLocalCommitNonces(It.IsAny<TxId>()),
                    wantsNonces && !incompleteTransaction ? Times.Once() : Times.Never());
    }
}