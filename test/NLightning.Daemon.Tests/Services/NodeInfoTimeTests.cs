using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Services;

using Daemon.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Transport.Interfaces;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// NL-885 (the 2026-10-03 Mutinynet test): <c>info</c> printed "Best Block Time: 2026-10-03T13:52:19-04:00", the UTC
/// wall time the database handed back without a kind with the local offset attached. The daemon now reports the UTC
/// instant and the CLI prints it in UTC with a Z.
/// </summary>
public class NodeInfoTimeTests
{
    private static readonly CompactPubKey s_node = new([0x02, .. Enumerable.Repeat((byte)0x21, 32)]);

    [Fact]
    public async Task Given_AStoredTimeWithoutKind_When_NodeInfoIsQueried_Then_ItIsTheUtcInstant()
    {
        // Arrange: written as DateTime.UtcNow, read back by the provider as Unspecified
        var stored = new DateTime(2026, 10, 3, 13, 52, 19, DateTimeKind.Unspecified);
        var service = CreateService(new BlockchainState(3_476_174, new Hash(new byte[32]), stored));

        // Act
        var info = await service.QueryAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 13, 52, 19, TimeSpan.Zero), info.BestBlockTime);
        Assert.Equal(TimeSpan.Zero, info.BestBlockTime!.Value.Offset);
    }

    [Fact]
    public void Given_ALocalTime_When_MadeUtc_Then_TheInstantIsKept()
    {
        // Arrange
        var local = new DateTime(2026, 10, 3, 9, 52, 19, DateTimeKind.Local);

        // Act
        var utc = NodeInfoQueryService.AsUtc(local);

        // Assert
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(new DateTimeOffset(local), utc);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(0)]
    [InlineData(9)]
    public void Given_ABestBlockTimeWithAnyOffset_When_InfoPrinted_Then_ItIsUtcWithAZ(int offsetHours)
    {
        // Arrange: 13:52:19 UTC, whatever offset it carries
        var instant = new DateTimeOffset(2026, 10, 3, 13, 52, 19, TimeSpan.Zero);
        var response = new NodeInfoIpcResponse
        {
            PubKey = s_node,
            ListeningTo = [],
            BestBlockHash = new Hash(new byte[32]),
            BestBlockTime = instant.ToOffset(TimeSpan.FromHours(offsetHours))
        };
        using var writer = new StringWriter();
        writer.NewLine = "\n";

        // Act
        new NodeInfoPrinter(writer).Print(response);

        // Assert
        Assert.Contains("  Best Block Time:   2026-10-03 13:52:19Z\n", writer.ToString());
    }

    private static NodeInfoQueryService CreateService(BlockchainState state)
    {
        var stateRepository = new Mock<IBlockchainStateDbRepository>();
        stateRepository.Setup(r => r.GetStateAsync()).ReturnsAsync(state);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.BlockchainStateDbRepository).Returns(stateRepository.Object);
        var provider = new ServiceCollection().AddScoped(_ => unitOfWork.Object).BuildServiceProvider();
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodePubKey()).Returns(s_node);
        var tcp = new Mock<ITcpService>();
        tcp.SetupGet(t => t.ListeningTo).Returns([new IPEndPoint(IPAddress.Loopback, 9735)]);
        return new NodeInfoQueryService(Options.Create(new NodeOptions()), keyManager.Object, provider, tcp.Object);
    }
}