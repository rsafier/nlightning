using System.Net;
using System.Net.Sockets;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.TestUtils;

public class PortPoolUtilTests
{
    [Fact]
    public void Given_NoConfiguredBase_When_ProcessesOfOneMachineResolve_Then_TheirRangesDoNotOverlap()
    {
        // Arrange
        const string machine = "host-a";
        var processIds = Enumerable.Range(4_000, PortPoolUtil.Slots).ToList();

        // Act
        var bases = processIds.Select(pid => PortPoolUtil.ResolveBasePort(null, pid, machine)).ToList();

        // Assert: every slot once, each range inside 20000-29999 (below the ephemeral ranges)
        Assert.Equal(PortPoolUtil.Slots, bases.Distinct().Count());
        Assert.All(bases, b =>
        {
            Assert.InRange(b, PortPoolUtil.FirstSlotPort,
                           PortPoolUtil.FirstSlotPort + (PortPoolUtil.Slots - 1) * PortPoolUtil.PoolSize);
            Assert.Equal(0, (b - PortPoolUtil.FirstSlotPort) % PortPoolUtil.PoolSize);
        });
        Assert.True(bases.Max() + PortPoolUtil.PoolSize - 1 < 32_768);
    }

    [Fact]
    public void Given_ContainersWithTheSamePid_When_Resolved_Then_TheHostNameSpreadsThem()
    {
        // Act: the test process in two containers is often pid 1; their host names are the container ids
        var bases = Enumerable.Range(0, 20)
                              .Select(i => PortPoolUtil.ResolveBasePort(null, 1, $"container{i:x12}"))
                              .Distinct()
                              .Count();

        // Assert
        Assert.True(bases > 1);
    }

    [Fact]
    public void Given_SameProcess_When_ResolvedTwice_Then_TheBaseIsStable()
    {
        // Act / Assert
        Assert.Equal(PortPoolUtil.ResolveBasePort(null, 1234, "m"), PortPoolUtil.ResolveBasePort(null, 1234, "m"));
    }

    [Theory]
    [InlineData("31000", 31000)]
    [InlineData(" 1024 ", 1024)]
    [InlineData("65486", 65486)]
    public void Given_ConfiguredBase_When_Resolved_Then_ItWins(string configured, int expected)
    {
        // Act / Assert
        Assert.Equal(expected, PortPoolUtil.ResolveBasePort(configured, 1, "m"));
    }

    [Theory]
    [InlineData("80")]
    [InlineData("65487")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Given_InvalidConfiguredBase_When_Resolved_Then_ItThrows(string configured)
    {
        // Act / Assert
        var ex = Assert.Throws<InvalidOperationException>(() => PortPoolUtil.ResolveBasePort(configured, 1, "m"));
        Assert.Contains(PortPoolUtil.PortBaseEnvironmentVariable, ex.Message);
    }

    [Fact]
    public async Task Given_ThisProcess_When_GettingAPort_Then_ItIsInThisProcessRangeAndFree()
    {
        // Act
        var port = await PortPoolUtil.GetAvailablePortAsync();
        try
        {
            // Assert
            Assert.InRange(port, PortPoolUtil.BasePort, PortPoolUtil.BasePort + PortPoolUtil.PoolSize - 1);
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        finally
        {
            PortPoolUtil.ReleasePort(port);
        }
    }

    [Fact]
    public async Task Given_PortsInUseByAnotherListener_When_GettingPorts_Then_TheyAreSkipped()
    {
        // Arrange: another "process" listens on a pool port we don't hold
        var taken = new List<int>();
        var squatter = default(TcpListener);
        try
        {
            var first = await PortPoolUtil.GetAvailablePortAsync();
            PortPoolUtil.ReleasePort(first);
            squatter = new TcpListener(IPAddress.Loopback, first);
            squatter.Start();

            // Act: take every other port of the range
            for (var i = 0; i < PortPoolUtil.PoolSize - 1; i++)
            {
                try
                {
                    taken.Add(await PortPoolUtil.GetAvailablePortAsync());
                }
                catch (InvalidOperationException)
                {
                    // Another test of this process holds a port; the rest of the range is enough
                    break;
                }
            }

            // Assert
            Assert.DoesNotContain(first, taken);
            Assert.NotEmpty(taken);
        }
        finally
        {
            squatter?.Stop();
            foreach (var port in taken)
                PortPoolUtil.ReleasePort(port);
        }
    }
}